using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Secrets;

namespace Ylm.Server.Security;

public static class AuthSchemes
{
    public const string Cookie = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string Entra = "Entra";
    public const string ApiKey = "ApiKey";
    public const string Agent = "AgentToken";
}

/// <summary>Configures the Entra OpenID Connect login from the installation settings stored in the database.</summary>
public sealed class EntraOidcOptionsSetup(IServiceScopeFactory scopes, IOptions<YlmOptions> ylm, ILogger<EntraOidcOptionsSetup> logger) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(OpenIdConnectOptions options) => Configure(Options.DefaultName, options);

    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != AuthSchemes.Entra) return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YlmDbContext>();
        InstallationSettings? settings = null;
        try { settings = db.Installation.AsNoTracking().FirstOrDefault(); }
        catch (Exception e) { logger.LogWarning("Entra-Anmeldung: Einstellungen nicht lesbar ({Message}).", e.Message); }

        var tenant = settings?.HomeTenantId?.ToString() ?? "organizations";
        options.Authority = $"{ylm.Value.LoginAuthority.TrimEnd('/')}/{tenant}/v2.0";
        options.ClientId = settings?.ClientId?.ToString() ?? "00000000-0000-0000-0000-000000000000";
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.ResponseMode = OpenIdConnectResponseMode.FormPost;
        options.CallbackPath = "/signin-oidc";
        options.SignInScheme = AuthSchemes.Cookie;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.MapInboundClaims = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";
        options.Events = new OpenIdConnectEvents
        {
            OnAuthorizationCodeReceived = async ctx =>
            {
                // Redeem the code with a certificate-based client assertion (no client secret).
                var certs = ctx.HttpContext.RequestServices.GetRequiredService<AppCertificateService>();
                var cert = await certs.GetAsync() ?? throw new InvalidOperationException("App certificate missing");
                var config = await ctx.Options.ConfigurationManager!.GetConfigurationAsync(ctx.HttpContext.RequestAborted);
                ctx.TokenEndpointRequest!.ClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
                ctx.TokenEndpointRequest.ClientAssertion = ClientAssertion.Create(cert, ctx.Options.ClientId!, config.TokenEndpoint);
            },
            OnTokenValidated = async ctx =>
            {
                var signIn = ctx.HttpContext.RequestServices.GetRequiredService<PlatformSignIn>();
                var principal = await signIn.MapEntraPrincipalAsync(ctx.Principal!);
                if (principal is null)
                {
                    ctx.HandleResponse();
                    ctx.Response.Redirect("/login?error=" + Uri.EscapeDataString(signIn.LastError ?? "denied")
                        + (signIn.LastAmr is { Length: > 0 } amr ? "&amr=" + Uri.EscapeDataString(amr) : ""));
                    return;
                }
                ctx.Principal = principal;
            },
            OnRemoteFailure = ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.Redirect("/login?error=remote_failure");
                return Task.CompletedTask;
            },
        };
    }
}

public static class ClientAssertion
{
    public static string Create(System.Security.Cryptography.X509Certificates.X509Certificate2 cert, string clientId, string audience)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", clientId), new Claim("jti", Guid.NewGuid().ToString())]),
            NotBefore = now,
            IssuedAt = now,
            Expires = now.AddMinutes(5),
            SigningCredentials = new X509SigningCredentials(cert),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}

/// <summary>Maps an Entra login to a platform user (Owner, Operator, Helpdesk, Auditor).</summary>
public sealed class PlatformSignIn(YlmDbContext db, AuditService audit, IOptions<YlmOptions> options, ILogger<PlatformSignIn> logger)
{
    public string? LastError { get; private set; }
    /// <summary>Anmeldemethoden laut Token (amr) bei abgelehnter Anmeldung, für einen verständlichen Hinweis.</summary>
    public string? LastAmr { get; private set; }

    public async Task<ClaimsPrincipal?> MapEntraPrincipalAsync(ClaimsPrincipal entra)
    {
        var settings = await db.Installation.FirstOrDefaultAsync();
        var tid = entra.FindFirstValue("tid");
        var oidValue = entra.FindFirstValue("oid");
        var upn = entra.FindFirstValue("preferred_username") ?? entra.FindFirstValue("upn") ?? entra.FindFirstValue("email") ?? "";
        var name = entra.FindFirstValue("name") ?? upn;

        if (settings?.HomeTenantId is null || !Guid.TryParse(tid, out var tenantId) || tenantId != settings.HomeTenantId)
        {
            return await DenyAsync("wrong_tenant", upn, "Anmeldung nur mit Konten aus dem Heim-Tenant der Installation.");
        }
        if (!Guid.TryParse(oidValue, out var oid)) return await DenyAsync("invalid_token", upn, "Token ohne Objekt-ID.");
        if (!settings.EntraSsoEnabled) return await DenyAsync("sso_disabled", upn, "Die Anmeldung mit Entra ist abgeschaltet. Bitte mit Passkey anmelden.");

        if (options.Value.RequirePhishingResistantSignIn)
        {
            var amr = entra.FindAll("amr").Select(c => c.Value).ToList();
            // Einträge dürfen Komma-Listen sein (eine Umgebungsvariable, YLM_ACCEPTED_AMR); leer = Standard "fido".
            var accepted = options.Value.PhishingResistantAmrValues
                .SelectMany(v => (v ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .DefaultIfEmpty("fido");
            if (!amr.Intersect(accepted, StringComparer.OrdinalIgnoreCase).Any())
            {
                LastAmr = string.Join(",", amr.Where(a => a.Length <= 20 && a.All(char.IsAsciiLetterOrDigit)).Take(8));
                return await DenyAsync("not_phishing_resistant", upn,
                    $"Anmeldung muss passwortlos mit Sicherheitsschlüssel erfolgen (amr: {string.Join(",", amr)}).");
            }
        }

        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.EntraObjectId == oid && u.LoginKind == UserLoginKind.Entra)
                   ?? await db.PlatformUsers.FirstOrDefaultAsync(u => u.EntraObjectId == null && u.LoginKind == UserLoginKind.Entra && u.Upn.ToLower() == upn.ToLower());

        if (user is null && !await db.PlatformUsers.AnyAsync() && settings.BootstrapOwnerUpn is { } bootstrap
            && string.Equals(bootstrap, upn, StringComparison.OrdinalIgnoreCase))
        {
            user = new PlatformUser { Upn = upn, DisplayName = name, Role = PlatformRole.Owner, AllTenants = true };
            db.PlatformUsers.Add(user);
            audit.Add(AuditService.User(user), AuditActions.UserCreated, target: upn, details: new { role = "Owner", bootstrap = true });
        }

        if (user is null || !user.IsActive)
            return await DenyAsync("not_registered", upn, "Ihr Konto ist für die Plattform nicht freigeschaltet. Bitte wenden Sie sich an einen Owner.");

        user.EntraObjectId = oid;
        user.DisplayName = name;
        user.Upn = upn;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        audit.Add(AuditService.User(user), AuditActions.Login, details: new { method = "entra" });
        await db.SaveChangesAsync();
        return BuildPrincipal(user, AuthSchemes.Entra);
    }

    private async Task<ClaimsPrincipal?> DenyAsync(string code, string upn, string message)
    {
        LastError = code;
        logger.LogWarning("Login denied for {Upn}: {Message}", upn, message);
        await audit.WriteAsync(new AuditActor(ActorType.User, null, upn), AuditActions.LoginDenied, details: new { code, message });
        return null;
    }

    public static ClaimsPrincipal BuildPrincipal(PlatformUser user, string authType) =>
        new(new ClaimsIdentity(
        [
            new Claim(YlmClaims.UserId, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim("upn", user.Upn),
        ], authType, ClaimTypes.Name, ClaimTypes.Role));
}

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, YlmDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string HeaderName = "X-Api-Key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values)) return AuthenticateResult.NoResult();
        var raw = values.ToString();
        // Format: ylm_<prefix>_<secret>
        var parts = raw.Split('_', 3);
        if (parts.Length != 3 || parts[0] != "ylm") return AuthenticateResult.Fail("Malformed API key");
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Prefix == parts[1]);
        if (key is null || key.RevokedAt is not null || (key.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow)
            || !TokenHashing.FixedTimeEquals(key.SecretHash, TokenHashing.Hash(parts[2])))
            return AuthenticateResult.Fail("Invalid API key");
        var tenantQuery = db.Tenants.AsNoTracking().Where(t => t.Status != TenantStatus.Offboarded);
        if (!key.AllTenants) tenantQuery = tenantQuery.Where(t => key.TenantIds.Contains(t.Id));
        var tenantIds = await tenantQuery.Select(t => t.Id).ToListAsync();
        if (!key.AllTenants && tenantIds.Count == 0) return AuthenticateResult.Fail("No active tenant for this API key");

        if (key.LastUsedAt is null || key.LastUsedAt < DateTimeOffset.UtcNow.AddMinutes(-5))
        {
            key.LastUsedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var identity = new ClaimsIdentity(
            [new Claim(YlmClaims.ApiKeyId, key.Id.ToString()), new Claim(ClaimTypes.Name, "api:" + key.Name), .. tenantIds.Select(id => new Claim(YlmClaims.ApiKeyTenant, id.ToString())),
             .. key.Scopes.Select(s => new Claim(YlmClaims.ApiKeyScope, s)),
             .. key.AllTenants ? [new Claim(YlmClaims.ApiKeyAllTenants, "true")] : Array.Empty<Claim>()],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public sealed class AgentTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, YlmDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = null;
        var header = Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = header[7..].Trim();
        // WebSockets cannot send headers from the .NET client in every transport; SignalR uses access_token.
        if (token is null && Request.Path.StartsWithSegments(Contracts.AgentProtocol.HubPath)) token = Request.Query["access_token"];
        if (string.IsNullOrEmpty(token)) return AuthenticateResult.NoResult();

        // Format: ylma_<agentId:N>_<secret>
        var parts = token.Split('_', 3);
        if (parts.Length != 3 || parts[0] != "ylma" || !Guid.TryParseExact(parts[1], "N", out var agentId))
            return AuthenticateResult.Fail("Malformed agent token");
        var agent = await db.Agents.Include(a => a.PlatformUser).FirstOrDefaultAsync(a => a.Id == agentId);
        if (agent is null || agent.RevokedAt is not null || agent.PlatformUser is not { IsActive: true }
            || !TokenHashing.FixedTimeEquals(agent.TokenHash, TokenHashing.Hash(parts[2])))
            return AuthenticateResult.Fail("Invalid agent token");

        var identity = new ClaimsIdentity(
        [
            new Claim(YlmClaims.AgentId, agent.Id.ToString()),
            new Claim(YlmClaims.UserId, agent.PlatformUserId.ToString()),
            new Claim(ClaimTypes.Name, agent.MachineName),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

/// <summary>
/// Cookie-authenticated state-changing API calls must carry the X-YLM-CSRF header. Browsers cannot add
/// custom headers cross-site without a CORS preflight, which this server never allows.
/// </summary>
public sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-YLM-CSRF";

    public Task InvokeAsync(HttpContext ctx)
    {
        var method = ctx.Request.Method;
        if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method)
            && ctx.Request.Cookies.Count > 0 && !ctx.Request.Headers.ContainsKey(HeaderName)
            && !ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
            && !ctx.Request.Path.StartsWithSegments("/api/agent"))
        {
            ctx.Response.StatusCode = 400;
            return ctx.Response.WriteAsJsonAsync(new ApiError("csrf", "Fehlender Schutz-Header."));
        }
        return next(ctx);
    }
}
