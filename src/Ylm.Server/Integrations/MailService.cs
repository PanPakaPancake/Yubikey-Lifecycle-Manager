using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Pdf;
using Ylm.Server.Reporting;

namespace Ylm.Server.Integrations;

public sealed record MailAttachment(string FileName, string ContentType, byte[] Content);

public sealed record OutgoingMail(IReadOnlyList<string> To, string Subject, string Html, string Text, IReadOnlyList<MailAttachment> Attachments);

/// <summary>Sends mail via SMTP. Replaced by a fake in tests.</summary>
public interface IMailSender
{
    Task SendAsync(SmtpSettings settings, string? password, OutgoingMail mail, CancellationToken ct = default);
}

public sealed class SmtpMailSender : IMailSender
{
    public async Task SendAsync(SmtpSettings s, string? password, OutgoingMail mail, CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(s.FromName ?? "Yubikey Lifecycle Manager", s.FromAddress));
        foreach (var to in mail.To) msg.To.Add(MailboxAddress.Parse(to));
        msg.Subject = mail.Subject;
        var body = new BodyBuilder { HtmlBody = mail.Html, TextBody = mail.Text };
        foreach (var a in mail.Attachments) body.Attachments.Add(a.FileName, a.Content, ContentType.Parse(a.ContentType));
        msg.Body = body.ToMessageBody();
        using var client = new SmtpClient { Timeout = 30_000 };
        var security = s.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };
        await client.ConnectAsync(s.Host, s.Port, security, ct);
        if (!string.IsNullOrWhiteSpace(s.Username)) await client.AuthenticateAsync(s.Username, password ?? "", ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
    }
}

/// <summary>Mail-Berichte (planning 7, V2): das Dashboard als PDF regelmäßig an feste Empfänger.</summary>
public sealed class ReportMailService(YlmDbContext db, IMailSender sender, DashboardService dashboards, ExportService exports, IDataProtectionProvider dataProtection,
    AuditService audit, ILogger<ReportMailService> logger)
{
    public const string PasswordPurpose = "Ylm.Smtp.Password";

    public string ProtectPassword(string password) => dataProtection.CreateProtector(PasswordPurpose).Protect(password);

    public async Task<SmtpSettings> SettingsAsync(CancellationToken ct = default)
    {
        var s = await db.Smtp.FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new SmtpSettings();
        db.Smtp.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    private async Task SendAsync(OutgoingMail mail, CancellationToken ct)
    {
        var s = await SettingsAsync(ct);
        if (!s.IsConfigured) throw ApiException.Conflict("smtp_missing", "Bitte zuerst den Mailserver einrichten.");
        var password = s.PasswordProtected is null ? null : dataProtection.CreateProtector(PasswordPurpose).Unprotect(s.PasswordProtected);
        await sender.SendAsync(s, password, mail, ct);
    }

    public Task SendTestAsync(string to, CancellationToken ct = default) => SendAsync(new OutgoingMail([to], "Testmail vom Yubikey Lifecycle Manager",
        "<p>Der Mailversand für die Mail-Berichte funktioniert.</p>", "Der Mailversand für die Mail-Berichte funktioniert.", []), ct);

    /// <summary>Next run after <paramref name="after"/> in the schedule's time zone.</summary>
    public static DateTimeOffset NextRun(ReportSchedule s, DateTimeOffset after)
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById(s.TimeZone); } catch (Exception) { tz = TimeZoneInfo.Utc; }
        var local = TimeZoneInfo.ConvertTime(after, tz);
        var day = local.Date;
        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            var fits = s.Frequency switch
            {
                ReportFrequency.Daily => true,
                ReportFrequency.Weekly => (int)day.DayOfWeek == s.DayOfWeek,
                _ => day.Day == Math.Clamp(s.DayOfMonth, 1, 28),
            };
            if (!fits) continue;
            var candidate = day.AddHours(Math.Clamp(s.Hour, 0, 23));
            var offset = tz.GetUtcOffset(candidate);
            var at = new DateTimeOffset(candidate, offset);
            if (at > after) return at.ToUniversalTime();
        }
        return after.AddDays(1);
    }

    public static string Describe(ReportSchedule s)
    {
        string[] days = ["Sonntag", "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag"];
        return s.Frequency switch
        {
            ReportFrequency.Daily => $"täglich um {s.Hour}:00 Uhr",
            ReportFrequency.Weekly => $"jeden {days[Math.Clamp(s.DayOfWeek, 0, 6)]} um {s.Hour}:00 Uhr",
            _ => $"monatlich am {s.DayOfMonth}. um {s.Hour}:00 Uhr",
        };
    }

    public async Task SendReportAsync(ReportSchedule s, AuditActor actor, CancellationToken ct = default)
    {
        var tenants = await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active && (s.AllTenants || s.TenantIds.Contains(t.Id))).OrderBy(t => t.DisplayName).ToListAsync(ct);
        var ids = tenants.Select(t => t.Id).ToList();
        try
        {
            if (s.Recipients.Count == 0) throw ApiException.BadRequest("no_recipients", "Der Bericht hat keine Empfänger.");
            var org = (await db.Installation.AsNoTracking().FirstAsync(ct)).OrganizationName;
            var scope = s.AllTenants ? "Alle Tenants" : tenants.Count == 1 ? tenants[0].DisplayName : $"{tenants.Count} Tenants";
            var d = await dashboards.BuildAsync(ids, ct: ct);
            var openTodos = await db.Todos.CountAsync(t => ids.Contains(t.TenantId) && t.Status == TodoStatus.Open, ct);
            var pendingApprovals = await db.Approvals.CountAsync(a => ids.Contains(a.TenantId) && a.Status == ApprovalStatus.Pending, ct);
            var attachments = new List<MailAttachment> { new($"yubikey-bericht-{DateTime.Today:yyyy-MM-dd}.pdf", "application/pdf", ReportPdf.Create(org, scope, d)) };
            if (s.IncludeCsv) attachments.Add(new($"yubikeys-{DateTime.Today:yyyy-MM-dd}.csv", "text/csv", await exports.KeysCsvAsync(ids, ct)));
            var rows = new List<(string Label, string Value)>
            {
                ("Keys gesamt", d.Total.ToString()),
                ("Aktiv", d.ByStatus.GetValueOrDefault("Active").ToString()),
                ("Im Lager", d.InStock.ToString()),
                ("Gesperrt", d.ByStatus.GetValueOrDefault("Locked").ToString()),
                ("Personen mit Key", d.UsersWithKey.ToString()),
                ("Abdeckung der Zielgruppe", d.CoveragePercent is { } c ? $"{c:0.#} %" : "–"),
                ("Keys mit Sicherheitshinweis", d.VulnerableKeys.ToString()),
                ("Ohne Seriennummer (nachzuerfassen)", d.NeedsDetails.ToString()),
                ("Offene ToDos", openTodos.ToString()),
                ("Offene Freigaben", pendingApprovals.ToString()),
            };
            var html = new StringBuilder();
            html.Append($"<h2 style=\"font-family:sans-serif\">{WebUtility.HtmlEncode(s.Name)}</h2>");
            html.Append($"<p style=\"font-family:sans-serif;color:#555\">{WebUtility.HtmlEncode(org)} · {WebUtility.HtmlEncode(scope)} · Stand {DateTime.Now:dd.MM.yyyy HH:mm}</p>");
            html.Append("<table style=\"font-family:sans-serif;border-collapse:collapse\">");
            foreach (var (label, value) in rows)
                html.Append($"<tr><td style=\"padding:4px 16px 4px 0;color:#555\">{WebUtility.HtmlEncode(label)}</td><td style=\"padding:4px 0;font-weight:600\">{WebUtility.HtmlEncode(value)}</td></tr>");
            html.Append("</table><p style=\"font-family:sans-serif;color:#555\">Den vollständigen Bericht finden Sie im PDF im Anhang.</p>");
            var text = $"{s.Name}\n{org} · {scope}\n\n" + string.Join("\n", rows.Select(r => $"{r.Label}: {r.Value}")) + "\n\nDer vollständige Bericht steht im PDF im Anhang.";
            await SendAsync(new OutgoingMail(s.Recipients, $"{s.Name} · {scope} · {DateTime.Today:dd.MM.yyyy}", html.ToString(), text, attachments), ct);
            s.LastSentAt = DateTimeOffset.UtcNow;
            s.LastSucceeded = true;
            s.LastError = null;
            audit.Add(actor, AuditActions.ReportSent, ids.Count == 1 ? ids[0] : null, target: s.Name, details: new { recipients = s.Recipients.Count, scope });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Report {Name} could not be sent", s.Name);
            s.LastSentAt = DateTimeOffset.UtcNow;
            s.LastSucceeded = false;
            s.LastError = ex.Message;
            audit.Add(actor, AuditActions.ReportFailed, null, target: s.Name, details: new { error = ex.Message });
            await db.SaveChangesAsync(ct);
            throw;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Sends all reports that are due. Errors are stored at the schedule.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await db.ReportSchedules.Where(s => s.IsActive && s.NextRunAt != null && s.NextRunAt <= now).ToListAsync(ct);
        foreach (var s in due)
        {
            s.NextRunAt = NextRun(s, now);
            try { await SendReportAsync(s, AuditService.System, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* stored at the schedule */ }
        }
        return due.Count;
    }
}
