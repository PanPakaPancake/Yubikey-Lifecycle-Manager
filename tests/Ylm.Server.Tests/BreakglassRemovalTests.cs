using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;

namespace Ylm.Server.Tests;

/// <summary>Aktive Breakglass-Keys: Sperren, Zurücksetzen und Konto entfernen nur mit Vier-Augen-Freigabe.</summary>
public sealed class BreakglassRemovalTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private FakeGraph _graph = null!;
    private HttpClient _owner = null!;
    private Guid _tenantId, _keyId, _personalKeyId, _regBg2;

    public async Task InitializeAsync()
    {
        _graph = new FakeGraph();
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _graph.Methods["u-bg1"] = [new GraphFido2Method("m-bg1", "Breakglass", DateTimeOffset.UtcNow, null, "YubiKey 5 Series")];
        _graph.Methods["u-bg2"] = [new GraphFido2Method("m-bg2", "Breakglass", DateTimeOffset.UtcNow, null, "YubiKey 5 Series")];
        _graph.Methods["u-anna"] = [new GraphFido2Method("m-anna", "Anna", DateTimeOffset.UtcNow, null, "YubiKey 5 Series")];
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var t = new Tenant { EntraTenantId = _graph.TenantId, DisplayName = "Contoso GmbH", DefaultDomain = "contoso.com" };
            db.Tenants.Add(t);
            var bg = new YubiKey { TenantId = t.Id, SerialNumber = 50_000_001, Status = KeyStatus.Active, Usage = KeyUsage.Breakglass, Custodian = "Tresor IT" };
            bg.Registrations.Add(new KeyRegistration { TenantId = t.Id, KeyId = bg.Id, EntraUserId = "u-bg1", UserPrincipalName = "bg-admin1@contoso.com", EntraMethodId = "m-bg1" });
            var r2 = new KeyRegistration { TenantId = t.Id, KeyId = bg.Id, EntraUserId = "u-bg2", UserPrincipalName = "bg-admin2@contoso.com", EntraMethodId = "m-bg2" };
            bg.Registrations.Add(r2);
            var personal = new YubiKey { TenantId = t.Id, SerialNumber = 50_000_002, Status = KeyStatus.Active };
            personal.Registrations.Add(new KeyRegistration { TenantId = t.Id, KeyId = personal.Id, EntraUserId = "u-anna", UserPrincipalName = "anna@contoso.com", EntraMethodId = "m-anna" });
            db.Keys.AddRange(bg, personal);
            await db.SaveChangesAsync();
            (_tenantId, _keyId, _personalKeyId, _regBg2) = (t.Id, bg.Id, personal.Id, r2.Id);
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
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<string?> ErrorCode(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        return doc.TryGetProperty("code", out var c) ? c.GetString() : doc.GetProperty("error").GetString();
    }

    [Fact]
    public async Task Locking_a_breakglass_key_needs_a_second_admin()
    {
        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        var detail = await Json(await op.GetAsync($"/api/keys/{_keyId}"));
        Assert.True(detail.GetProperty("removal").GetProperty("required").GetBoolean());

        // Allein geht nichts, auch nicht für den Owner.
        Assert.Equal("approval_required", await ErrorCode(await op.PostAsJsonAsync($"/api/keys/{_keyId}/lock", new { reason = "Lost" })));
        Assert.Equal("approval_required", await ErrorCode(await _owner.PostAsJsonAsync($"/api/keys/{_keyId}/lock", new { reason = "Lost" })));
        Assert.Equal("approval_required", await ErrorCode(await op.PostAsJsonAsync($"/api/keys/{_keyId}/reset", new { agentId = Guid.NewGuid() })));
        Assert.Single(_graph.Methods["u-bg1"]);

        // Persönliche Keys sind nicht betroffen.
        Assert.False((await Json(await op.GetAsync($"/api/keys/{_personalKeyId}"))).GetProperty("removal").GetProperty("required").GetBoolean());
        await Json(await op.PostAsJsonAsync($"/api/keys/{_personalKeyId}/lock", new { reason = "Lost" }));
        Assert.Empty(_graph.Methods["u-anna"]);

        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/keys/{_keyId}/approvals", new { action = "Lock", reason = "" })).StatusCode);
        var approval = await Json(await op.PostAsJsonAsync($"/api/keys/{_keyId}/approvals", new { action = "Lock", reason = "Key verloren, Ticket 4711" }));
        var approvalId = approval.GetProperty("id").GetGuid();
        Assert.Equal("KeyRemoval", approval.GetProperty("kind").GetString());
        Assert.Equal(2, approval.GetProperty("userPrincipalNames").GetArrayLength());

        // Antragsteller darf nicht freigeben, noch nicht erteilt.
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync($"/api/approvals/{approvalId}/approve", new { note = "ok" })).StatusCode);
        Assert.Equal("approval_not_approved", await ErrorCode(await op.PostAsJsonAsync($"/api/keys/{_keyId}/lock", new { reason = "Lost", approvalId })));

        // Eine Freigabe zum Entfernen taugt nicht für eine Registrierung.
        Assert.Empty((await Json(await _owner.GetAsync($"/api/approvals?tenantId={_tenantId}&status=Approved"))).EnumerateArray());

        await Json(await _owner.PostAsJsonAsync($"/api/approvals/{approvalId}/approve", new { note = "passt" }));
        var pending = (await Json(await op.GetAsync($"/api/keys/{_keyId}"))).GetProperty("removal").GetProperty("approvals");
        Assert.Equal("Approved", pending[0].GetProperty("status").GetString());

        // Freigabe gilt nur für die beantragte Aktion und nur für den Antragsteller.
        Assert.Equal("approval_mismatch", await ErrorCode(await op.DeleteAsync($"/api/keys/{_keyId}/registrations/{_regBg2}?approvalId={approvalId}")));
        Assert.Equal("approval_not_yours", await ErrorCode(await _owner.PostAsJsonAsync($"/api/keys/{_keyId}/lock", new { reason = "Lost", approvalId })));

        var locked = await Json(await op.PostAsJsonAsync($"/api/keys/{_keyId}/lock", new { reason = "Lost", approvalId }));
        Assert.Equal("Locked", locked.GetProperty("status").GetString());
        Assert.Empty(_graph.Methods["u-bg1"]);
        Assert.Empty(_graph.Methods["u-bg2"]);
        var list = await Json(await _owner.GetAsync($"/api/approvals?tenantId={_tenantId}"));
        Assert.Equal("Used", list[0].GetProperty("approval").GetProperty("status").GetString());

        // Gesperrt (ohne aktive Konten) braucht es keine Freigabe mehr, z. B. zum Ausmustern.
        Assert.False((await Json(await op.GetAsync($"/api/keys/{_keyId}"))).GetProperty("removal").GetProperty("required").GetBoolean());
        var verify = await Json(await _owner.GetAsync("/api/audit/verify"));
        Assert.True(verify.GetProperty("valid").GetBoolean());
    }

    [Fact]
    public async Task Removing_one_account_from_a_breakglass_key_needs_approval_for_that_account()
    {
        var op = await _app.LoginAsync("op@home.example", PlatformRole.Operator);
        Assert.Equal("approval_required", await ErrorCode(await op.DeleteAsync($"/api/keys/{_keyId}/registrations/{_regBg2}")));
        var approvalId = (await Json(await op.PostAsJsonAsync($"/api/keys/{_keyId}/approvals", new { action = "RemoveAccount", registrationId = _regBg2, reason = "Konto wird abgelöst" })))
            .GetProperty("id").GetGuid();
        await Json(await _owner.PostAsJsonAsync($"/api/approvals/{approvalId}/approve", new { note = (string?)null }));
        await Json(await op.DeleteAsync($"/api/keys/{_keyId}/registrations/{_regBg2}?approvalId={approvalId}"));
        Assert.Empty(_graph.Methods["u-bg2"]);
        Assert.Single(_graph.Methods["u-bg1"]);

        // Verbraucht: ein zweites Mal geht nicht.
        Assert.Equal("approval_not_approved", await ErrorCode(await op.PostAsJsonAsync($"/api/keys/{_keyId}/lock", new { reason = "Lost", approvalId })));
    }

    [Fact]
    public async Task Api_cannot_lock_breakglass_and_switching_protection_off_needs_owner_right()
    {
        var created = await Json(await _owner.PostAsJsonAsync("/api/api-keys", new { name = "Ticket", allTenants = true, scopes = new[] { "inventory.read", "keys.lock" } }));
        var api = _app.Client();
        api.DefaultRequestHeaders.Add("X-Api-Key", created.GetProperty("apiKey").GetString());
        Assert.Equal("approval_required", await ErrorCode(await api.PostAsJsonAsync($"/api/v1/keys/{_keyId}/lock", new { reason = "Lost" })));
        Assert.Single(_graph.Methods["u-bg1"]);

        var tenant = await Json(await _owner.GetAsync($"/api/tenants/{_tenantId}"));
        Assert.True(tenant.GetProperty("requireBreakglassRemovalApproval").GetBoolean());

        // Abschalten ist eine bewusste Owner-Entscheidung und steht im Audit-Log.
        var off = await Json(await _owner.PatchAsJsonAsync($"/api/tenants/{_tenantId}", new { requireBreakglassRemovalApproval = false }));
        Assert.False(off.GetProperty("requireBreakglassRemovalApproval").GetBoolean());
        await Json(await api.PostAsJsonAsync($"/api/v1/keys/{_keyId}/lock", new { reason = "Lost" }));
        Assert.Empty(_graph.Methods["u-bg1"]);
    }
}
