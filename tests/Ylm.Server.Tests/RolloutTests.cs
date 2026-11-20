using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

public sealed class RolloutTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private readonly FakeGraph _graph = new();
    private HttpClient _owner = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _graph.Users.AddRange([
            new GraphUser("u-key", "anna@contoso.com", "Anna Key", null, "IT", true),
            new GraphUser("u-sms", "bernd@contoso.com", "Bernd SMS", null, "Vertrieb", true),
            new GraphUser("u-none", "carla@contoso.com", "Carla Nichts", null, "Vertrieb", true),
            new GraphUser("u-whfb", "dora@contoso.com", "Dora Hello", null, "IT", true),
            new GraphUser("u-off", "emil@contoso.com", "Emil Aus", null, null, false),
        ]);
        _graph.Methods["u-key"] = [new GraphFido2Method("f1", "YubiKey", DateTimeOffset.UtcNow, null, "YubiKey 5 NFC")];
        _graph.OtherMethods["u-key"] = [new GraphAuthMethod("p1", "phone", "mobilePhone", null, "+49 170 1234567", null)];
        _graph.OtherMethods["u-sms"] = [new GraphAuthMethod("p2", "phone", "mobilePhone", null, "+49 170 7654321", null),
            new GraphAuthMethod("a2", "microsoftAuthenticator", "microsoftAuthenticatorPush", "iPhone", null, null)];
        _graph.OtherMethods["u-whfb"] = [new GraphAuthMethod("w1", "windowsHelloForBusiness", "windowsHelloForBusiness", "LAPTOP-1", "normal", null)];

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

    private string Url(string path = "") => $"/api/tenants/{_tenant}/rollout{path}";

    [Fact]
    public async Task Overview_groups_users_by_stage()
    {
        var o = await Json(await _owner.GetAsync(Url()));
        Assert.True(o.GetProperty("fromReport").GetBoolean());
        var stages = o.GetProperty("users").EnumerateArray().ToDictionary(u => u.GetProperty("id").GetString()!, u => u.GetProperty("stage").GetString());
        Assert.Equal("SecurityKey", stages["u-key"]);
        Assert.Equal("ClassicMfa", stages["u-sms"]);
        Assert.Equal("NoMfa", stages["u-none"]);
        Assert.Equal("OtherPasskey", stages["u-whfb"]);
        var off = o.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("id").GetString() == "u-off");
        Assert.False(off.GetProperty("accountEnabled").GetBoolean());

        var csv = await _owner.GetAsync(Url("/export.csv?stages=NoMfa,ClassicMfa"));
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("bernd@contoso.com", text);
        Assert.Contains("carla@contoso.com", text);
        Assert.DoesNotContain("anna@contoso.com", text);
        Assert.DoesNotContain("emil@contoso.com", text); // deaktiviert, standardmäßig ausgeblendet
    }

    [Fact]
    public async Task Removing_a_method_is_audited_and_updates_the_overview()
    {
        await Json(await _owner.GetAsync(Url()));
        var methods = await Json(await _owner.GetAsync(Url("/users/u-sms/methods")));
        Assert.Contains(methods.EnumerateArray(), m => m.GetProperty("kind").GetString() == "phone" && m.GetProperty("removable").GetBoolean());
        Assert.Contains(methods.EnumerateArray(), m => m.GetProperty("kind").GetString() == "password" && !m.GetProperty("removable").GetBoolean());

        var after = await Json(await _owner.DeleteAsync(Url("/users/u-sms/methods/phone/p2")));
        Assert.DoesNotContain(after.EnumerateArray(), m => m.GetProperty("id").GetString() == "p2");

        // FIDO2 läuft über das Inventar.
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.DeleteAsync(Url("/users/u-key/methods/fido2/f1"))).StatusCode);

        // Passkeys im Authenticator sind ebenfalls fido2, dürfen aber hier entfernt werden.
        _graph.OtherMethods["u-whfb"].Add(new GraphAuthMethod("fa", "fido2", "passKeyDeviceBoundAuthenticator", "Authenticator - iOS", "Microsoft Authenticator - iOS", null));
        var whfb = await Json(await _owner.GetAsync(Url("/users/u-whfb/methods")));
        Assert.True(whfb.EnumerateArray().Single(m => m.GetProperty("id").GetString() == "fa").GetProperty("removable").GetBoolean());
        await Json(await _owner.DeleteAsync(Url("/users/u-whfb/methods/fido2/fa")));
        Assert.DoesNotContain(_graph.OtherMethods["u-whfb"], m => m.Id == "fa");

        await _app.WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.SingleAsync(a => a.Action == "authmethod.removed" && a.Target == "bernd@contoso.com");
            Assert.Equal("bernd@contoso.com", entry.Target);
            Assert.Contains("4321", entry.Details);
            Assert.DoesNotContain("7654321", entry.Details);
        });

        // Die Übersicht zeigt den neuen Stand ohne erneuten Abruf aus Entra.
        var o = await Json(await _owner.GetAsync(Url()));
        var bernd = o.GetProperty("users").EnumerateArray().Single(u => u.GetProperty("id").GetString() == "u-sms");
        Assert.DoesNotContain(bernd.GetProperty("methods").EnumerateArray(), m => m.GetString() == "mobilePhone");
    }

    [Fact]
    public async Task Bulk_removal_skips_users_without_phishing_resistant_method()
    {
        var res = await Json(await _owner.PostAsJsonAsync(Url("/remove-methods"), new { userIds = new[] { "u-key", "u-sms" }, kinds = new[] { "phone" }, keepPhishingResistantOnly = true }));
        var byUser = res.EnumerateArray().ToDictionary(r => r.GetProperty("userId").GetString()!);
        Assert.Equal(1, byUser["u-key"].GetProperty("removed").GetInt32());
        Assert.Equal(0, byUser["u-sms"].GetProperty("removed").GetInt32());
        Assert.NotNull(byUser["u-sms"].GetProperty("skipped").GetString());
        Assert.Empty(_graph.OtherMethods["u-key"]);
        Assert.Equal(2, _graph.OtherMethods["u-sms"].Count);

        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PostAsJsonAsync(Url("/remove-methods"), new { userIds = new[] { "u-key" }, kinds = new[] { "password" } })).StatusCode);
    }

    [Fact]
    public async Task Taps_are_returned_once_and_not_stored()
    {
        var res = await _owner.PostAsJsonAsync(Url("/taps"), new { userIds = new[] { "u-none", "u-sms" }, lifetimeInMinutes = 120, isUsableOnce = true });
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        var taps = await Json(res);
        Assert.All(taps.EnumerateArray(), t => Assert.False(string.IsNullOrEmpty(t.GetProperty("pass").GetString())));
        Assert.Equal(2, _graph.Taps.Count);

        // Ohne „ersetzen“ wird ein vorhandener TAP nicht überschrieben, mit schon.
        var again = await Json(await _owner.PostAsJsonAsync(Url("/taps"), new { userIds = new[] { "u-none" }, replaceExisting = false }));
        Assert.NotNull(again[0].GetProperty("error").GetString());
        var replaced = await Json(await _owner.PostAsJsonAsync(Url("/taps"), new { userIds = new[] { "u-none" }, replaceExisting = true }));
        Assert.False(string.IsNullOrEmpty(replaced[0].GetProperty("pass").GetString()));
        Assert.Single(_graph.OtherMethods["u-none"], m => m.Kind == "temporaryAccessPass");

        await _app.WithDbAsync(async db =>
        {
            var details = await db.AuditEntries.Where(a => a.Action == "authmethod.tap_created").Select(a => a.Details!).ToListAsync();
            Assert.Equal(3, details.Count);
            foreach (var (_, pass) in _graph.Taps) Assert.DoesNotContain(details, d => d.Contains(pass));
        });
    }

    [Fact]
    public async Task Tap_policy_can_be_enabled_by_tenant_managers()
    {
        _graph.TapPolicy = _graph.TapPolicy with { Enabled = false, TargetCount = 0 };
        var failed = await Json(await _owner.PostAsJsonAsync(Url("/taps"), new { userIds = new[] { "u-none" } }));
        Assert.Contains("nicht aktiviert", failed[0].GetProperty("error").GetString());

        var operatorClient = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        Assert.Equal(HttpStatusCode.Forbidden, (await operatorClient.PostAsync(Url("/tap-policy/enable"), null)).StatusCode);

        var policy = await Json(await _owner.PostAsync(Url("/tap-policy/enable"), null));
        Assert.True(policy.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Roles_limit_actions()
    {
        var auditor = await _app.LoginAsync("audit@home.example", PlatformRole.Auditor);
        await Json(await auditor.GetAsync(Url()));
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.PostAsJsonAsync(Url("/taps"), new { userIds = new[] { "u-none" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await auditor.DeleteAsync(Url("/users/u-sms/methods/phone/p2"))).StatusCode);

        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        await Json(await op.PostAsJsonAsync(Url("/taps"), new { userIds = new[] { "u-none" } }));
    }
}
