using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Integrations;

public sealed record WebhookEventType(string Type, string Group, string Label);

/// <summary>
/// Webhooks (planning 8, V2). Ereignisse kommen aus dem Audit-Log: was im Audit-Log steht, geht als Webhook raus.
/// Jede Zustellung ist mit HMAC-SHA256 signiert und wird bei Fehlern mit wachsendem Abstand wiederholt.
/// </summary>
public sealed class WebhookService(YlmDbContext db, IHttpClientFactory http, IDataProtectionProvider dataProtection, ReportMailService mail, ILogger<WebhookService> logger)
{
    public const string SecretPurpose = "Ylm.Webhook.Secret";
    public const string HttpClientName = "integrations";
    public const string SignatureHeader = "X-YLM-Signature";
    public const string TimestampHeader = "X-YLM-Timestamp";
    public const string EventHeader = "X-YLM-Event";
    public const string DeliveryHeader = "X-YLM-Delivery";
    public const string TestEvent = "webhook.test";

    /// <summary>Abstände der Wiederholungen nach einem Fehlversuch; danach gilt die Zustellung als fehlgeschlagen.</summary>
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6)];

    public static IReadOnlyList<WebhookEventType> Catalog => _catalog.Value;
    private static readonly L.PerLanguage<IReadOnlyList<WebhookEventType>> _catalog = new(() => [
        new(AuditActions.KeyRegistered, L.T("Keys"), L.T("Key registriert")),
        new(AuditActions.KeyRegistrationFailed, L.T("Keys"), L.T("Registrierung fehlgeschlagen")),
        new(AuditActions.KeyLocked, L.T("Keys"), L.T("Key gesperrt")),
        new(AuditActions.KeyReset, L.T("Keys"), L.T("Key zurückgesetzt")),
        new(AuditActions.KeyRetired, L.T("Keys"), L.T("Key ausgemustert")),
        new(AuditActions.KeyIdentified, L.T("Keys"), L.T("Key nacherfasst")),
        new(AuditActions.KeyCustodianChanged, L.T("Keys"), L.T("Verwahrer geändert")),
        new(AuditActions.RegistrationRemoved, L.T("Keys"), L.T("Konto von geteiltem Key entfernt")),
        new(AuditActions.KeyStockIntake, L.T("Lager"), L.T("Wareneingang")),
        new(AuditActions.KeyAssignedToTenant, L.T("Lager"), L.T("Key einem Tenant zugewiesen")),
        new(AuditActions.KeyMovedToPartnerStock, L.T("Lager"), L.T("Key ins Partnerlager")),
        new(AuditActions.RegistrationImported, L.T("Sync"), L.T("Selbst registrierter Key gefunden")),
        new(AuditActions.RegistrationRemovedInEntra, L.T("Sync"), L.T("Passkey in Entra entfernt")),
        new(AuditActions.SyncFailed, L.T("Sync"), L.T("Sync fehlgeschlagen")),
        new(AuditActions.TenantOnboarded, L.T("Tenants"), L.T("Tenant angebunden")),
        new(AuditActions.TenantOffboarded, L.T("Tenants"), L.T("Tenant offboarded")),
        new(AuditActions.ApprovalRequested, L.T("Vier-Augen"), L.T("Freigabe beantragt")),
        new(AuditActions.ApprovalApproved, L.T("Vier-Augen"), L.T("Freigabe erteilt")),
        new(AuditActions.ApprovalRejected, L.T("Vier-Augen"), L.T("Freigabe abgelehnt")),
        new(AuditActions.TodoCreated, L.T("ToDo-Liste"), L.T("ToDo angelegt")),
        new(AuditActions.TodoCompleted, L.T("ToDo-Liste"), L.T("ToDo erledigt")),
        new(AuditActions.TodoCancelled, L.T("ToDo-Liste"), L.T("ToDo zurückgezogen")),
        new(AuditActions.BatchCompleted, L.T("Stapel"), L.T("Stapelregistrierung abgeschlossen")),
        new(AuditActions.ReminderUnusedKeys, L.T("Erinnerungen"), L.T("Keys lange nicht benutzt")),
        new(AuditActions.ReminderNoSpareKey, L.T("Erinnerungen"), L.T("Personen ohne Ersatz-Key")),
        new(AuditActions.ReminderLeavers, L.T("Erinnerungen"), L.T("Ausgeschiedene Personen mit aktivem Key")),
        new(AuditActions.ReminderRetiredWithPasskey, L.T("Erinnerungen"), L.T("Gesperrte Keys mit Passkey in Entra")),
        new(AuditActions.RecoveryCodeUsed, L.T("Plattform"), L.T("Anmeldung mit Notfallcode")),
        new(AuditActions.HostingChanged, L.T("Plattform"), L.T("Adresse oder Zertifikat geändert")),
        new(AuditActions.AppCertificateExpiring, L.T("Plattform"), L.T("App-Zertifikat läuft bald ab")),
        new(AuditActions.AppCertificateRenewed, L.T("Plattform"), L.T("App-Zertifikat erneuert")),
        new(AuditActions.AppCertificateRenewalFailed, L.T("Plattform"), L.T("Erneuerung des App-Zertifikats fehlgeschlagen")),
        // Sicherheit: Schritte, mit denen eine einzelne Person Schutzmechanismen umgehen könnte. Ein zweiter Mensch soll sie sofort sehen.
        new(AuditActions.UserCreated, L.T("Sicherheit"), L.T("Plattform-Benutzer angelegt")),
        new(AuditActions.UserUpdated, L.T("Sicherheit"), L.T("Plattform-Benutzer geändert (Rolle, Tenants, aktiv)")),
        new(AuditActions.RoleUpdated, L.T("Sicherheit"), L.T("Rolle geändert")),
        new(AuditActions.TenantProtectionChanged, L.T("Sicherheit"), L.T("Schutz geändert (Vier-Augen, geschützte Konten)")),
        new(AuditActions.LoginSettingsChanged, L.T("Sicherheit"), L.T("Anmeldeeinstellungen geändert")),
        new(AuditActions.ApiKeyCreated, L.T("Sicherheit"), L.T("API-Schlüssel angelegt")),
        new(AuditActions.ApiKeyScopeChanged, L.T("Sicherheit"), L.T("Rechte eines API-Schlüssels geändert")),
        new(AuditActions.AgentPaired, L.T("Sicherheit"), L.T("Agent gekoppelt")),
        new(AuditActions.TapCreated, L.T("Sicherheit"), L.T("Befristeter Zugriffspass (TAP) erzeugt")),
    ]);

    private static readonly HashSet<string> Exposed = Catalog.Select(c => c.Type).ToHashSet();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Protect(string secret) => dataProtection.CreateProtector(SecretPurpose).Protect(secret);
    private string Unprotect(string secret) => dataProtection.CreateProtector(SecretPurpose).Unprotect(secret);

    public static string Sign(string secret, string timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }

    public static bool Matches(WebhookSubscription s, string eventType, Guid? tenantId) =>
        s.IsActive
        && (s.Events.Count == 0 || s.Events.Contains(eventType) || eventType == TestEvent)
        && (s.AllTenants || (tenantId is { } t && s.TenantIds.Contains(t)));

    private static async Task<IntegrationState> StateAsync(YlmDbContext db, CancellationToken ct)
    {
        var state = await db.IntegrationState.FirstOrDefaultAsync(ct);
        if (state is not null) return state;
        // Erster Lauf: alte Einträge nicht nachliefern.
        state = new IntegrationState { WebhookAuditCursor = await db.AuditEntries.MaxAsync(a => (long?)a.Sequence, ct) ?? 0 };
        db.IntegrationState.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    /// <summary>
    /// Called before a webhook is created: without any webhook the cursor stands still,
    /// so it is moved to the end of the audit log. Only events after the creation are delivered.
    /// </summary>
    public async Task EnsureCursorAsync(CancellationToken ct = default)
    {
        var state = await StateAsync(db, ct);
        if (await db.Webhooks.AnyAsync(ct)) return;
        state.WebhookAuditCursor = await db.AuditEntries.MaxAsync(a => (long?)a.Sequence, ct) ?? 0;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reads new audit entries and queues a delivery per matching webhook.</summary>
    public async Task<int> EnqueueAsync(CancellationToken ct = default)
    {
        using var language = L.Use(await Setup.BrandingService.PlatformLanguageAsync(db, ct));
        var subs = await db.Webhooks.AsNoTracking().Where(w => w.IsActive).ToListAsync(ct);
        var state = await StateAsync(db, ct);
        var entries = await db.AuditEntries.AsNoTracking().Where(a => a.Sequence > state.WebhookAuditCursor).OrderBy(a => a.Sequence).Take(500).ToListAsync(ct);
        if (entries.Count == 0) return 0;
        var tenantIds = entries.Where(e => e.TenantId != null).Select(e => e.TenantId!.Value).Distinct().ToList();
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var keyIds = entries.Where(e => e.KeyId != null).Select(e => e.KeyId!.Value).Distinct().ToList();
        var serials = await db.Keys.AsNoTracking().Where(k => keyIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, k => k.SerialNumber, ct);
        var queued = 0;
        foreach (var e in entries)
        {
            if (!Exposed.Contains(e.Action)) continue;
            var matching = subs.Where(s => Matches(s, e.Action, e.TenantId)).ToList();
            if (matching.Count == 0) continue;
            var tenant = e.TenantId is { } tid ? tenants.GetValueOrDefault(tid) : null;
            object? details = null;
            if (e.Details is not null)
            {
                try { details = JsonSerializer.Deserialize<JsonElement>(e.Details); } catch (JsonException) { details = e.Details; }
            }
            var payload = JsonSerializer.Serialize(new
            {
                id = $"evt_{e.Sequence}",
                @event = e.Action,
                label = Catalog.First(c => c.Type == e.Action).Label,
                occurredAt = e.Timestamp,
                tenant = tenant is null ? null : new { id = tenant.Id, entraTenantId = tenant.EntraTenantId, name = tenant.DisplayName, defaultDomain = tenant.DefaultDomain },
                key = e.KeyId is { } kid ? new { id = kid, serialNumber = serials.GetValueOrDefault(kid) } : null,
                target = e.Target,
                actor = new { type = e.ActorType.ToString(), name = e.ActorName },
                details,
            }, Json);
            foreach (var s in matching)
            {
                db.WebhookDeliveries.Add(new WebhookDelivery { SubscriptionId = s.Id, AuditSequence = e.Sequence, EventType = e.Action, Payload = payload });
                queued++;
            }
        }
        state.WebhookAuditCursor = entries[^1].Sequence;
        await db.SaveChangesAsync(ct);
        return queued;
    }

    public WebhookDelivery QueueTest(WebhookSubscription s, string actor)
    {
        var d = new WebhookDelivery { SubscriptionId = s.Id, EventType = TestEvent };
        d.Payload = JsonSerializer.Serialize(new
        {
            id = "evt_test_" + d.Id.ToString("N")[..8], @event = TestEvent, label = L.T("Testereignis"), occurredAt = DateTimeOffset.UtcNow,
            tenant = (object?)null, key = (object?)null, target = s.Name, actor = new { type = "User", name = actor }, details = new { message = L.T("Testereignis aus dem Yubikey Lifecycle Manager.") },
        }, Json);
        db.WebhookDeliveries.Add(d);
        return d;
    }

    /// <summary>Sends due deliveries. Returns the number of attempts.</summary>
    public async Task<int> DeliverAsync(CancellationToken ct = default)
    {
        using var language = L.Use(await Setup.BrandingService.PlatformLanguageAsync(db, ct));
        var now = DateTimeOffset.UtcNow;
        var due = await db.WebhookDeliveries.Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now).OrderBy(d => d.CreatedAt).Take(50).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var subIds = due.Select(d => d.SubscriptionId).Distinct().ToList();
        var subs = await db.Webhooks.Where(w => subIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var client = http.CreateClient(HttpClientName);
        foreach (var d in due)
        {
            if (!subs.TryGetValue(d.SubscriptionId, out var s) || !s.IsActive)
            {
                d.Status = DeliveryStatus.Failed;
                d.LastError = L.T("Webhook ist deaktiviert.");
                continue;
            }
            await SendAsync(client, s, d, ct);
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task SendAsync(HttpClient client, WebhookSubscription s, WebhookDelivery d, CancellationToken ct)
    {
        d.Attempts++;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string? error = null;
        int? status = null;
        if (s.Channel == WebhookChannel.Mail)
        {
            try { await mail.SendMailAsync(AlertMail(s, d.Payload), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                error = ex is ApiException api ? api.Message : ex.Message;
            }
        }
        else try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, s.Url) { Content = new StringContent(d.Payload, Encoding.UTF8, "application/json") };
            req.Headers.Add(EventHeader, d.EventType);
            req.Headers.Add(DeliveryHeader, d.Id.ToString());
            req.Headers.Add(TimestampHeader, timestamp);
            req.Headers.Add(SignatureHeader, Sign(Unprotect(s.SecretProtected), timestamp, d.Payload));
            req.Headers.UserAgent.ParseAdd("YubikeyLifecycleManager-Webhook/1");
            using var res = await client.SendAsync(req, ct);
            status = (int)res.StatusCode;
            if (!res.IsSuccessStatusCode) error = $"HTTP {status}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or CryptographicException)
        {
            error = ex is TaskCanceledException ? L.T("Zeitüberschreitung") : ex.Message;
        }
        d.LastStatusCode = status;
        d.LastError = error;
        s.LastDeliveryAt = DateTimeOffset.UtcNow;
        s.LastDeliverySucceeded = error is null;
        s.LastError = error;
        if (error is null)
        {
            d.Status = DeliveryStatus.Delivered;
            d.DeliveredAt = DateTimeOffset.UtcNow;
        }
        else if (d.Attempts > RetryDelays.Length || d.EventType == TestEvent)
        {
            d.Status = DeliveryStatus.Failed;
            logger.LogInformation("Webhook {Name} delivery {Delivery} failed finally: {Error}", s.Name, d.Id, error);
        }
        else d.NextAttemptAt = DateTimeOffset.UtcNow.Add(RetryDelays[d.Attempts - 1]);
    }

    /// <summary>Builds the alert mail for one event from the webhook payload.</summary>
    public static OutgoingMail AlertMail(WebhookSubscription s, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;
        var label = Str(r, "label") ?? Str(r, "event") ?? L.T("Ereignis");
        var tenant = r.TryGetProperty("tenant", out var t) ? Str(t, "name") : null;
        var serial = r.TryGetProperty("key", out var k) ? Str(k, "serialNumber") : null;
        var target = Str(r, "target");
        var actor = r.TryGetProperty("actor", out var a) ? Str(a, "name") : null;
        var when = r.TryGetProperty("occurredAt", out var o) && o.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.UtcNow;
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); } catch (Exception) { tz = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTime(when, tz);
        var details = r.TryGetProperty("details", out var dt) && dt.ValueKind is JsonValueKind.Object ? dt.EnumerateObject()
            .Where(p => p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array)).Take(12)
            .Select(p => (p.Name, p.Value.ToString())).ToList() : [];
        var rows = new List<(string, string)> { (L.T("Ereignis"), label), (L.T("Zeit"), L.F($"{(local.ToString("dd.MM.yyyy HH:mm"))} Uhr")) };
        if (tenant is not null) rows.Add((L.T("Tenant"), tenant));
        if (target is not null) rows.Add((L.T("Betrifft"), target));
        if (serial is not null) rows.Add((L.T("Seriennummer"), serial));
        if (actor is not null) rows.Add((L.T("Ausgelöst von"), actor));
        rows.AddRange(details);
        var subject = $"[YubiKey] {label}" + (target is not null ? ": " + target : "") + (tenant is not null ? $" ({tenant})" : "");
        var html = new StringBuilder(L.F($"<p>Alarm „{System.Net.WebUtility.HtmlEncode(s.Name)}“ aus dem Yubikey Lifecycle Manager:</p><table cellpadding=\"4\" style=\"border-collapse:collapse;font-family:sans-serif;font-size:14px\">"));
        var text = new StringBuilder(L.F($"Alarm „{s.Name}“ aus dem Yubikey Lifecycle Manager:\n\n"));
        foreach (var (n, v) in rows)
        {
            html.Append($"<tr><td style=\"color:#666\">{System.Net.WebUtility.HtmlEncode(n)}</td><td><strong>{System.Net.WebUtility.HtmlEncode(v)}</strong></td></tr>");
            text.Append($"{n}: {v}\n");
        }
        html.Append("</table>");
        // Erinnerungen liefern eine Liste der Betroffenen (details.items).
        if (r.TryGetProperty("details", out var det) && det.ValueKind == JsonValueKind.Object && det.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            var lines = items.EnumerateArray().Select(i => string.Join(" · ", new[] { Str(i, "userDisplayName") ?? Str(i, "userPrincipalName"), Str(i, "serialNumber") is { } sn ? L.F($"Key {sn}") : null, Str(i, "detail") }
                .Where(x => !string.IsNullOrEmpty(x)))).Where(x => x.Length > 0).ToList();
            if (lines.Count > 0)
            {
                html.Append("<ul style=\"font-family:sans-serif;font-size:14px\">");
                foreach (var l in lines) html.Append($"<li>{System.Net.WebUtility.HtmlEncode(l)}</li>");
                html.Append("</ul>");
                text.Append('\n').Append(string.Join("\n", lines.Select(l => "- " + l))).Append('\n');
            }
        }
        html.Append(L.T("<p style=\"color:#888;font-size:12px\">Welche Ereignisse diese Mail auslösen, steht unter Integrationen → Mail-Alarme.</p>"));
        text.Append(L.T("\nWelche Ereignisse diese Mail auslösen, steht unter Integrationen → Mail-Alarme.\n"));
        return new OutgoingMail(s.Recipients, subject, html.ToString(), text.ToString(), []);
    }

    /// <summary>Sends one delivery right now (test button), regardless of its schedule.</summary>
    public async Task SendNowAsync(WebhookSubscription s, WebhookDelivery d, CancellationToken ct = default)
    {
        using var language = L.Use(await Setup.BrandingService.PlatformLanguageAsync(db, ct));
        await SendAsync(http.CreateClient(HttpClientName), s, d, ct);
        await db.SaveChangesAsync(ct);
    }
}
