using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ylm.Agent.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Tests;

namespace Ylm.E2E.Host;

/// <summary>
/// Testserver für Browser- und Agent-Tests. Konfiguration über Umgebungsvariablen:
/// <list type="bullet">
/// <item>YLM_E2E_PORT: HTTP-Port (Standard 5180), Adresse http://localhost:PORT</item>
/// <item>YLM_E2E_HTTPS_PORT: zusätzlich HTTPS mit selbst erzeugtem Zertifikat für localhost (optional)</item>
/// <item>YLM_E2E_CERT_OUT: Pfad, unter dem das HTTPS-Zertifikat (.cer) abgelegt wird, damit der Test es als vertrauenswürdig eintragen kann</item>
/// <item>YLM_TEST_POSTGRES: PostgreSQL ohne Datenbankname (Standard Host=localhost;Username=ylm;Password=ylm); die Datenbank wird je Start neu angelegt</item>
/// <item>YLM_E2E_WEBROOT: gebaute Web-Oberfläche (Standard src/Ylm.Server/wwwroot, dorthin baut 'npm run build' in web/)</item>
/// </list>
/// Steuer-Endpunkte für die Tests liegen unter /e2e (nur in diesem Host).
/// </summary>
public static class E2EHost
{
    public static async Task<int> Main()
    {
        var port = int.TryParse(Environment.GetEnvironmentVariable("YLM_E2E_PORT"), out var p) ? p : 5180;
        var httpsPort = int.TryParse(Environment.GetEnvironmentVariable("YLM_E2E_HTTPS_PORT"), out var hp) ? hp : (int?)null;
        var baseUrl = $"http://localhost:{port}";
        var repo = FindRepository();
        var webRoot = Environment.GetEnvironmentVariable("YLM_E2E_WEBROOT") ?? Path.Combine(repo, "src", "Ylm.Server", "wwwroot");
        // Inhaltsverzeichnis des Servers für WebApplicationFactory (sonst sucht sie neben der Lösungsdatei).
        Environment.SetEnvironmentVariable("ASPNETCORE_TEST_CONTENTROOT_YLM_SERVER", Path.Combine(repo, "src", "Ylm.Server"));
        // Ohne gebaute Oberfläche läuft nur die API (reicht für den Agent-Test unter Windows).
        if (!File.Exists(Path.Combine(webRoot, "index.html")))
        {
            Console.Error.WriteLine($"Hinweis: Web-Oberfläche fehlt in {webRoot} ('npm run build' in web/), nur die API ist verfügbar.");
            Directory.CreateDirectory(webRoot);
        }

        X509Certificate2? cert = null;
        if (httpsPort is not null)
        {
            cert = CreateLocalhostCertificate();
            if (Environment.GetEnvironmentVariable("YLM_E2E_CERT_OUT") is { Length: > 0 } certOut)
                await File.WriteAllBytesAsync(certOut, cert.Export(X509ContentType.Cert));
        }

        // Mit HTTPS ist das die öffentliche Adresse; der gekoppelte Agent bleibt dann auf HTTPS.
        var publicUrl = httpsPort is { } hp2 ? $"https://localhost:{hp2}" : baseUrl;
        await using var app = new E2EApp(baseUrl, publicUrl, webRoot);
        app.UseKestrel(k =>
        {
            k.ListenLocalhost(port);
            if (httpsPort is { } h) k.ListenLocalhost(h, l => l.UseHttps(cert!));
        });
        app.StartServer();

        try
        {
            await app.SeedAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Beispieldaten konnten nicht angelegt werden: " + ex);
            return 3;
        }
        Console.WriteLine($"E2E-Host bereit: {baseUrl}" + (httpsPort is { } x ? $" und https://localhost:{x}" : ""));

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
        await stop.Task;
        return 0;
    }

    private static string FindRepository()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "YubikeyLifecycleManager.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository (YubikeyLifecycleManager.slnx) nicht gefunden.");
    }

    private static X509Certificate2 CreateLocalhostCertificate()
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        // Unter Windows braucht SslStream einen Schlüssel aus einem PFX, nicht nur im Speicher.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }
}

/// <summary>Der eigentliche Server (wie in den Server-Tests), mit Beispieldaten und simuliertem Agent.</summary>
public sealed class E2EApp(string baseUrl, string publicUrl, string webRoot) : WebApplicationFactory<global::Program>
{
    public const string OwnerUpn = "owner@muster-it.example";
    public const string OperatorUpn = "operator@muster-it.example";

    private readonly string _database = "ylm_e2e_" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ylm-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly FakeGraphFactory _graphFactory = new();
    private readonly FakeGraph _graph = new();
    private readonly SemaphoreSlim _agentLock = new(1, 1);
    private AgentConnection? _agent;
    private Guid _tenantId;
    private volatile bool _ready;

    public SimulatedKeyDevice Device { get; } = new() { SerialNumber = 31_337_001 };

    private static string BaseConnection => Environment.GetEnvironmentVariable("YLM_TEST_POSTGRES") ?? "Host=localhost;Username=ylm;Password=ylm";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseWebRoot(webRoot);
        builder.UseSetting("ConnectionStrings:Postgres", $"{BaseConnection};Database={_database}");
        builder.UseSetting("Ylm:AuthMode", "Development");
        builder.UseSetting("Ylm:EnableBackgroundJobs", "false");
        builder.UseSetting("Ylm:DataDirectory", _dataDir);
        builder.UseSetting("Ylm:PublicBaseUrl", publicUrl);
        // Keine Abfragen ins Internet (GitHub-Releases), sonst hängen die Tests vom Netz ab.
        builder.UseSetting("Ylm:UpdateCheckEnabled", "false");
        builder.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning).AddFilter("Ylm", LogLevel.Information));
        builder.ConfigureServices(s =>
        {
            foreach (var d in s.Where(d => d.ServiceType == typeof(IGraphClientFactory) || d.ServiceType == typeof(Ylm.Server.Integrations.IMailSender)).ToList())
                s.Remove(d);
            s.AddSingleton<IGraphClientFactory>(_graphFactory);
            s.AddSingleton<Ylm.Server.Integrations.IMailSender>(new FakeMailSender());
            s.AddSingleton<IStartupFilter>(new ControlEndpoints(this));
        });
    }

    // ---------- Beispieldaten ----------

    public async Task SeedAsync()
    {
        _graph.Users.Add(new GraphUser("u-anna", "anna@contoso.com", "Anna Beispiel", "anna@contoso.com", "Vertrieb", true));
        _graph.Users.Add(new GraphUser("u-ben", "ben@contoso.com", "Ben Muster", "ben@contoso.com", "IT", true));
        _graph.Users.Add(new GraphUser("u-carla", "carla@contoso.com", "Carla Selbst", "carla@contoso.com", "Einkauf", true));
        _graph.Users.Add(new GraphUser("u-dana", "dana@contoso.com", "Dana Admin", "dana@contoso.com", "IT", true));
        _graph.Admins.Add("u-dana");
        // Carla hat sich selbst einen YubiKey registriert: kommt mit dem Sync ins Inventar.
        _graph.Methods["u-carla"] = [new GraphFido2Method("m-carla", "Mein YubiKey", DateTimeOffset.UtcNow.AddDays(-3), Ylm.Core.YubicoAaguids.Known.Keys.First(), "YubiKey 5 Series")];
        _graphFactory.Tenants[_graph.TenantId] = _graph;

        using var owner = await LoginAsync(OwnerUpn, "Owner");
        using (var scope = Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<YlmDbContext>();
            var s = await db.Installation.FirstAsync();
            s.OrganizationName = "Muster IT";
            s.ClientId = Guid.NewGuid();
            s.HomeTenantId = _graph.TenantId;
            s.SetupCompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Tenant-Onboarding über denselben Weg wie in der Oberfläche (Admin-Consent-Rücksprung).
        var consent = await JsonAsync(await owner.PostAsJsonAsync("/api/tenants/consent", new { tenantHint = (string?)null }));
        var state = HttpUtility.ParseQueryString(new Uri(consent.GetProperty("url").GetString()!).Query)["state"]!;
        var callback = await owner.GetAsync($"/api/tenants/consent-callback?tenant={_graph.TenantId}&admin_consent=True&state={Uri.EscapeDataString(state)}");
        var location = callback.Headers.Location?.ToString() ?? throw new InvalidOperationException($"Consent-Rücksprung ohne Weiterleitung ({(int)callback.StatusCode})");
        _tenantId = Guid.Parse(location.Split('/')[2].Split('?')[0]);
        await JsonAsync(await owner.PostAsync($"/api/tenants/{_tenantId}/sync", null));

        using var op = await LoginAsync(OperatorUpn, "Operator");
        _ready = true;
    }

    private async Task<HttpClient> LoginAsync(string upn, string role)
    {
        var c = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = new Uri(baseUrl) };
        c.DefaultRequestHeaders.Add("X-YLM-CSRF", "1");
        var res = await c.PostAsJsonAsync("/auth/dev-login", new { upn, displayName = upn.Split('@')[0], role });
        res.EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"{res.RequestMessage?.RequestUri}: {(int)res.StatusCode} {text}");
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    // ---------- Simulierter Agent ----------

    private async Task<(bool Ok, string Message)> PairAgentAsync(string code)
    {
        await _agentLock.WaitAsync();
        try
        {
            if (_agent is not null) await _agent.DisposeAsync();
            var store = new AgentSettingsStore(Path.Combine(_dataDir, "agent-" + Guid.NewGuid().ToString("N")[..6] + ".json"), new PlainSecretProtector());
            _agent = new AgentConnection(Device, store, Services.GetRequiredService<ILoggerFactory>().CreateLogger("E2E.Agent"), "1.0.0-e2e");
            var result = await _agent.PairAsync(baseUrl, code, CancellationToken.None);
            for (var i = 0; result.Ok && i < 100 && _agent.Status.Connection != Ylm.Contracts.BackendConnectionState.Connected; i++) await Task.Delay(100);
            return result;
        }
        finally { _agentLock.Release(); }
    }

    private sealed class ControlEndpoints(E2EApp app) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            // Eigener Zweig vor der Pipeline des Servers, damit dessen Routing und Middleware unberührt bleiben.
            builder.Map("/e2e", branch =>
            {
                branch.UseRouting();
                branch.UseEndpoints(Map);
            });
            next(builder);
        };

        private void Map(IEndpointRouteBuilder e)
        {
            e.MapGet("/info", () => app._ready
                ? Results.Ok(new
                {
                    tenantId = app._tenantId,
                    entraTenantId = app._graph.TenantId,
                    owner = OwnerUpn,
                    @operator = OperatorUpn,
                    agentConnected = app._agent?.Status.Connection == Ylm.Contracts.BackendConnectionState.Connected,
                    key = new { serialNumber = app.Device.SerialNumber, firmware = app.Device.Firmware, inserted = app.Device.Inserted, pin = app.Device.CurrentPin },
                    registered = app._graph.Registered.Select(r => r.UserId),
                })
                : Results.StatusCode(503));

            e.MapPost("/agent/pair", async (PairBody body) =>
            {
                var (ok, message) = await app.PairAgentAsync(body.Code);
                return Results.Ok(new { ok, message });
            });

            // Key ab-/anstecken oder einen anderen Key (Werkszustand) einstecken.
            e.MapPost("/agent/key", (KeyBody body) =>
            {
                switch (body.Action)
                {
                    case "remove": app.Device.Remove(); break;
                    case "insert": app.Device.Insert(); break;
                    case "replace": app.Device.ReplaceWith(body.SerialNumber ?? app.Device.SerialNumber + 1, body.Firmware); break;
                    default: return Results.BadRequest();
                }
                return Results.Ok(new { serialNumber = app.Device.SerialNumber, firmware = app.Device.Firmware, inserted = app.Device.Inserted });
            });
        }
    }

    private sealed record PairBody(string Code);
    private sealed record KeyBody(string Action, int? SerialNumber, string? Firmware);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _agent?.DisposeAsync().AsTask().Wait(2000); } catch { /* best effort */ }
        }
        base.Dispose(disposing);
        if (!disposing) return;
        try
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            using var conn = new Npgsql.NpgsqlConnection(BaseConnection + ";Database=postgres");
            conn.Open();
            using var cmd = new Npgsql.NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", conn);
            cmd.ExecuteNonQuery();
            Directory.Delete(_dataDir, true);
        }
        catch (Exception) { /* best effort */ }
    }
}
