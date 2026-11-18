using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Tests;

public sealed class KeyBulkTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _owner = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var t = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Kunde A", Status = TenantStatus.Active };
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

    private async Task<Guid> AddKeyAsync(Guid? tenantId, int serial, KeyStatus status = KeyStatus.InStock, bool activeRegistration = false)
    {
        var id = Guid.Empty;
        await _app.WithDbAsync(async db =>
        {
            var k = new YubiKey { TenantId = tenantId, SerialNumber = serial, Status = status, Source = KeySource.Import, Model = "YubiKey 5 NFC", Tags = ["alt"] };
            if (activeRegistration)
                k.Registrations.Add(new KeyRegistration { TenantId = tenantId!.Value, EntraUserId = Guid.NewGuid().ToString(), EntraMethodId = Guid.NewGuid().ToString(), UserPrincipalName = "a@b", RegisteredAt = DateTimeOffset.UtcNow });
            db.Keys.Add(k);
            await db.SaveChangesAsync();
            id = k.Id;
        });
        return id;
    }

    [Fact]
    public async Task Bulk_assign_update_and_delete_report_per_key_results()
    {
        var a = await AddKeyAsync(null, 1001);
        var b = await AddKeyAsync(null, 1002);
        var c = await AddKeyAsync(null, 1003, KeyStatus.Retired);

        var assign = await Json(await _owner.PostAsJsonAsync("/api/keys/bulk", new { action = "Assign", ids = new[] { a, b, c }, tenantId = _tenant }));
        Assert.Equal(2, assign.GetProperty("done").GetArrayLength());
        Assert.Contains("Lager", assign.GetProperty("failed")[0].GetProperty("error").GetString());

        var update = await Json(await _owner.PostAsJsonAsync("/api/keys/bulk", new { action = "Update", ids = new[] { a, b }, addTags = new[] { "Charge 2026" }, removeTags = new[] { "alt" } }));
        Assert.Equal(2, update.GetProperty("done").GetArrayLength());
        await _app.WithDbAsync(async db =>
        {
            var keys = await db.Keys.Where(k => k.Id == a || k.Id == b).ToListAsync();
            Assert.All(keys, k => { Assert.Equal(_tenant, k.TenantId); Assert.Equal(["Charge 2026"], k.Tags); });
        });

        var active = await AddKeyAsync(_tenant, 1004, KeyStatus.Active, activeRegistration: true);
        var delete = await Json(await _owner.PostAsJsonAsync("/api/keys/bulk", new { action = "Delete", ids = new[] { a, c, active } }));
        Assert.Equal(2, delete.GetProperty("done").GetArrayLength());
        Assert.Equal(active, delete.GetProperty("failed")[0].GetProperty("id").GetGuid());
        await _app.WithDbAsync(async db =>
        {
            Assert.False(await db.Keys.AnyAsync(k => k.Id == a || k.Id == c));
            Assert.Equal(2, await db.AuditEntries.CountAsync(e => e.Action == "key.deleted"));
        });
    }

    [Fact]
    public async Task Bulk_respects_permissions_and_partner_stock_moves()
    {
        var a = await AddKeyAsync(_tenant, 2001);
        var b = await AddKeyAsync(_tenant, 2002);
        var helpdesk = await _app.LoginAsync("hd@home.example", PlatformRole.Helpdesk);
        var res = await Json(await helpdesk.PostAsJsonAsync("/api/keys/bulk", new { action = "Delete", ids = new[] { a } }));
        Assert.Equal(0, res.GetProperty("done").GetArrayLength());

        var move = await Json(await _owner.PostAsJsonAsync("/api/keys/bulk", new { action = "ToPartnerStock", ids = new[] { a, b } }));
        Assert.Equal(2, move.GetProperty("done").GetArrayLength());
        var retire = await Json(await _owner.PostAsJsonAsync("/api/keys/bulk", new { action = "Retire", ids = new[] { a }, note = "defekt" }));
        Assert.Equal(1, retire.GetProperty("done").GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.DeleteAsync($"/api/keys/{b}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PostAsJsonAsync("/api/keys/bulk", new { action = "Delete", ids = Array.Empty<Guid>() })).StatusCode);
    }
}
