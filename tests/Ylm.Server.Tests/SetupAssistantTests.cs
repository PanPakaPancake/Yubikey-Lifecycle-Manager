using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Server.Setup;

namespace Ylm.Server.Tests;

/// <summary>Stand-in for the Microsoft sign-in (device code) and the delegated app provisioning.</summary>
public sealed class FakeEntraBootstrap : IEntraBootstrap
{
    public Guid TenantId { get; set; } = Guid.NewGuid();
    public int PendingPolls { get; set; } = 1;
    public Guid ClientId { get; } = Guid.NewGuid();
    public List<AppProvisioningRequest> Requests { get; } = [];
    public int Polls { get; private set; }

    public List<(string Code, string RedirectUri, string Verifier)> Redeemed { get; } = [];

    public string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge) =>
        $"https://login.example/authorize?redirect_uri={Uri.EscapeDataString(redirectUri)}&state={Uri.EscapeDataString(state)}&code_challenge={codeChallenge}&code_challenge_method=S256";

    public Task<DeviceCodePoll> RedeemCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
    {
        Redeemed.Add((code, redirectUri, codeVerifier));
        return Task.FromResult(new DeviceCodePoll(DeviceCodeStatus.SignedIn, "delegated-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    public Task<DeviceCodeStart> StartDeviceCodeAsync(CancellationToken ct = default) =>
        Task.FromResult(new DeviceCodeStart("device-123", "ABCD-EFGH", "https://microsoft.com/devicelogin", 900, 1));

    public Task<DeviceCodePoll> PollDeviceCodeAsync(string deviceCode, CancellationToken ct = default)
    {
        Assert.Equal("device-123", deviceCode);
        Polls++;
        return Task.FromResult(Polls <= PendingPolls
            ? new DeviceCodePoll(DeviceCodeStatus.Pending)
            : new DeviceCodePoll(DeviceCodeStatus.SignedIn, "delegated-token", DateTimeOffset.UtcNow.AddHours(1)));
    }

    public Task<SignedInAdmin> GetSignedInAdminAsync(string accessToken, CancellationToken ct = default)
    {
        Assert.Equal("delegated-token", accessToken);
        return Task.FromResult(new SignedInAdmin("admin@contoso.com", "Admin Contoso", TenantId, "Contoso GmbH", "contoso.com"));
    }

    public Task<AppProvisioningResult> ProvisionAppAsync(string accessToken, AppProvisioningRequest request, CancellationToken ct = default)
    {
        Assert.Equal("delegated-token", accessToken);
        Requests.Add(request);
        return Task.FromResult(new AppProvisioningResult(ClientId, ["App-Registrierung angelegt"]));
    }
}

public sealed class SetupAssistantTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _app.Client();
        var secrets = (Ylm.Server.Secrets.ISecretStore)_app.Services.GetService(typeof(Ylm.Server.Secrets.ISecretStore))!;
        var token = System.Text.Encoding.UTF8.GetString((await secrets.GetAsync("setup-token"))!);
        _client.DefaultRequestHeaders.Add("X-Setup-Token", token);
        // The home tenant's app-only Graph, as it answers once the app exists.
        var graph = new FakeGraph();
        _app.Bootstrap.TenantId = graph.TenantId;
        _app.Graph.Tenants[graph.TenantId] = graph;
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> SignInAsync()
    {
        var start = await Json(await _client.PostAsync("/api/setup/signin/start", null));
        Assert.Equal("ABCD-EFGH", start.GetProperty("userCode").GetString());
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(1100);
            var poll = await Json(await _client.PostAsync("/api/setup/signin/poll", null));
            if (poll.GetProperty("status").GetString() == "signed_in") return poll;
            Assert.Equal("pending", poll.GetProperty("status").GetString());
        }
        throw new Xunit.Sdk.XunitException("Anmeldung nicht abgeschlossen");
    }

    [Fact]
    public async Task Interactive_sign_in_with_pasted_redirect_address()
    {
        var start = await Json(await _client.PostAsync("/api/setup/signin/interactive", null));
        var url = start.GetProperty("url").GetString()!;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(url).Query);
        var state = query["state"].ToString();
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.StartsWith("http://localhost", start.GetProperty("redirectUri").GetString());

        // Der Rücksprung braucht kein Setup-Token, aber den passenden state.
        var anonymous = _app.Client();
        var wrong = await Json(await anonymous.PostAsJsonAsync("/api/setup/signin/complete", new { url = "http://localhost/?code=abc&state=falsch" }));
        Assert.Equal("expired", wrong.GetProperty("status").GetString());
        var denied = await Json(await anonymous.PostAsJsonAsync("/api/setup/signin/complete", new { url = $"http://localhost/?error=access_denied&error_description=abgelehnt&state={state}" }));
        Assert.Equal("failed", denied.GetProperty("status").GetString());

        var ok = await Json(await anonymous.PostAsJsonAsync("/api/setup/signin/complete", new { url = $"http://localhost/?code=abc123&state={Uri.EscapeDataString(state)}&session_state=x" }));
        Assert.Equal("signed_in", ok.GetProperty("status").GetString());
        var (code, redirect, verifier) = Assert.Single(_app.Bootstrap.Redeemed);
        Assert.Equal("abc123", code);
        Assert.Equal(start.GetProperty("redirectUri").GetString(), redirect);
        // PKCE: Challenge = SHA256(Verifier)
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(challenge, query["code_challenge"].ToString());

        // Einmalig: derselbe state geht nicht noch einmal.
        var again = await Json(await anonymous.PostAsJsonAsync("/api/setup/signin/complete", new { url = $"http://localhost/?code=abc123&state={Uri.EscapeDataString(state)}" }));
        Assert.Equal("expired", again.GetProperty("status").GetString());

        // Der Assistent im ursprünglichen Fenster sieht die Anmeldung beim nächsten Abfragen.
        var poll = await Json(await _client.PostAsync("/api/setup/signin/poll", null));
        Assert.Equal("signed_in", poll.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Sign_in_provisions_app_and_registers_home_tenant()
    {
        (await _client.PostAsJsonAsync("/api/setup/basics", new { organizationName = "Muster IT", publicBaseUrl = "https://ylm.test" })).EnsureSuccessStatusCode();

        var noSignIn = await _client.PostAsync("/api/setup/provision", null);
        Assert.Equal(HttpStatusCode.Conflict, noSignIn.StatusCode);

        var poll = await SignInAsync();
        Assert.Equal("admin@contoso.com", poll.GetProperty("admin").GetProperty("upn").GetString());

        var provisioned = await Json(await _client.PostAsync("/api/setup/provision", null));
        Assert.Equal(_app.Bootstrap.ClientId, provisioned.GetProperty("clientId").GetGuid());
        Assert.Equal(_app.Bootstrap.TenantId, provisioned.GetProperty("homeTenantId").GetGuid());
        Assert.Equal("admin@contoso.com", provisioned.GetProperty("bootstrapOwnerUpn").GetString());

        var request = Assert.Single(_app.Bootstrap.Requests);
        Assert.Null(request.ExistingClientId);
        Assert.Equal("Yubikey Lifecycle Manager Muster IT", request.DisplayName);
        Assert.Contains("https://ylm.test/signin-oidc", request.RedirectUris);
        Assert.Contains("https://ylm.test/api/tenants/consent-callback", request.RedirectUris);
        Assert.Contains("UserAuthenticationMethod.ReadWrite.All", request.ApplicationPermissions);
        Assert.NotEmpty(request.CertificateDer);

        // A second attempt (e.g. after an error) updates the same app instead of creating another one.
        await Json(await _client.PostAsync("/api/setup/provision", null));
        Assert.Equal(_app.Bootstrap.ClientId, _app.Bootstrap.Requests[1].ExistingClientId);

        var verify = await Json(await _client.PostAsync("/api/setup/verify", null));
        Assert.True(verify.GetProperty("ok").GetBoolean());
        Assert.Equal(0, verify.GetProperty("missing").GetArrayLength());

        var state = await Json(await _client.GetAsync("/api/setup/state"));
        Assert.Equal("admin@contoso.com", state.GetProperty("signedInAdmin").GetProperty("upn").GetString());

        (await _client.PostAsJsonAsync("/api/setup/complete", new { addHomeTenant = true })).EnsureSuccessStatusCode();

        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            Assert.True(s.IsSetupComplete);
            Assert.Equal("admin@contoso.com", s.BootstrapOwnerUpn);
            var tenant = await db.Tenants.SingleAsync();
            Assert.Equal(_app.Bootstrap.TenantId, tenant.EntraTenantId);
            Assert.Equal("Contoso GmbH", tenant.DisplayName);
            Assert.Equal("contoso.com", tenant.DefaultDomain);
            Assert.Equal("admin@contoso.com", tenant.ConsentedBy);
            Assert.NotNull(tenant.LastCheckAt);
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "setup.app_provisioned"));
        });

        // The setup token is no longer valid.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/setup/state")).StatusCode);
    }

    [Fact]
    public async Task Complete_without_home_tenant_and_with_other_owner()
    {
        (await _client.PostAsJsonAsync("/api/setup/basics", new { organizationName = "Muster IT", publicBaseUrl = "https://ylm.test" })).EnsureSuccessStatusCode();
        await SignInAsync();
        await Json(await _client.PostAsync("/api/setup/provision", null));

        var bad = await _client.PostAsJsonAsync("/api/setup/complete", new { bootstrapOwnerUpn = "kein-upn", addHomeTenant = false });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        (await _client.PostAsJsonAsync("/api/setup/complete", new { bootstrapOwnerUpn = "it-chef@contoso.com", addHomeTenant = false })).EnsureSuccessStatusCode();
        await _app.WithDbAsync(async db =>
        {
            Assert.Equal("it-chef@contoso.com", (await db.Installation.FirstAsync()).BootstrapOwnerUpn);
            Assert.False(await db.Tenants.AnyAsync());
        });
    }
}
