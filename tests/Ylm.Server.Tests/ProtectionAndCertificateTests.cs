using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Secrets;

namespace Ylm.Server.Tests;

/// <summary>Geschützte Konten (Admins, Schutzgruppen) und Ablauf/Erneuerung des App-Zertifikats.</summary>
public sealed class ProtectionAndCertificateTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private readonly FakeGraph _graph = new();
    private HttpClient _owner = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _graph.Users.AddRange([
            new GraphUser("u-admin", "admin@contoso.com", "Ada Admin", null, "IT", true),
            new GraphUser("u-vip", "vip@contoso.com", "Viktor Vorstand", null, "GF", true),
            new GraphUser("u-normal", "nora@contoso.com", "Nora Normal", null, "Vertrieb", true),
        ]);
        _graph.Admins.Add("u-admin");
        _graph.GroupMembers["g-vip"] = ["u-vip"];
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var t = new Tenant { EntraTenantId = _graph.TenantId, DisplayName = "Contoso", Status = TenantStatus.Active };
            db.Tenants.Add(t);
            await db.SaveChangesAsync();
            _tenant = t.Id;
        });
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
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<JsonElement> TapAsync(HttpClient client, params string[] users) =>
        await Json(await client.PostAsJsonAsync($"/api/tenants/{_tenant}/rollout/taps", new { userIds = users }));

    [Fact]
    public async Task Operator_cannot_touch_admins_or_protected_groups_owner_can()
    {
        Assert.Equal(HttpStatusCode.OK, (await _owner.PatchAsJsonAsync($"/api/tenants/{_tenant}",
            new { protectedGroups = new[] { new { id = "g-vip", name = "Vorstand" } } })).StatusCode);

        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        var taps = await TapAsync(op, "u-admin", "u-vip", "u-normal");
        var byUser = taps.EnumerateArray().ToDictionary(t => t.GetProperty("userId").GetString()!);
        Assert.Contains("Geschütztes Konto", byUser["u-admin"].GetProperty("error").GetString());
        Assert.Contains("Geschütztes Konto", byUser["u-vip"].GetProperty("error").GetString());
        Assert.False(string.IsNullOrEmpty(byUser["u-normal"].GetProperty("pass").GetString()));

        var single = await op.DeleteAsync($"/api/tenants/{_tenant}/rollout/users/u-admin/methods/phone/p1");
        Assert.Equal(HttpStatusCode.Forbidden, single.StatusCode);
        Assert.Contains("protected_account", await single.Content.ReadAsStringAsync());

        var ownerTaps = await TapAsync(_owner, "u-admin", "u-vip");
        Assert.All(ownerTaps.EnumerateArray(), t => Assert.False(string.IsNullOrEmpty(t.GetProperty("pass").GetString())));
    }

    [Fact]
    public async Task Only_holders_of_the_permission_can_change_protection()
    {
        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PatchAsJsonAsync($"/api/tenants/{_tenant}", new { protectAdmins = false })).StatusCode);
        var t = await Json(await _owner.PatchAsJsonAsync($"/api/tenants/{_tenant}", new { protectAdmins = false }));
        Assert.False(t.GetProperty("protectAdmins").GetBoolean());

        // Ohne Schutz darf auch der Operator wieder.
        var taps = await TapAsync(op, "u-admin");
        Assert.False(string.IsNullOrEmpty(taps[0].GetProperty("pass").GetString()));
    }

    [Fact]
    public async Task Without_admin_report_only_groups_protect()
    {
        _graph.ReportAvailable = false;
        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        var taps = await TapAsync(op, "u-admin");
        Assert.False(string.IsNullOrEmpty(taps[0].GetProperty("pass").GetString()));
    }

    // ----- App-Zertifikat -----

    private async Task<X509Certificate2> InstallCertificateAsync(int daysLeft, string? objectId = "app-object-1")
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=YLM Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-700), DateTimeOffset.UtcNow.AddDays(daysLeft).AddHours(1));
        var cert = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);
        await _app.Services.GetRequiredService<AppCertificateService>().ImportAsync(cert);
        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            s.OrganizationName = "Muster IT";
            s.SetupCompletedAt ??= DateTimeOffset.UtcNow;
            s.ClientId = Guid.NewGuid();
            s.HomeTenantId = _graph.TenantId;
            s.ApplicationObjectId = objectId;
            s.CertificateThumbprint = cert.Thumbprint;
            s.CertificateExpiresAt = cert.NotAfter.ToUniversalTime();
            await db.SaveChangesAsync();
        });
        return cert;
    }

    private async Task RunCertificateJobAsync()
    {
        using var scope = _app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CertificateRenewalService>().RunAsync();
    }

    [Fact]
    public async Task Certificate_close_to_expiry_is_renewed_with_signed_proof()
    {
        var old = await InstallCertificateAsync(daysLeft: 20);
        var before = await Json(await _owner.GetAsync("/api/platform/certificate"));
        Assert.Equal("warning", before.GetProperty("state").GetString());

        await RunCertificateJobAsync();

        var (objectId, der, proof) = Assert.Single(_graph.AddedKeys);
        Assert.Equal("app-object-1", objectId);
        // Nachweis: RS256-JWT, signiert mit dem alten Zertifikat, iss = Objekt-ID der App.
        var parts = proof.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = JsonDocument.Parse(FromB64Url(parts[1])).RootElement;
        Assert.Equal("app-object-1", payload.GetProperty("iss").GetString());
        Assert.Equal(CertificateRenewalService.ProofAudience, payload.GetProperty("aud").GetString());
        Assert.True(old.GetRSAPublicKey()!.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), FromB64Url(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        var after = await Json(await _owner.GetAsync("/api/platform/certificate"));
        Assert.Equal("ok", after.GetProperty("state").GetString());
        var current = await _app.Services.GetRequiredService<AppCertificateService>().GetAsync();
        Assert.Equal(X509CertificateLoader.LoadCertificate(der).Thumbprint, current!.Thumbprint);
        Assert.True(_app.Graph.Resets > 0);
        await _app.WithDbAsync(async db =>
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "platform.certificate_renewed")));
    }

    [Fact]
    public async Task New_certificate_is_only_used_once_entra_accepts_it()
    {
        var old = await InstallCertificateAsync(daysLeft: 20);
        _app.Graph.AcceptedCertificates.Add(old.Thumbprint); // Entra kennt das neue noch nicht
        await RunCertificateJobAsync();
        Assert.Single(_graph.AddedKeys);
        var service = _app.Services.GetRequiredService<AppCertificateService>();
        Assert.Equal(old.Thumbprint, (await service.GetAsync())!.Thumbprint);
        var pending = await Json(await _owner.GetAsync("/api/platform/certificate"));
        var next = pending.GetProperty("nextThumbprint").GetString()!;

        // Kein zweites addKey, solange der Nachfolger wartet.
        await RunCertificateJobAsync();
        Assert.Single(_graph.AddedKeys);

        _app.Graph.AcceptedCertificates.Add(next);
        await RunCertificateJobAsync();
        Assert.Equal(next, (await service.GetAsync())!.Thumbprint);
    }

    [Fact]
    public async Task Failed_renewal_warns_and_offers_manual_path()
    {
        await InstallCertificateAsync(daysLeft: 5, objectId: null);
        _graph.ApplicationObjectId = null; // App von Hand angelegt, Objekt-ID nicht lesbar
        await RunCertificateJobAsync();

        var status = await Json(await _owner.GetAsync("/api/platform/certificate"));
        Assert.Equal("critical", status.GetProperty("state").GetString());
        Assert.Contains("Objekt-ID", status.GetProperty("lastError").GetString());
        await _app.WithDbAsync(async db =>
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "platform.certificate_renewal_failed"));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "platform.certificate_expiring"));
        });

        // Von Hand: vorbereiten, .cer holen, (in Entra hochladen), aktivieren.
        await Json(await _owner.PostAsync("/api/platform/certificate/prepare", null));
        var cer = await _owner.GetAsync("/api/platform/certificate/next.cer");
        Assert.Equal(HttpStatusCode.OK, cer.StatusCode);
        var uploaded = X509CertificateLoader.LoadCertificate(await cer.Content.ReadAsByteArrayAsync());
        var activated = await Json(await _owner.PostAsync("/api/platform/certificate/activate", null));
        Assert.Equal("ok", activated.GetProperty("state").GetString());
        Assert.Equal(uploaded.Thumbprint, activated.GetProperty("thumbprint").GetString());

        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/platform/certificate")).StatusCode);
    }

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
