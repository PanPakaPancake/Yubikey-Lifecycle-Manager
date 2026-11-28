using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Reporting;

public sealed class ExportService(YlmDbContext db)
{
    public async Task<byte[]> KeysCsvAsync(IReadOnlyList<Guid> tenantIds, CancellationToken ct = default)
    {
        var keys = await db.Keys.AsNoTracking().Include(k => k.Tenant).Include(k => k.Registrations)
            .Where(k => k.TenantId != null && tenantIds.Contains(k.TenantId.Value)).OrderBy(k => k.Tenant!.DisplayName).ThenBy(k => k.SerialNumber).ToListAsync(ct);
        // Global fields first, then the tenants' own fields.
        var fieldKeys = (await db.CustomFields.AsNoTracking().Where(f => f.TenantId == null || tenantIds.Contains(f.TenantId.Value))
                .OrderBy(f => f.TenantId != null).ThenBy(f => f.SortOrder).Select(f => f.Key).ToListAsync(ct))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string[] header = [L.T("Tenant"), L.T("Seriennummer"), L.T("Modell"), L.T("Firmware"), L.T("Status"), L.T("Sperrgrund"), L.T("Quelle"), L.T("Personen"), L.T("Registriert am"), L.T("PIN-Wechsel erzwungen"), L.T("Tags"), L.T("Notiz"), L.T("Warnungen"), .. fieldKeys];
        var rows = keys.Select(k =>
        {
            var active = k.Registrations.Where(r => r.RemovedAt == null).ToList();
            return (IReadOnlyList<string?>)
            [
                k.Tenant?.DisplayName, k.SerialNumber?.ToString(), k.Model, k.FirmwareVersion, KeyLifecycle.DisplayName(k.Status),
                k.LockReason is { } lr ? KeyLifecycle.DisplayName(lr) : null, k.Source.ToString(),
                string.Join(", ", active.Select(r => r.UserPrincipalName)),
                active.Count > 0 ? active.Min(r => r.RegisteredAt).ToString("yyyy-MM-dd HH:mm") : null,
                k.PinChangeForced is null ? null : k.PinChangeForced.Value ? L.T("ja") : L.T("nein"),
                string.Join(", ", k.Tags), k.Notes,
                string.Join(", ", FirmwareAdvisories.For(k.FirmwareVersion, k.Model, k.IsFips).Select(a => a.Id)),
                .. fieldKeys.Select(f => k.CustomFields.GetValueOrDefault(f)),
            ];
        });
        return Csv.Write(header, rows);
    }

    public async Task<byte[]> RegistrationsCsvAsync(IReadOnlyList<Guid> tenantIds, CancellationToken ct = default)
    {
        var regs = await db.Registrations.AsNoTracking().Include(r => r.Key)
            .Where(r => tenantIds.Contains(r.TenantId)).OrderBy(r => r.RegisteredAt).ToListAsync(ct);
        string[] header = [L.T("Person"), L.T("Name"), L.T("Seriennummer"), L.T("Anzeigename in Entra"), L.T("Registriert am"), L.T("Registriert von"), L.T("Quelle"), L.T("Entfernt am"), L.T("Grund")];
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
        string[] header = ["Nr", L.T("Zeitpunkt (UTC)"), L.T("Akteur"), L.T("Aktion"), L.T("Tenant"), L.T("Key"), L.T("Ziel"), L.T("Details"), "IP", L.T("Hash")];
        return Csv.Write(header, entries.Select(a => (IReadOnlyList<string?>)
        [
            a.Sequence.ToString(), a.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"), a.ActorName, a.Action, a.TenantId?.ToString(), a.KeyId?.ToString(),
            a.Target, a.Details, a.IpAddress, a.Hash,
        ]));
    }
}
