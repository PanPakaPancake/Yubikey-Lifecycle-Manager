using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Tests;

public sealed class RolesAndPartnerStockTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _owner = null!;
    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var a = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Kunde A", Status = TenantStatus.Active };
            var b = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Kunde B", Status = TenantStatus.Active };
            db.Tenants.AddRange(a, b);
            await db.SaveChangesAsync();
            _tenantA = a.Id;
            _tenantB = b.Id;
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

    private async Task<Guid> AddKeyAsync(Guid? tenantId, int serial, KeyStatus status = KeyStatus.InStock)
    {
        var id = Guid.Empty;
        await _app.WithDbAsync(async db =>
        {
            var k = new YubiKey { TenantId = tenantId, SerialNumber = serial, Status = status, Source = KeySource.Import, Model = "YubiKey 5 NFC" };
            db.Keys.Add(k);
            await db.SaveChangesAsync();
            id = k.Id;
        });
        return id;
    }

    [Fact]
    public async Task Custom_role_grants_only_selected_permissions()
    {
        var bad = await _owner.PostAsJsonAsync("/api/roles", new { name = "Admin light", permissions = new[] { "ReadInventory", "ManageUsers" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var reserved = await _owner.PostAsJsonAsync("/api/roles", new { name = "owner", permissions = new[] { "ReadInventory" } });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);

        var role = await Json(await _owner.PostAsJsonAsync("/api/roles", new
        {
            name = "Lagerist", description = "Nur Lager", permissions = new[] { "ReadInventory", "ManageInventory", "ManagePartnerStock" },
        }));
        var roleId = role.GetProperty("id").GetGuid();

        await Json(await _owner.PostAsJsonAsync("/api/users", new
        {
            upn = "lager@home.example", displayName = "Lager", role = "Custom", customRoleId = roleId, allTenants = true, tenantIds = Array.Empty<Guid>(),
        }));
        var users = await Json(await _owner.GetAsync("/api/users"));
        var lager = users.EnumerateArray().Single(u => u.GetProperty("upn").GetString() == "lager@home.example");
        Assert.Equal("Lagerist", lager.GetProperty("customRoleName").GetString());

        var client = await _app.LoginAsync("lager@home.example");
        var me = await Json(await client.GetAsync("/api/me"));
        var perms = me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToHashSet();
        Assert.Equal(new HashSet<string?> { "ReadInventory", "ManageInventory", "ManagePartnerStock" }, perms);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
        var keyId = await AddKeyAsync(_tenantA, 1001);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/keys/{keyId}/retire", new { note = "x" })).StatusCode);

        // Role in use cannot be deleted; after editing the permissions they apply immediately.
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.DeleteAsync($"/api/roles/{roleId}")).StatusCode);
        await Json(await _owner.PutAsJsonAsync($"/api/roles/{roleId}", new { name = "Lagerist", permissions = new[] { "ReadInventory", "ManageInventory", "ManagePartnerStock", "RetireKeys" } }));
        await Json(await client.PostAsJsonAsync($"/api/keys/{keyId}/retire", new { note = "defekt" }));
    }

    [Fact]
    public async Task Keys_move_between_tenant_and_partner_stock()
    {
        var keyId = await AddKeyAsync(_tenantA, 2001);

        // In den Tenant gelegt bleibt er dort "bereit" – ins Partnerlager getrennt verlässt er den Tenant.
        var moved = await Json(await _owner.PostAsync($"/api/keys/{keyId}/to-partner-stock", null));
        Assert.True(moved.GetProperty("inPartnerStock").GetBoolean());

        var partner = await Json(await _owner.GetAsync("/api/keys?partner=true"));
        Assert.Equal(1, partner.GetProperty("total").GetInt32());
        var tenantList = await Json(await _owner.GetAsync($"/api/keys?tenantId={_tenantA}"));
        Assert.Equal(0, tenantList.GetProperty("total").GetInt32());
        var all = await Json(await _owner.GetAsync("/api/keys"));
        Assert.Equal(1, all.GetProperty("total").GetInt32());

        // Helpdesk has no partner stock permission.
        var helpdesk = await _app.LoginAsync("hd@home.example", PlatformRole.Helpdesk);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.GetAsync("/api/keys?partner=true")).StatusCode);
        Assert.Equal(0, (await Json(await helpdesk.GetAsync("/api/keys"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.GetAsync($"/api/keys/{keyId}")).StatusCode);

        var assigned = await Json(await _owner.PostAsJsonAsync($"/api/keys/{keyId}/assign", new { tenantId = _tenantB }));
        Assert.Equal(_tenantB, assigned.GetProperty("tenantId").GetGuid());
        Assert.Equal("Kunde B", assigned.GetProperty("tenantName").GetString());
        Assert.Equal("InStock", assigned.GetProperty("status").GetString());

        // Ausmustern mit Trennung vom Tenant.
        var retired = await Json(await _owner.PostAsJsonAsync($"/api/keys/{keyId}/retire", new { note = "Rückläufer", toPartnerStock = true }));
        Assert.True(retired.GetProperty("inPartnerStock").GetBoolean());
        Assert.Equal("Retired", retired.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsJsonAsync($"/api/keys/{keyId}/assign", new { tenantId = _tenantA })).StatusCode);

        // An active key cannot leave its tenant.
        var activeId = await AddKeyAsync(_tenantA, 2002, KeyStatus.Active);
        Assert.Equal(HttpStatusCode.Conflict, (await _owner.PostAsync($"/api/keys/{activeId}/to-partner-stock", null)).StatusCode);

        await _app.WithDbAsync(async db =>
        {
            var actions = await db.AuditEntries.Where(a => a.KeyId == keyId).Select(a => a.Action).ToListAsync();
            Assert.Contains("key.moved_to_partner_stock", actions);
            Assert.Contains("key.assigned_to_tenant", actions);
        });
    }
}
