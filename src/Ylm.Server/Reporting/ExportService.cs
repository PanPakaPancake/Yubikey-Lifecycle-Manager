using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Reporting;

public sealed class ExportService(YlmDbContext db)
{
    public async Task<byte[]> KeysCsvAsync(IReadOnlyList<Guid> tenantIds, CancellationToken ct = default)
    {
        var keys = await db.Keys.AsNoTracking().Include(k => k.Tenant).Include(k => k.Registrations)
            .Where(k => tenantIds.Contains(k.TenantId)).OrderBy(k => k.Tenant!.DisplayName).ThenBy(k => k.SerialNumber).ToListAsync(ct);
        var fieldKeys = await db.CustomFields.AsNoTracking().Where(f => tenantIds.Contains(f.TenantId)).OrderBy(f => f.SortOrder)
            .Select(f => f.Key).Distinct().ToListAsync(ct);
        string[] header = ["Tenant", "Seriennummer", "Modell", "Firmware", "Status", "Sperrgrund", "Quelle", "Personen", "Registriert am", "PIN-Wechsel erzwungen", "Tags", "Notiz", "Warnungen", .. fieldKeys];
        var rows = keys.Select(k =>
        {
            var active = k.Registrations.Where(r => r.RemovedAt == null).ToList();
            return (IReadOnlyList<string?>)
            [
                k.Tenant?.DisplayName, k.SerialNumber?.ToString(), k.Model, k.FirmwareVersion, KeyLifecycle.DisplayName(k.Status),
                k.LockReason is { } lr ? KeyLifecycle.DisplayName(lr) : null, k.Source.ToString(),
                string.Join(", ", active.Select(r => r.UserPrincipalName)),
                active.Count > 0 ? active.Min(r => r.RegisteredAt).ToString("yyyy-MM-dd HH:mm") : null,
                k.PinChangeForced is null ? null : k.PinChangeForced.Value ? "ja" : "nein",
                string.Join(", ", k.Tags), k.Notes,
                string.Join(", ", FirmwareAdvisories.For(k.FirmwareVersion).Select(a => a.Id)),
                .. fieldKeys.Select(f => k.CustomFields.GetValueOrDefault(f)),
            ];
        });
        return Csv.Write(header, rows);
    }

    public async Task<byte[]> RegistrationsCsvAsync(IReadOnlyList<Guid> tenantIds, CancellationToken ct = default)
    {
        var regs = await db.Registrations.AsNoTracking().Include(r => r.Key)
            .Where(r => tenantIds.Contains(r.TenantId)).OrderBy(r => r.RegisteredAt).ToListAsync(ct);
        string[] header = ["Person", "Name", "Seriennummer", "Anzeigename in Entra", "Registriert am", "Registriert von", "Quelle", "Entfernt am", "Grund"];
        return Csv.Write(header, regs.Select(r => (IReadOnlyList<string?>)
        [
            r.UserPrincipalName, r.UserDisplayName, r.Key?.SerialNumber?.ToString(), r.DisplayName, r.RegisteredAt.ToString("yyyy-MM-dd HH:mm"),
            r.RegisteredByName, r.Source.ToString(), r.RemovedAt?.ToString("yyyy-MM-dd HH:mm"), r.RemovedReason,
        ]));
    }

    public async Task<byte[]> AuditCsvAsync(IReadOnlyList<Guid>? tenantIds, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct = default)
    {
        var q = db.AuditEntries.AsNoTracking().AsQueryable();
        if (tenantIds is not null) q = q.Where(a => a.TenantId != null && tenantIds.Contains(a.TenantId.Value));
        if (from is not null) q = q.Where(a => a.Timestamp >= from);
        if (to is not null) q = q.Where(a => a.Timestamp <= to);
        var entries = await q.OrderBy(a => a.Sequence).Take(100_000).ToListAsync(ct);
        string[] header = ["Nr", "Zeitpunkt (UTC)", "Akteur", "Aktion", "Tenant", "Key", "Ziel", "Details", "IP", "Hash"];
        return Csv.Write(header, entries.Select(a => (IReadOnlyList<string?>)
        [
            a.Sequence.ToString(), a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), a.ActorName, a.Action, a.TenantId?.ToString(), a.KeyId?.ToString(),
            a.Target, a.Details, a.IpAddress, a.Hash,
        ]));
    }
}
