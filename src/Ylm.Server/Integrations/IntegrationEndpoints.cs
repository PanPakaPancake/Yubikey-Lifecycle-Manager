using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Integrations;

/// <summary>Partnerebene → Integrationen: Webhooks und Mail-Berichte (V2). Nur mit „API und Integrationen“ (Owner).</summary>
public static class IntegrationEndpoints
{
    public sealed record WebhookRequest(string Name, string? Url, List<string>? Events, bool AllTenants, List<Guid>? TenantIds, bool IsActive = true,
        WebhookChannel Channel = WebhookChannel.Webhook, List<string>? Recipients = null);
    public sealed record SmtpRequest(string? Host, int Port, SmtpSecurity Security, string? Username, string? Password, bool ClearPassword, string? FromAddress, string? FromName);
    public sealed record TestMailRequest(string To);
    public sealed record ScheduleRequest(string Name, List<string> Recipients, bool AllTenants, List<Guid>? TenantIds, ReportFrequency Frequency, int DayOfWeek, int DayOfMonth, int Hour,
        string? TimeZone, bool IncludeCsv, bool IsActive = true);

    private static object WebhookDto(WebhookSubscription w) => new
    {
        w.Id, w.Name, Channel = w.Channel.ToString(), w.Url, w.Recipients, w.Events, w.AllTenants, w.TenantIds, w.IsActive, w.CreatedAt, w.CreatedBy, w.LastDeliveryAt, w.LastDeliverySucceeded, w.LastError,
    };

    private static object ScheduleDto(ReportSchedule s) => new
    {
        s.Id, s.Name, s.Recipients, s.AllTenants, s.TenantIds, Frequency = s.Frequency.ToString(), s.DayOfWeek, s.DayOfMonth, s.Hour, s.TimeZone, s.IncludeCsv, s.IsActive,
        s.NextRunAt, s.LastSentAt, s.LastSucceeded, s.LastError, Description = ReportMailService.Describe(s),
    };

    private static string ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
            throw ApiException.BadRequest("invalid_url", "Bitte eine vollständige URL mit https:// angeben.");
        if (!string.IsNullOrEmpty(u.UserInfo)) throw ApiException.BadRequest("invalid_url", "Zugangsdaten gehören nicht in die URL. Die Signatur sichert den Webhook ab.");
        return u.ToString();
    }

    private static async Task<List<Guid>> ValidateTenantsAsync(YlmDbContext db, bool all, List<Guid>? ids)
    {
        if (all) return [];
        var list = (ids ?? []).Distinct().ToList();
        if (list.Count == 0) throw ApiException.BadRequest("invalid", "Bitte mindestens einen Tenant auswählen oder „Alle Tenants“ wählen.");
        if (await db.Tenants.CountAsync(t => list.Contains(t.Id)) != list.Count) throw ApiException.BadRequest("invalid", "Mindestens ein Tenant existiert nicht.");
        return list;
    }

    private static List<string> ValidateRecipients(List<string>? recipients)
    {
        var list = (recipients ?? []).SelectMany(r => r.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries)).Select(r => r.Trim()).Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) throw ApiException.BadRequest("no_recipients", "Bitte mindestens einen Empfänger angeben.");
        if (list.Count > 50) throw ApiException.BadRequest("too_many", "Höchstens 50 Empfänger.");
        foreach (var r in list)
            if (!MailAddress.TryCreate(r, out _)) throw ApiException.BadRequest("invalid_recipient", $"„{r}“ ist keine gültige Mailadresse.");
        return list;
    }

    public static void MapIntegrations(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/integrations").RequireAuthorization();

        // ---------- Webhooks ----------
        g.MapGet("/webhooks/events", async (CurrentUser cu) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            return Results.Ok(WebhookService.Catalog);
        });

        g.MapGet("/webhooks", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            var list = await db.Webhooks.OrderBy(w => w.Name).ToListAsync();
            return Results.Ok(list.Select(WebhookDto));
        });

        g.MapPost("/webhooks", async (WebhookRequest body, CurrentUser cu, YlmDbContext db, WebhookService hooks, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.BadRequest("invalid", "Bitte einen Namen angeben.");
            var isMail = body.Channel == WebhookChannel.Mail;
            var secret = isMail ? null : "whsec_" + TokenHashing.NewSecret();
            var w = new WebhookSubscription
            {
                Name = body.Name.Trim(), Channel = body.Channel,
                Url = isMail ? "" : ValidateUrl(body.Url ?? ""), Recipients = isMail ? ValidateRecipients(body.Recipients) : [],
                SecretProtected = secret is null ? "" : hooks.Protect(secret),
                Events = (body.Events ?? []).Where(e => WebhookService.Catalog.Any(c => c.Type == e)).Distinct().ToList(),
                AllTenants = body.AllTenants, TenantIds = await ValidateTenantsAsync(db, body.AllTenants, body.TenantIds), IsActive = body.IsActive, CreatedBy = user.Upn,
            };
            await hooks.EnsureCursorAsync();
            db.Webhooks.Add(w);
            audit.Add(AuditService.User(user), AuditActions.WebhookCreated, target: w.Name, details: new { channel = w.Channel.ToString(), w.Url, w.Recipients, w.Events, w.AllTenants, w.TenantIds });
            await db.SaveChangesAsync();
            return Results.Ok(new { webhook = WebhookDto(w), secret });
        });

        g.MapPut("/webhooks/{id:guid}", async (Guid id, WebhookRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var w = await db.Webhooks.FindAsync(id) ?? throw ApiException.NotFound("Webhook");
            if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.BadRequest("invalid", "Bitte einen Namen angeben.");
            w.Name = body.Name.Trim();
            if (w.Channel == WebhookChannel.Mail) w.Recipients = ValidateRecipients(body.Recipients);
            else w.Url = ValidateUrl(body.Url ?? "");
            w.Events = (body.Events ?? []).Where(e => WebhookService.Catalog.Any(c => c.Type == e)).Distinct().ToList();
            w.TenantIds = await ValidateTenantsAsync(db, body.AllTenants, body.TenantIds);
            w.AllTenants = body.AllTenants;
            w.IsActive = body.IsActive;
            audit.Add(AuditService.User(user), AuditActions.WebhookUpdated, target: w.Name, details: new { channel = w.Channel.ToString(), w.Url, w.Recipients, w.Events, w.AllTenants, w.TenantIds, w.IsActive });
            await db.SaveChangesAsync();
            return Results.Ok(WebhookDto(w));
        });

        g.MapPost("/webhooks/{id:guid}/secret", async (Guid id, CurrentUser cu, YlmDbContext db, WebhookService hooks, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var w = await db.Webhooks.FindAsync(id) ?? throw ApiException.NotFound("Webhook");
            if (w.Channel == WebhookChannel.Mail) throw ApiException.BadRequest("invalid", "Mail-Alarme haben kein Geheimnis.");
            var secret = "whsec_" + TokenHashing.NewSecret();
            w.SecretProtected = hooks.Protect(secret);
            audit.Add(AuditService.User(user), AuditActions.WebhookUpdated, target: w.Name, details: new { secretRotated = true });
            await db.SaveChangesAsync();
            return Results.Ok(new { secret });
        });

        g.MapDelete("/webhooks/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var w = await db.Webhooks.FindAsync(id) ?? throw ApiException.NotFound("Webhook");
            db.Webhooks.Remove(w);
            audit.Add(AuditService.User(user), AuditActions.WebhookDeleted, target: w.Name, details: new { channel = w.Channel.ToString(), w.Url, w.Recipients });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/webhooks/{id:guid}/test", async (Guid id, CurrentUser cu, YlmDbContext db, WebhookService hooks) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var w = await db.Webhooks.FindAsync(id) ?? throw ApiException.NotFound("Webhook");
            var d = hooks.QueueTest(w, user.DisplayName);
            await hooks.SendNowAsync(w, d);
            return Results.Ok(new { success = d.Status == DeliveryStatus.Delivered, statusCode = d.LastStatusCode, error = d.LastError });
        });

        g.MapGet("/webhooks/{id:guid}/deliveries", async (Guid id, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            var list = await db.WebhookDeliveries.AsNoTracking().Where(d => d.SubscriptionId == id).OrderByDescending(d => d.CreatedAt).Take(50)
                .Select(d => new { d.Id, d.EventType, Status = d.Status.ToString(), d.Attempts, d.LastStatusCode, d.LastError, d.CreatedAt, d.DeliveredAt, d.NextAttemptAt, d.Payload })
                .ToListAsync();
            return Results.Ok(list);
        });

        g.MapPost("/webhooks/deliveries/{id:guid}/retry", async (Guid id, CurrentUser cu, YlmDbContext db, WebhookService hooks) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            var d = await db.WebhookDeliveries.FindAsync(id) ?? throw ApiException.NotFound("Zustellung");
            var w = await db.Webhooks.FindAsync(d.SubscriptionId) ?? throw ApiException.NotFound("Webhook");
            d.Status = DeliveryStatus.Pending;
            await hooks.SendNowAsync(w, d);
            return Results.Ok(new { success = d.Status == DeliveryStatus.Delivered, statusCode = d.LastStatusCode, error = d.LastError });
        });

        // ---------- Mailserver und Mail-Berichte ----------
        g.MapGet("/smtp", async (CurrentUser cu, ReportMailService mail) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            var s = await mail.SettingsAsync();
            return Results.Ok(new { s.Host, s.Port, Security = s.Security.ToString(), s.Username, HasPassword = s.PasswordProtected != null, s.FromAddress, s.FromName, s.IsConfigured });
        });

        g.MapPut("/smtp", async (SmtpRequest body, CurrentUser cu, ReportMailService mail, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var s = await mail.SettingsAsync();
            if (body.Port is < 1 or > 65535) throw ApiException.BadRequest("invalid", "Ungültiger Port.");
            if (!string.IsNullOrWhiteSpace(body.FromAddress) && !MailAddress.TryCreate(body.FromAddress.Trim(), out _))
                throw ApiException.BadRequest("invalid", "Die Absenderadresse ist ungültig.");
            s.Host = string.IsNullOrWhiteSpace(body.Host) ? null : body.Host.Trim();
            s.Port = body.Port;
            s.Security = body.Security;
            s.Username = string.IsNullOrWhiteSpace(body.Username) ? null : body.Username.Trim();
            if (body.ClearPassword) s.PasswordProtected = null;
            else if (!string.IsNullOrEmpty(body.Password)) s.PasswordProtected = mail.ProtectPassword(body.Password);
            s.FromAddress = string.IsNullOrWhiteSpace(body.FromAddress) ? null : body.FromAddress.Trim();
            s.FromName = string.IsNullOrWhiteSpace(body.FromName) ? null : body.FromName.Trim();
            audit.Add(AuditService.User(user), AuditActions.SmtpChanged, details: new { s.Host, s.Port, security = s.Security.ToString(), s.Username, s.FromAddress, passwordChanged = body.ClearPassword || !string.IsNullOrEmpty(body.Password) });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/smtp/test", async (TestMailRequest body, CurrentUser cu, ReportMailService mail) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            if (!MailAddress.TryCreate(body.To?.Trim(), out _)) throw ApiException.BadRequest("invalid", "Bitte eine gültige Mailadresse angeben.");
            try { await mail.SendTestAsync(body.To.Trim()); }
            catch (Exception ex) when (ex is not ApiException) { throw ApiException.Conflict("smtp_failed", "Versand fehlgeschlagen: " + ex.Message); }
            return Results.NoContent();
        });

        g.MapGet("/reports", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            var list = await db.ReportSchedules.OrderBy(s => s.Name).ToListAsync();
            return Results.Ok(list.Select(ScheduleDto));
        });

        async Task<ReportSchedule> Apply(ReportSchedule s, ScheduleRequest body, YlmDbContext db)
        {
            if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.BadRequest("invalid", "Bitte einen Namen angeben.");
            if (body.Hour is < 0 or > 23 || body.DayOfWeek is < 0 or > 6 || body.DayOfMonth is < 1 or > 28) throw ApiException.BadRequest("invalid", "Ungültiger Zeitplan.");
            var tz = string.IsNullOrWhiteSpace(body.TimeZone) ? "Europe/Berlin" : body.TimeZone.Trim();
            if (!TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)) throw ApiException.BadRequest("invalid", $"Unbekannte Zeitzone „{tz}“.");
            s.Name = body.Name.Trim();
            s.Recipients = ValidateRecipients(body.Recipients);
            s.TenantIds = await ValidateTenantsAsync(db, body.AllTenants, body.TenantIds);
            s.AllTenants = body.AllTenants;
            s.Frequency = body.Frequency;
            s.DayOfWeek = body.DayOfWeek;
            s.DayOfMonth = body.DayOfMonth;
            s.Hour = body.Hour;
            s.TimeZone = tz;
            s.IncludeCsv = body.IncludeCsv;
            s.IsActive = body.IsActive;
            s.NextRunAt = s.IsActive ? ReportMailService.NextRun(s, DateTimeOffset.UtcNow) : null;
            return s;
        }

        g.MapPost("/reports", async (ScheduleRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var s = await Apply(new ReportSchedule { CreatedBy = user.Upn }, body, db);
            db.ReportSchedules.Add(s);
            audit.Add(AuditService.User(user), AuditActions.ReportScheduleChanged, target: s.Name, details: new { created = true, s.Recipients, frequency = s.Frequency.ToString(), s.AllTenants, s.TenantIds });
            await db.SaveChangesAsync();
            return Results.Ok(ScheduleDto(s));
        });

        g.MapPut("/reports/{id:guid}", async (Guid id, ScheduleRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var s = await db.ReportSchedules.FindAsync(id) ?? throw ApiException.NotFound("Bericht");
            await Apply(s, body, db);
            audit.Add(AuditService.User(user), AuditActions.ReportScheduleChanged, target: s.Name, details: new { s.Recipients, frequency = s.Frequency.ToString(), s.AllTenants, s.TenantIds, s.IsActive });
            await db.SaveChangesAsync();
            return Results.Ok(ScheduleDto(s));
        });

        g.MapDelete("/reports/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var s = await db.ReportSchedules.FindAsync(id) ?? throw ApiException.NotFound("Bericht");
            db.ReportSchedules.Remove(s);
            audit.Add(AuditService.User(user), AuditActions.ReportScheduleChanged, target: s.Name, details: new { deleted = true });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/reports/{id:guid}/send", async (Guid id, CurrentUser cu, YlmDbContext db, ReportMailService mail) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var s = await db.ReportSchedules.FindAsync(id) ?? throw ApiException.NotFound("Bericht");
            try { await mail.SendReportAsync(s, AuditService.User(user)); }
            catch (Exception ex) when (ex is not ApiException) { throw ApiException.Conflict("send_failed", "Versand fehlgeschlagen: " + ex.Message); }
            return Results.Ok(ScheduleDto(s));
        });

    }
}

/// <summary>Webhooks every few seconds, due mail reports every minute.</summary>
public sealed class IntegrationJobs(IServiceScopeFactory scopes, IOptions<YlmOptions> options, ILogger<IntegrationJobs> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnableBackgroundJobs) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        var tick = 0;
        do
        {
            try
            {
                await RunWebhooksAsync(stoppingToken);
                if (tick++ % 6 == 0) await RunScheduledAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Integration job run failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunWebhooksAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YlmDbContext>();
        if (!await db.Webhooks.AnyAsync(ct)) return;
        var hooks = scope.ServiceProvider.GetRequiredService<WebhookService>();
        await hooks.EnqueueAsync(ct);
        await hooks.DeliverAsync(ct);
    }

    public async Task RunScheduledAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ReportMailService>().RunDueAsync(ct);
    }
}
