using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Integrations;

namespace Ylm.Server.Tests;

/// <summary>Optionale Lebenszyklus-Erinnerungen: standardmäßig aus, pro Tenant einschaltbar, einmal gemeldet, ausblendbar.</summary>
public sealed class ReminderTests : IAsyncLifetime
{
    private static readonly Guid Yubi5Nfc = Guid.Parse("2fc0579f-8113-47ea-b116-bb5a8db9202a");
    private readonly TestApp _app = new();
    private HttpClient _owner = null!;
    private FakeGraph _graph = null!;
    private Guid _tenantId;
    private readonly Dictionary<string, Guid> _keys = [];

    public async Task InitializeAsync()
    {
        _graph = new FakeGraph();
        _app.Graph.Tenants[_graph.TenantId] = _graph;
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        var now = DateTimeOffset.UtcNow;
        await _app.WithDbAsync(async db =>
        {
            var s = await db.Installation.FirstAsync();
            s.SetupCompletedAt = now;
            s.HomeTenantId = Guid.NewGuid();
            var t = new Tenant { EntraTenantId = _graph.TenantId, DisplayName = "Contoso GmbH", DefaultDomain = "contoso.com" };
            db.Tenants.Add(t);
            _tenantId = t.Id;

            YubiKey Key(string name, int serial, KeyStatus status = KeyStatus.Active, KeyUsage usage = KeyUsage.Personal)
            {
                var k = new YubiKey { TenantId = t.Id, SerialNumber = serial, Model = "YubiKey 5 NFC", FirmwareVersion = "5.7.4", Status = status, Usage = usage, Source = KeySource.Platform };
                db.Keys.Add(k);
                _keys[name] = k.Id;
                return k;
            }
            void Reg(YubiKey k, string user, string method, DateTimeOffset? lastUsed = null, bool? enabled = true, DateTimeOffset? removed = null)
            {
                // Entra kennt dieselben Passkeys (sonst entfernt der Sync sie); Nutzungsdatum liefert der Test-Graph nicht.
                if (!_graph.Users.Any(u => u.Id == user)) _graph.Users.Add(new GraphUser(user, user + "@contoso.com", user, null, null, enabled != false));
                if (removed is null) _graph.Methods.GetOrAdd(user, _ => []).Add(new GraphFido2Method(method, "YubiKey", now.AddDays(-400), Yubi5Nfc, "YubiKey 5 NFC"));
                k.Registrations.Add(new KeyRegistration
                {
                    TenantId = t.Id, EntraUserId = user, UserPrincipalName = user + "@contoso.com", UserDisplayName = user, EntraMethodId = method,
                    RegisteredAt = now.AddDays(-400), LastUsedAt = lastUsed, UserAccountEnabled = enabled, RemovedAt = removed, Source = KeySource.Platform,
                });
            }

            // Anna: ein Key, vor 200 Tagen zuletzt benutzt → lange nicht benutzt, kein Ersatz-Key.
            Reg(Key("anna", 1001), "anna", "m-anna", now.AddDays(-200));
            // Bert: zwei Keys, beide frisch benutzt → nichts.
            Reg(Key("bert1", 1002), "bert", "m-bert1", now.AddDays(-3));
            Reg(Key("bert2", 1003), "bert", "m-bert2", now.AddDays(-10));
            // Carla: Konto deaktiviert, Key noch aktiv.
            Reg(Key("carla", 1004), "carla", "m-carla", now.AddDays(-1), enabled: false);
            // Breakglass, seit Jahren unbenutzt: bleibt absichtlich liegen.
            Reg(Key("breakglass", 1005, usage: KeyUsage.Breakglass), "bg-admin", "m-bg", now.AddDays(-700));
            // Gesperrt, aber Passkey noch in Entra.
            var locked = Key("locked", 1006, KeyStatus.Locked);
            Reg(locked, "dora", "m-dora", now.AddDays(-30));
            // Aktiv, aber das Konto wurde gelöscht (Passkey in Entra entfernt).
            Reg(Key("orphan", 1007), "emil", "m-emil", removed: now.AddDays(-5));
            // Ohne Nutzungsdatum aus Entra: wird nicht als ungenutzt gemeldet.
            Reg(Key("nodate", 1008), "fritz", "m-fritz", lastUsed: null);
            Reg(Key("fritz2", 1009), "fritz", "m-fritz2", now.AddDays(-1));
            await db.SaveChangesAsync();
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

    private async Task<JsonElement> EnableAsync(bool on = true, bool todos = false) =>
        await Json(await _owner.PatchAsJsonAsync($"/api/tenants/{_tenantId}", new
        {
            reminders = new { unusedKeys = on, unusedKeyDays = 180, noSpareKey = on, leavers = on, retiredWithPasskey = on, createTodos = todos },
        }));

    private static Dictionary<string, int> Counts(JsonElement o) =>
        o.GetProperty("counts").EnumerateArray().ToDictionary(c => c.GetProperty("kind").GetString()!, c => c.GetProperty("count").GetInt32());

    private static int[] Serials(JsonElement o, string kind) => o.GetProperty("items").EnumerateArray()
        .Where(i => i.GetProperty("kind").GetString() == kind).Select(i => i.GetProperty("serialNumber").GetInt32()).Order().ToArray();

    private async Task<List<AuditEntry>> AuditAsync(string prefix)
    {
        List<AuditEntry> list = [];
        await _app.WithDbAsync(async db => list = await db.AuditEntries.AsNoTracking().Where(a => a.Action.StartsWith(prefix)).OrderBy(a => a.Sequence).ToListAsync());
        return list;
    }

    [Fact]
    public async Task Reminders_are_off_by_default()
    {
        var tenant = await Json(await _owner.GetAsync($"/api/tenants/{_tenantId}"));
        var r = tenant.GetProperty("reminders");
        Assert.False(r.GetProperty("unusedKeys").GetBoolean());
        Assert.False(r.GetProperty("noSpareKey").GetBoolean());
        Assert.False(r.GetProperty("leavers").GetBoolean());
        Assert.False(r.GetProperty("retiredWithPasskey").GetBoolean());
        Assert.Equal(180, r.GetProperty("unusedKeyDays").GetInt32());

        var dash = await Json(await _owner.GetAsync("/api/dashboard"));
        Assert.Equal(JsonValueKind.Null, dash.GetProperty("reminders").ValueKind);
        var o = await Json(await _owner.GetAsync("/api/reminders"));
        Assert.Empty(o.GetProperty("items").EnumerateArray());
        Assert.Empty(await AuditAsync("reminder."));
    }

    [Fact]
    public async Task Enabled_reminders_find_each_case_and_report_once()
    {
        await EnableAsync();
        var o = await Json(await _owner.GetAsync("/api/reminders"));
        Assert.Equal([1001], Serials(o, "UnusedKey"));
        // Anna allein; Bert und Fritz haben zwei Keys, Carla ist deaktiviert, der Breakglass-Key ist nicht persönlich.
        Assert.Equal([1001], Serials(o, "NoSpareKey"));
        Assert.Equal([1004, 1007], Serials(o, "Leaver"));
        Assert.Equal([1006], Serials(o, "RetiredWithPasskey"));
        Assert.Equal(1, o.GetProperty("keysWithoutUsageDate").GetInt32());

        var dash = await Json(await _owner.GetAsync("/api/dashboard"));
        var counts = dash.GetProperty("reminders").EnumerateArray().ToDictionary(c => c.GetProperty("kind").GetString()!, c => c.GetProperty("count").GetInt32());
        Assert.Equal(2, counts["Leaver"]);

        // Je Art ein Eintrag im Audit-Log (für Webhooks und Mail-Alarme), beim nächsten Lauf nichts Neues.
        var events = await AuditAsync("reminder.");
        Assert.Equal(["reminder.unused_keys", "reminder.no_spare_key", "reminder.leavers", "reminder.retired_with_passkey"], events.Select(e => e.Action).ToArray());
        Assert.Contains("\"count\":2", events.Single(e => e.Action == "reminder.leavers").Details);
        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsync($"/api/tenants/{_tenantId}/sync", null)).StatusCode);
        Assert.Equal(4, (await AuditAsync("reminder.")).Count);

        // Schwelle 365 Tage: Anna (200 Tage) fällt heraus.
        await Json(await _owner.PatchAsJsonAsync($"/api/tenants/{_tenantId}", new { reminders = new { unusedKeyDays = 365 } }));
        Assert.Empty(Serials(await Json(await _owner.GetAsync("/api/reminders")), "UnusedKey"));
        Assert.Equal(HttpStatusCode.BadRequest, (await _owner.PatchAsJsonAsync($"/api/tenants/{_tenantId}", new { reminders = new { unusedKeyDays = 5 } })).StatusCode);
    }

    [Fact]
    public async Task Dismissed_reminders_are_hidden_until_restored()
    {
        await EnableAsync();
        var body = new { tenantId = _tenantId, kind = "RetiredWithPasskey", subject = _keys["locked"].ToString(), note = "Passkey wird manuell gelöscht" };
        Assert.Equal(HttpStatusCode.NoContent, (await _owner.PostAsJsonAsync("/api/reminders/dismiss", body)).StatusCode);
        var o = await Json(await _owner.GetAsync("/api/reminders"));
        Assert.Empty(Serials(o, "RetiredWithPasskey"));
        Assert.Equal(0, Counts(o)["RetiredWithPasskey"]);
        var all = await Json(await _owner.GetAsync("/api/reminders?includeDismissed=true"));
        var hidden = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "RetiredWithPasskey");
        Assert.True(hidden.GetProperty("dismissed").GetBoolean());
        Assert.Equal("Passkey wird manuell gelöscht", hidden.GetProperty("dismissNote").GetString());

        // Helpdesk darf nur lesen, nicht ausblenden.
        var helpdesk = await _app.LoginAsync("hd@home.example", PlatformRole.Helpdesk);
        Assert.Equal(HttpStatusCode.OK, (await helpdesk.GetAsync("/api/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await helpdesk.PostAsJsonAsync("/api/reminders/restore", body)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _owner.PostAsJsonAsync("/api/reminders/restore", body)).StatusCode);
        var restored = await Json(await _owner.GetAsync("/api/reminders"));
        Assert.Equal(new[] { 1006 }, Serials(restored, "RetiredWithPasskey"));
    }

    [Fact]
    public async Task Missing_spare_key_creates_one_todo_and_registration_closes_it()
    {
        await EnableAsync(todos: true);
        List<RegistrationTodo> todos = [];
        await _app.WithDbAsync(async db => todos = await db.Todos.AsNoTracking().ToListAsync());
        var todo = Assert.Single(todos);
        Assert.Equal("anna", todo.EntraUserId);
        Assert.Equal("Erinnerung: Ersatz-Key", todo.Source);

        // Zweiter Lauf: kein doppeltes ToDo.
        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsync($"/api/tenants/{_tenantId}/sync", null)).StatusCode);
        await _app.WithDbAsync(async db => Assert.Equal(1, await db.Todos.CountAsync()));

        // Anna bekommt einen zweiten Key: Erinnerung erledigt, beim erneuten Auftreten würde sie wieder gemeldet.
        await _app.WithDbAsync(async db =>
        {
            var k = new YubiKey { TenantId = _tenantId, SerialNumber = 1010, Status = KeyStatus.Active, Source = KeySource.Platform };
            k.Registrations.Add(new KeyRegistration { TenantId = _tenantId, EntraUserId = "anna", UserPrincipalName = "anna@contoso.com", EntraMethodId = "m-anna2", LastUsedAt = DateTimeOffset.UtcNow });
            _graph.Methods["anna"].Add(new GraphFido2Method("m-anna2", "YubiKey", DateTimeOffset.UtcNow, Yubi5Nfc, "YubiKey 5 NFC"));
            db.Keys.Add(k);
            await db.SaveChangesAsync();
        });
        Assert.Empty(Serials(await Json(await _owner.GetAsync("/api/reminders")), "NoSpareKey"));
        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsync($"/api/tenants/{_tenantId}/sync", null)).StatusCode);
        await _app.WithDbAsync(async db => Assert.False(await db.ReminderStates.AnyAsync(s => s.Kind == ReminderKind.NoSpareKey)));
    }

    [Fact]
    public async Task Sync_reads_last_use_and_disabled_accounts_from_entra()
    {
        // Ein selbst registrierter Key in Entra, Konto deaktiviert, Passkey seit 300 Tagen ungenutzt.
        _graph.Users.Add(new GraphUser("u-gina", "gina@contoso.com", "Gina", null, null, false));
        _graph.Methods["u-gina"] = [new GraphFido2Method("m-gina", "YubiKey", DateTimeOffset.UtcNow.AddDays(-500), Yubi5Nfc, "YubiKey 5 NFC", DateTimeOffset.UtcNow.AddDays(-300))];
        await EnableAsync();
        Assert.Equal(HttpStatusCode.OK, (await _owner.PostAsync($"/api/tenants/{_tenantId}/sync", null)).StatusCode);

        KeyRegistration? reg = null;
        await _app.WithDbAsync(async db => reg = await db.Registrations.AsNoTracking().SingleAsync(r => r.EntraMethodId == "m-gina"));
        Assert.False(reg!.UserAccountEnabled);
        Assert.NotNull(reg.LastUsedAt);
        var o = await Json(await _owner.GetAsync("/api/reminders"));
        var gina = o.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("userPrincipalName").GetString() == "gina@contoso.com").Select(i => i.GetProperty("kind").GetString()!).Order().ToArray();
        // Deaktiviert: als ausgeschieden gemeldet, nicht als „ohne Ersatz-Key“.
        Assert.Equal(["Leaver", "UnusedKey"], gina);
    }

    [Fact]
    public async Task Reminder_events_reach_mail_alerts_with_the_affected_people()
    {
        await Json(await _owner.PutAsJsonAsync("/api/integrations/smtp", new { host = "smtp.contoso.com", port = 587, security = "StartTls", fromAddress = "ylm@contoso.com" }));
        await Json(await _owner.PostAsJsonAsync("/api/integrations/webhooks",
            new { name = "Lebenszyklus", channel = "Mail", recipients = new[] { "it@contoso.com" }, events = new[] { "reminder.leavers" }, allTenants = true }));
        var catalog = await Json(await _owner.GetAsync("/api/integrations/webhooks/events"));
        Assert.Contains(catalog.EnumerateArray(), e => e.GetProperty("type").GetString() == "reminder.no_spare_key");

        await EnableAsync();
        await _app.Services.GetRequiredService<IntegrationJobs>().RunWebhooksAsync(CancellationToken.None);
        Assert.True(_app.Mail.Sent.TryDequeue(out var mail));
        Assert.Contains("Ausgeschiedene Personen", mail.Mail.Subject);
        Assert.Contains("carla", mail.Mail.Text);
        Assert.Contains("Key 1004", mail.Mail.Text);
        Assert.False(_app.Mail.Sent.TryDequeue(out _));
    }
}
