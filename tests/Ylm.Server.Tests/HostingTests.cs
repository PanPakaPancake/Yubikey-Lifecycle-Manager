using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ylm.Core;
using Ylm.Server.Infrastructure;
using Ylm.Server.Updates;

namespace Ylm.Server.Tests;

public sealed class HostingPlanTests
{
    private static HostingCurrent Current(Dictionary<string, string> env, string[]? secrets = null, string[]? files = null) =>
        new(env, (secrets ?? []).ToHashSet(), (files ?? []).ToHashSet());

    private static readonly HostingCurrent Internal = Current(new()
    {
        ["COMPOSE_PROFILES"] = "https", ["YLM_DOMAIN"] = "ylm.firma.intern", ["YLM_CADDY_TLS"] = "tls internal",
        ["YLM_PUBLIC_URL"] = "https://ylm.firma.intern", ["YLM_MODE"] = "internal",
    });

    [Fact]
    public void Describes_the_installation_from_env()
    {
        var v = HostingPlan.Describe(Internal, null);
        Assert.Equal(HostingMode.Https, v.Mode);
        Assert.Equal(HostingTls.Internal, v.Tls);
        Assert.Equal("ylm.firma.intern", v.Domain);
        Assert.Equal(443, v.HttpsPort);

        var local = HostingPlan.Describe(Current(new() { ["YLM_PUBLIC_URL"] = "http://localhost:8080" }), null);
        Assert.Equal(HostingMode.Local, local.Mode);
        Assert.Equal(8080, local.Port);

        var dns = HostingPlan.Describe(Current(new() { ["COMPOSE_PROFILES"] = "https", ["YLM_CADDY_TLS"] = "import dns-hetzner" }, ["YLM_DNS_TOKEN"]), null);
        Assert.Equal(HostingTls.LetsEncryptDns, dns.Tls);
        Assert.Equal("hetzner", dns.DnsProvider);
        Assert.Equal(["token"], dns.SecretsSet);
    }

    [Fact]
    public void New_domain_and_port_keep_the_old_address_as_alias()
    {
        var c = HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "YLM.firma.de", HttpsPort: 8443, Tls: HostingTls.Internal));
        Assert.Equal("https://ylm.firma.de:8443", c.PublicUrl);
        Assert.Equal("ylm.firma.de", c.Env["YLM_DOMAIN"]);
        Assert.Equal("ylm.firma.intern", c.Env["YLM_DOMAIN_ALIASES"]);
        Assert.Equal("8443", c.Env["YLM_HTTPS_PORT"]);
        Assert.Equal("https://ylm.firma.de:8443", c.Env["YLM_PUBLIC_URL"]);
        // Unverändertes wird nicht geschickt.
        Assert.False(c.Env.ContainsKey("YLM_CADDY_TLS"));
        Assert.False(c.Env.ContainsKey("COMPOSE_PROFILES"));
    }

    [Fact]
    public void Lets_encrypt_without_dns_needs_port_80()
    {
        var e = Assert.Throws<ApiException>(() => HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", HttpPort: 8080, Tls: HostingTls.LetsEncrypt)));
        Assert.Equal("acme_port", e.Code);
        var ok = HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", Tls: HostingTls.LetsEncrypt, AcmeEmail: "it@firma.de"));
        Assert.Equal("tls it@firma.de", ok.Env["YLM_CADDY_TLS"]);
        Assert.Equal("https", ok.Env["YLM_MODE"]);
    }

    [Fact]
    public void Dns_challenge_requires_credentials_but_keeps_stored_secrets()
    {
        var e = Assert.Throws<ApiException>(() => HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", Tls: HostingTls.LetsEncryptDns, DnsProvider: "cloudflare")));
        Assert.Equal("dns_missing", e.Code);

        var c = HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", Tls: HostingTls.LetsEncryptDns, DnsProvider: "cloudflare", Dns: new HostingDns(Token: "cf-token")));
        Assert.Equal("import dns-cloudflare", c.Env["YLM_CADDY_TLS"]);
        Assert.Equal("cf-token", c.Env["YLM_DNS_TOKEN"]);

        var stored = Current(new(Internal.Env) { ["YLM_DNS_PROVIDER"] = "cloudflare", ["YLM_CADDY_TLS"] = "import dns-cloudflare" }, ["YLM_DNS_TOKEN"]);
        var again = HostingPlan.Build(stored, new HostingRequest(HostingMode.Https, "ylm.firma.intern", HttpsPort: 8443, Tls: HostingTls.LetsEncryptDns, DnsProvider: "cloudflare"));
        Assert.False(again.Env.ContainsKey("YLM_DNS_TOKEN"));

        var azure = Assert.Throws<ApiException>(() => HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", Tls: HostingTls.LetsEncryptDns, DnsProvider: "azure", Dns: new HostingDns(KeyId: "x", Secret: "y"))));
        Assert.Contains("Tenant-ID", azure.Message);
    }

    [Fact]
    public void Values_that_compose_would_interpret_are_rejected()
    {
        var e = Assert.Throws<ApiException>(() => HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", Tls: HostingTls.LetsEncryptDns, DnsProvider: "cloudflare", Dns: new HostingDns(Token: "abc$def"))));
        Assert.Equal("invalid_value", e.Code);
    }

    [Fact]
    public void Own_certificate_must_match_key_and_name_and_wildcards_work()
    {
        var (certPem, keyPem) = SelfSigned("*.firma.de");
        var c = HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", KeepOldAddress: false, Tls: HostingTls.Own, CertificatePem: certPem, KeyPem: keyPem));
        Assert.Equal("tls /certs/ylm.crt /certs/ylm.key", c.Env["YLM_CADDY_TLS"]);
        Assert.Equal("cert", c.Env["YLM_MODE"]);
        Assert.Contains("BEGIN CERTIFICATE", c.Files["ylm.crt"]);

        var wrongName = Assert.Throws<ApiException>(() => HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.andere.de", KeepOldAddress: false, Tls: HostingTls.Own, CertificatePem: certPem, KeyPem: keyPem)));
        Assert.Equal("certificate_name", wrongName.Code);

        var (_, otherKey) = SelfSigned("ylm.firma.de");
        var mismatch = Assert.Throws<ApiException>(() => HostingPlan.Build(Internal, new HostingRequest(HostingMode.Https, "ylm.firma.de", Tls: HostingTls.Own, CertificatePem: certPem, KeyPem: otherKey)));
        Assert.Equal("certificate_invalid", mismatch.Code);
    }

    [Fact]
    public void Proxy_and_local_modes()
    {
        var proxy = HostingPlan.Build(Internal, new HostingRequest(HostingMode.Proxy, PublicUrl: "https://ylm.firma.de/", BindAddress: "10.0.0.5", Port: 8081));
        Assert.Equal("https://ylm.firma.de", proxy.PublicUrl);
        Assert.Equal("10.0.0.5:8081", proxy.Env["YLM_BIND"]);
        Assert.Equal("", proxy.Env["COMPOSE_PROFILES"]);
        Assert.Throws<ApiException>(() => HostingPlan.Build(Internal, new HostingRequest(HostingMode.Proxy, PublicUrl: "http://ylm.firma.de")));

        var local = HostingPlan.Build(Internal, new HostingRequest(HostingMode.Local, Port: 9090));
        Assert.Equal("http://localhost:9090", local.PublicUrl);
        Assert.Equal("127.0.0.1:9090", local.Env["YLM_BIND"]);
    }

    public static (string Cert, string Key) SelfSigned(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
        return (cert.ExportCertificatePem(), rsa.ExportPkcs8PrivateKeyPem());
    }
}

/// <summary>Endpunkte mit einem simulierten Updater, der .env im Speicher hält.</summary>
public sealed class HostingEndpointTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private readonly ConcurrentQueue<JsonElement> _posts = new();
    private readonly Dictionary<string, string> _env = new()
    {
        ["COMPOSE_PROFILES"] = "https", ["YLM_DOMAIN"] = "ylm.test", ["YLM_CADDY_TLS"] = "tls internal", ["YLM_PUBLIC_URL"] = "https://ylm.test",
    };
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _factory = null!;

    private sealed class Updater(HostingEndpointTests t) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Headers.Authorization?.Parameter != "updater-secret") return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/hosting")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { env = t._env, secretsSet = Array.Empty<string>(), files = Array.Empty<string>(), certificate = (string?)null, hosting = true }) };
            if (request.Method == HttpMethod.Post && path == "/hosting")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                t._posts.Enqueue(body);
                return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(new { state = "running" }) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    public Task InitializeAsync()
    {
        _factory = _app.WithWebHostBuilder(b =>
        {
            b.UseSetting("Ylm:UpdaterUrl", "http://updater:8090");
            b.UseSetting("Ylm:UpdaterToken", "updater-secret");
            b.UseSetting("Ylm:UpdateCheckEnabled", "false");
            b.ConfigureServices(s => s.AddHttpClient("updates").ConfigurePrimaryHttpMessageHandler(() => new Updater(this)));
        });
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        _app.Dispose();
        return Task.CompletedTask;
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://ylm.test") });
        c.DefaultRequestHeaders.Add("X-YLM-CSRF", "1");
        return c;
    }

    private async Task<HttpClient> LoginAsync(string upn, PlatformRole role)
    {
        var c = Client();
        (await c.PostAsJsonAsync("/auth/dev-login", new { upn, role = role.ToString() })).EnsureSuccessStatusCode();
        return c;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    [Fact]
    public async Task Owner_changes_port_and_certificate_type_via_the_updater()
    {
        var auditor = await LoginAsync("auditor@home.example", PlatformRole.Auditor);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.GetAsync("/api/platform/hosting")).StatusCode);

        var owner = await LoginAsync("owner@home.example", PlatformRole.Owner);
        var state = await Json(await owner.GetAsync("/api/platform/hosting"));
        Assert.True(state.GetProperty("available").GetBoolean());
        Assert.Equal("Https", state.GetProperty("current").GetProperty("mode").GetString());
        Assert.Equal("Internal", state.GetProperty("current").GetProperty("tls").GetString());

        // Gleiche Adresse, anderer Port: neue Adresse, also muss Entra bestätigt sein, sofern eine App-Registrierung existiert.
        var res = await owner.PutAsJsonAsync("/api/platform/hosting", new
        {
            settings = new { mode = "Https", domain = "ylm.test", httpsPort = 8443, tls = "LetsEncryptDns", dnsProvider = "hetzner", dns = new { token = "hz-token" } },
            entraConfirmed = true,
        });
        var applied = await Json(res);
        Assert.Equal("https://ylm.test:8443", applied.GetProperty("publicUrl").GetString());
        Assert.True(_posts.TryDequeue(out var post));
        var env = post.GetProperty("env");
        Assert.Equal("import dns-hetzner", env.GetProperty("YLM_CADDY_TLS").GetString());
        Assert.Equal("hz-token", env.GetProperty("YLM_DNS_TOKEN").GetString());
        Assert.Equal("8443", env.GetProperty("YLM_HTTPS_PORT").GetString());

        // Audit-Log ohne das Geheimnis.
        await _app.WithDbAsync(async db =>
        {
            var entry = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(db.AuditEntries, a => a.Action == "platform.hosting_changed");
            Assert.DoesNotContain("hz-token", entry.Details ?? "");
            Assert.Contains("YLM_DNS_TOKEN", entry.Details ?? "");
        });
    }

    [Fact]
    public async Task Api_key_with_certificate_right_replaces_the_certificate()
    {
        var owner = await LoginAsync("owner@home.example", PlatformRole.Owner);
        var created = await Json(await owner.PostAsJsonAsync("/api/api-keys", new { name = "PKI", allTenants = true, scopes = new[] { "certificate.write" } }));
        var api = Client();
        api.DefaultRequestHeaders.Add("X-Api-Key", created.GetProperty("apiKey").GetString());

        var info = await Json(await api.GetAsync("/api/v1/certificate"));
        Assert.Equal("Internal", info.GetProperty("type").GetString());

        var (cert, key) = HostingPlanTests.SelfSigned("ylm.test");
        var replaced = await Json(await api.PutAsJsonAsync("/api/v1/certificate", new { certificatePem = cert, keyPem = key }));
        Assert.Equal("CN=ylm.test", replaced.GetProperty("certificate").GetProperty("subject").GetString());
        Assert.True(_posts.TryDequeue(out var post));
        Assert.Equal("tls /certs/ylm.crt /certs/ylm.key", post.GetProperty("env").GetProperty("YLM_CADDY_TLS").GetString());
        Assert.Contains("PRIVATE KEY", post.GetProperty("files").GetProperty("ylm.key").GetString());

        // Ohne das Recht: 403.
        var other = await Json(await owner.PostAsJsonAsync("/api/api-keys", new { name = "Lesen", allTenants = true, scopes = new[] { "inventory.read" } }));
        var reader = Client();
        reader.DefaultRequestHeaders.Add("X-Api-Key", other.GetProperty("apiKey").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/certificate", new { certificatePem = cert, keyPem = key })).StatusCode);
    }
}

public sealed class AgentMoveTests : IAsyncLifetime
{
    private readonly TestApp _app = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Agent_follows_a_new_server_address_once_it_answers()
    {
        var owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        var code = JsonDocument.Parse(await (await owner.PostAsync("/api/agents/pairing-codes", null)).Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString()!;
        await using var agent = _app.CreateAgent(new Ylm.Agent.Core.SimulatedKeyDevice { SerialNumber = 50_000_009 });
        Assert.True((await agent.PairAsync("https://ylm.test", code, CancellationToken.None)).Ok);
        for (var i = 0; i < 100 && agent.Status.Connection != Ylm.Contracts.BackendConnectionState.Connected; i++) await Task.Delay(100);

        // Gleiche Adresse oder http: nichts passiert.
        await agent.MoveToAsync("https://ylm.test");
        await agent.MoveToAsync("http://ylm.neu.test");
        Assert.Equal("https://ylm.test", agent.Status.ServerUrl);

        await agent.MoveToAsync("https://ylm.neu.test");
        Assert.Equal("https://ylm.neu.test", agent.Status.ServerUrl);
        for (var i = 0; i < 100 && agent.Status.Connection != Ylm.Contracts.BackendConnectionState.Connected; i++) await Task.Delay(100);
        Assert.Equal(Ylm.Contracts.BackendConnectionState.Connected, agent.Status.Connection);
    }
}
