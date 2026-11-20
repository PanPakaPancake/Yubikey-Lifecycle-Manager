using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Tests;

/// <summary>Rechte pro API-Schlüssel: Bereiche lesen/schreiben, kombiniert mit der Tenant-Auswahl.</summary>
public sealed class ApiKeyScopeTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private FakeGraph _graph = null!;
    private HttpClient _owner = null!;
    private Guid _contoso, _fabrikam, _contosoKey, _fabrikamKey;

    public async Task InitializeAsync()
    {
        _graph = new FakeGraph();
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var c = new Tenant { EntraTenantId = _graph.TenantId, DisplayName = "Contoso GmbH", DefaultDomain = "contoso.com" };
            var f = new Tenant { EntraTenantId = Guid.NewGuid(), DisplayName = "Fabrikam AG", DefaultDomain = "fabrikam.com" };
            db.Tenants.AddRange(c, f);
            var ck = new YubiKey { TenantId = c.Id, SerialNumber = 30000001, Status = KeyStatus.Active, Model = "YubiKey 5 NFC" };
            ck.Registrations.Add(new KeyRegistration { TenantId = c.Id, KeyId = ck.Id, EntraUserId = "u-anna", UserPrincipalName = "anna@contoso.com", EntraMethodId = "m-1" });
            var fk = new YubiKey { TenantId = f.Id, SerialNumber = 30000002 };
            db.Keys.AddRange(ck, fk);
            await db.SaveChangesAsync();
            (_contoso, _fabrikam, _contosoKey, _fabrikamKey) = (c.Id, f.Id, ck.Id, fk.Id);
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

    private async Task<(HttpClient Client, Guid Id)> KeyAsync(object body)
    {
        var created = await Json(await _owner.PostAsJsonAsync("/api/api-keys", body));
        var c = _app.Client();
        c.DefaultRequestHeaders.Add("X-Api-Key", created.GetProperty("apiKey").GetString());
        return (c, created.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Read_only_key_for_one_tenant_sees_only_that_tenant_and_cannot_write()
    {
        var (c, _) = await KeyAsync(new { name = "Nur lesen Contoso", allTenants = false, tenantIds = new[] { _contoso }, scopes = new[] { "inventory.read" } });

        var me = await Json(await c.GetAsync("/api/v1/me"));
        Assert.Equal(["inventory.read"], me.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.Single(me.GetProperty("tenants").EnumerateArray());

        Assert.Equal(1, (await Json(await c.GetAsync("/api/v1/keys"))).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/v1/keys/{_fabrikamKey}")).StatusCode);
        // Bereiche ohne Recht: 403 mit Hinweis auf das fehlende Recht.
        var summary = await c.GetAsync("/api/v1/summary");
        Assert.Equal(HttpStatusCode.Forbidden, summary.StatusCode);
        Assert.Contains("reports.read", await summary.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/assignments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/todos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/v1/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchAsJsonAsync($"/api/v1/keys/{_contosoKey}", new { tags = new[] { "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"/api/v1/keys/{_contosoKey}/lock", new { reason = "Lost" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/v1/todos", new { userPrincipalName = "anna@contoso.com" })).StatusCode);
    }

    [Fact]
    public async Task Read_write_key_for_all_tenants_maintains_inventory_and_locks_keys_with_audit()
    {
        var (c, id) = await KeyAsync(new { name = "Ticketsystem", allTenants = true, scopes = new[] { "inventory.read", "inventory.write", "keys.lock", "audit.read" } });

        var patched = await Json(await c.PatchAsJsonAsync($"/api/v1/keys/{_fabrikamKey}", new { tags = new[] { "Lager Nord" }, notes = "Aus Ticket 4711" }));
        Assert.Equal(["Lager Nord"], patched.GetProperty("tags").EnumerateArray().Select(s => s.GetString()));

        _graph.Methods["u-anna"] = [new("m-1", "YubiKey 5 NFC", DateTimeOffset.UtcNow, null, "YubiKey 5 NFC")];
        var locked = await Json(await c.PostAsJsonAsync($"/api/v1/keys/{_contosoKey}/lock", new { reason = "Lost", note = "Ticket 4712" }));
        Assert.Equal("Locked", locked.GetProperty("status").GetString());
        Assert.Empty(_graph.Methods["u-anna"]);

        await _app.WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.Where(a => a.ActorType == ActorType.Api).ToListAsync();
            Assert.Contains(entries, a => a.Action == "key.updated" && a.KeyId == _fabrikamKey && a.ActorName == "api:Ticketsystem");
            Assert.Contains(entries, a => a.Action == "key.locked" && a.KeyId == _contosoKey);
        });

        // Über die API abholbar, fortlaufend mit after.
        var audit = await Json(await c.GetAsync("/api/v1/audit?action=key."));
        Assert.Equal(2, audit.GetArrayLength());
        var last = audit.EnumerateArray().Last().GetProperty("sequence").GetInt64();
        Assert.Equal(0, (await Json(await c.GetAsync($"/api/v1/audit?action=key.&after={last}"))).GetArrayLength());
        // Schlüssel für alle Tenants sehen auch Einträge ohne Tenant (z. B. Schlüssel angelegt).
        Assert.Contains((await Json(await c.GetAsync("/api/v1/audit"))).EnumerateArray(), e => e.GetProperty("action").GetString() == "apikey.created");
    }

    [Fact]
    public async Task Scopes_are_validated_listed_and_changed_with_audit()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PostAsJsonAsync("/api/api-keys", new { name = "X", allTenants = true, scopes = new[] { "keys.delete" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PostAsJsonAsync("/api/api-keys", new { name = "X", allTenants = true, scopes = Array.Empty<string>() })).StatusCode);

        var catalog = await Json(await _owner.GetAsync("/api/api-keys/scopes"));
        Assert.Contains(catalog.EnumerateArray(), s => s.GetProperty("id").GetString() == "keys.lock" && s.GetProperty("write").GetBoolean());

        // Ohne Angabe wie bisher: alles Lesende, mit allowTodoWrite zusätzlich ToDo schreiben.
        var (legacy, legacyId) = await KeyAsync(new { name = "Alt", allTenants = true, allowTodoWrite = true });
        var me = await Json(await legacy.GetAsync("/api/v1/me"));
        Assert.Equal(["inventory.read", "assignments.read", "reports.read", "todos.read", "todos.write"], me.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));

        (await _owner.PutAsJsonAsync($"/api/api-keys/{legacyId}/scope", new { allTenants = false, tenantIds = new[] { _fabrikam }, scopes = new[] { "reports.read" } })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await legacy.GetAsync("/api/v1/keys")).StatusCode);
        Assert.Equal(1, (await Json(await legacy.GetAsync("/api/v1/summary"))).GetProperty("total").GetInt32());

        var listed = (await Json(await _owner.GetAsync("/api/api-keys"))).EnumerateArray().First(k => k.GetProperty("id").GetGuid() == legacyId);
        Assert.Equal(["reports.read"], listed.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        await _app.WithDbAsync(async db =>
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "apikey.scope_changed" && a.Details!.Contains("reports.read"))));

        var openapi = await Json(await _app.Client().GetAsync("/openapi/v1.json"));
        var lockOp = openapi.GetProperty("paths").GetProperty("/api/v1/keys/{id}/lock").GetProperty("post");
        Assert.Contains("keys.lock", lockOp.GetProperty("description").GetString());
    }
}
