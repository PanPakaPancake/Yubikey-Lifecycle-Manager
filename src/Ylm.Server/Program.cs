using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Ylm.Contracts;
using Ylm.Server.Agents;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Inventory;
using Ylm.Server.Profiles;
using Ylm.Server.PublicApi;
using Ylm.Server.Registration;
using Ylm.Server.Reporting;
using Ylm.Server.Secrets;
using Ylm.Server.Security;
using Ylm.Server.Setup;
using Ylm.Server.Tenants;
using Ylm.Server.Users;

// Container health check without curl: `dotnet Ylm.Server.dll --healthcheck`.
if (args.Contains("--healthcheck"))
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var res = await http.GetAsync("http://localhost:8080/healthz");
        return res.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception) { return 1; }
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<YlmOptions>(builder.Configuration.GetSection(YlmOptions.Section));
var ylm = builder.Configuration.GetSection(YlmOptions.Section).Get<YlmOptions>() ?? new YlmOptions();
var dataDir = Path.GetFullPath(ylm.DataDirectory, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDir);

builder.Services.AddDbContext<YlmDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Postgres") ?? "Host=localhost;Database=ylm;Username=ylm;Password=ylm"));

builder.Services.AddDataProtection()
    .SetApplicationName("YubikeyLifecycleManager")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient("graph", c => c.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddSingleton<ISecretStore>(sp => new FileSecretStore(Path.Combine(dataDir, "secrets"), sp.GetRequiredService<IDataProtectionProvider>()));
builder.Services.AddSingleton<AppCertificateService>();
builder.Services.AddSingleton<IGraphClientFactory, GraphClientFactory>();
builder.Services.AddSingleton<AgentRegistry>();
builder.Services.AddSingleton<AgentGateway>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<PinLetterStore>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<PlatformSignIn>();
builder.Services.AddScoped<LocalAuthService>();
builder.Services.AddSingleton<Ylm.Server.Updates.UpdateService>();
builder.Services.AddHttpClient("updates", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddMemoryCache();
builder.Services.AddScoped<SetupService>();
builder.Services.AddSingleton<IEntraBootstrap, EntraBootstrap>();
builder.Services.AddSingleton<SetupSignInStore>();
builder.Services.AddScoped<TenantCheckService>();
builder.Services.AddScoped<SyncService>();
builder.Services.AddScoped<KeyService>();
builder.Services.AddScoped<RegistrationService>();
builder.Services.AddScoped<BatchService>();
builder.Services.AddScoped<SharedKeyService>();
builder.Services.AddScoped<Ylm.Server.Integrations.WebhookService>();
builder.Services.AddScoped<Ylm.Server.Integrations.ReportMailService>();
builder.Services.AddSingleton<Ylm.Server.Integrations.IMailSender, Ylm.Server.Integrations.SmtpMailSender>();
builder.Services.AddHttpClient(Ylm.Server.Integrations.WebhookService.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<Ylm.Server.Integrations.IntegrationJobs>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Ylm.Server.Integrations.IntegrationJobs>());
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddHostedService<BackgroundJobs>();

builder.Services.AddSingleton<IConfigureOptions<OpenIdConnectOptions>, EntraOidcOptionsSetup>();
builder.Services.AddAuthentication(AuthSchemes.Cookie)
    .AddCookie(AuthSchemes.Cookie, o =>
    {
        o.Cookie.Name = "ylm.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(10);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = 401;
            else ctx.Response.Redirect("/login?returnUrl=" + Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString));
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(AuthSchemes.Entra, _ => { })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, _ => { })
    .AddScheme<AuthenticationSchemeOptions, AgentTokenAuthenticationHandler>(AuthSchemes.Agent, _ => { });

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthSchemes.Cookie)
        .RequireAuthenticatedUser().RequireClaim(YlmClaims.UserId).Build())
    .AddPolicy("ApiKey", p => p.AddAuthenticationSchemes(AuthSchemes.ApiKey).RequireAuthenticatedUser().RequireClaim(YlmClaims.ApiKeyId));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy(AgentEndpoints.PairingRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddSignalR(o =>
{
    o.MaximumParallelInvocationsPerClient = 2;
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
}).AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi(PublicApiEndpoints.GroupName, o =>
{
    o.ShouldInclude = d => d.GroupName == PublicApiEndpoints.GroupName;
    o.AddDocumentTransformer((doc, _, _) =>
    {
        doc.Info = new OpenApiInfo
        {
            Title = "Yubikey Lifecycle Manager – API",
            Version = "v1",
            Description = "Lesender Zugriff auf das YubiKey-Inventar. Einzige Schreibfunktion: die ToDo-Liste (Personen, die einen Key bekommen sollen), nur mit ToDo-Schreibrecht am Schlüssel. Authentifizierung mit API-Schlüssel im Header X-Api-Key.",
        };
        doc.Components ??= new OpenApiComponents();
        doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        doc.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = ApiKeyAuthenticationHandler.HeaderName,
            Description = "API-Schlüssel des Tenants (Tenant → API-Schlüssel).",
        };
        doc.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [] }];
        return Task.CompletedTask;
    });
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<CsrfHeaderMiddleware>();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapAuth(app.Environment, ylm);
app.MapLocalAuth();
Ylm.Server.Updates.UpdateEndpoints.MapUpdates(app);
app.MapSetup();
app.MapUsers();
app.MapRoles();
app.MapTenants();
app.MapKeys();
app.MapKeyBulk();
app.MapProfiles();
app.MapCustomFields();
app.MapGroupProfiles();
app.MapAgents();
app.MapRegistration();
app.MapBatches();
app.MapSharedKeys();
app.MapApprovals();
app.MapTodos();
Ylm.Server.Integrations.IntegrationEndpoints.MapIntegrations(app);
app.MapReporting();
app.MapAudit();
app.MapPublicApi();
app.MapApiKeyManagement();
app.MapHub<AgentHub>(AgentProtocol.HubPath);
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapScalarApiReference("/api/docs", o => o.WithTitle("YLM Lese-API").WithOpenApiRoutePattern("/openapi/{documentName}.json")).AllowAnonymous();
app.MapFallback("/api/{**rest}", () => Results.Json(new ApiError("not_found", "Unbekannter Endpunkt."), statusCode: 404));
app.MapFallbackToFile("index.html");

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<YlmDbContext>();
    await db.Database.MigrateAsync();
    if (!await db.Profiles.AnyAsync())
    {
        db.Profiles.Add(ProfileEndpoints.CreateStandard());
        await db.SaveChangesAsync();
    }
    await scope.ServiceProvider.GetRequiredService<SetupService>().EnsureSetupTokenAsync();
}

await app.RunAsync();
return 0;

public partial class Program;
