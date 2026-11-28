using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Reporting;

public sealed record AdvisoryPerson(string UserPrincipalName, string? DisplayName);

/// <summary>Ein betroffener Key (nicht ausgemustert) mit den Personen, deren Konten darauf liegen.</summary>
public sealed record AdvisoryKey(
    Guid KeyId, Guid? TenantId, string TenantName, int? SerialNumber, string? Model, string Series, string? FirmwareVersion,
    string Status, string Usage, string? Custodian, IReadOnlyList<AdvisoryPerson> People);

/// <summary>Ein Yubico-Sicherheitshinweis mit den betroffenen Keys im gewählten Bereich.</summary>
public sealed record AdvisoryEntry(
    string Id, string Title, string Severity, string Url, DateOnly Published, bool AffectsFido, string Applications,
    string Summary, string Recommendation, string FixedIn, IReadOnlyList<string> Affected,
    int Keys, int People, IReadOnlyDictionary<string, int> ByStatus, IReadOnlyList<AdvisoryKey> Items);

/// <param name="KeysChecked">Keys (nicht ausgemustert) mit bekannter Firmware.</param>
/// <param name="KeysWithoutFirmware">Keys ohne Firmware-Angabe, z. B. selbst registriert und noch nicht nacherfasst.</param>
/// <param name="AffectedKeys">Keys mit mindestens einem Hinweis, der Passkeys (FIDO2/U2F) betrifft.</param>
public sealed record AdvisoryOverview(
    DateTimeOffset GeneratedAt, int KeysChecked, int KeysWithoutFirmware, int AffectedKeys, int AffectedPeople, IReadOnlyList<AdvisoryEntry> Advisories);

/// <summary>Welche Keys sind von welchem Yubico-Sicherheitshinweis betroffen (Liste in <see cref="FirmwareAdvisories"/>)?</summary>
public sealed class AdvisoryService(YlmDbContext db)
{
    /// <param name="includePartnerStock">Auch Keys im Partnerlager prüfen (noch keinem Kunden zugeordnet).</param>
    public async Task<AdvisoryOverview> BuildAsync(IReadOnlyList<Guid> tenantIds, bool includePartnerStock, CancellationToken ct = default)
    {
        var keys = await db.Keys.AsNoTracking().Include(k => k.Tenant).Include(k => k.Registrations)
            .Where(k => k.Status != KeyStatus.Retired)
            .Where(k => (k.TenantId != null && tenantIds.Contains(k.TenantId.Value)) || (includePartnerStock && k.TenantId == null))
            .OrderBy(k => k.Tenant!.DisplayName).ThenBy(k => k.SerialNumber).ToListAsync(ct);
        var withFirmware = keys.Where(k => FirmwareVersion.TryParse(k.FirmwareVersion, out _)).ToList();
        var hits = withFirmware.Select(k => (Key: k, Advisories: FirmwareAdvisories.For(k.FirmwareVersion, k.Model, k.IsFips))).ToList();

        var entries = FirmwareAdvisories.Known.Select(a =>
        {
            var items = hits.Where(h => h.Advisories.Contains(a)).Select(h => Map(h.Key)).ToList();
            return new AdvisoryEntry(a.Id, a.Title, a.Severity, a.Url, a.Published, a.AffectsFido, a.Applications, a.Summary, a.Recommendation, a.FixedIn,
                a.Ranges.Select(r => r.Describe()).ToList(), items.Count, CountPeople(items),
                new[] { KeyStatus.Active, KeyStatus.InStock, KeyStatus.Locked }.ToDictionary(s => s.ToString(), s => items.Count(i => i.Status == s.ToString())),
                items);
        }).ToList();

        var fido = hits.Where(h => h.Advisories.Any(a => a.AffectsFido)).Select(h => Map(h.Key)).ToList();
        return new AdvisoryOverview(DateTimeOffset.UtcNow, withFirmware.Count, keys.Count - withFirmware.Count, fido.Count, CountPeople(fido), entries);
    }

    private static int CountPeople(IEnumerable<AdvisoryKey> items) =>
        items.SelectMany(i => i.People).Select(p => p.UserPrincipalName.ToLowerInvariant()).Distinct().Count();

    private static AdvisoryKey Map(YubiKey k) => new(
        k.Id, k.TenantId, k.TenantId is null ? L.T("Partnerlager") : k.Tenant?.DisplayName ?? "", k.SerialNumber, k.Model,
        KeySeriesInfo.DisplayName(KeySeriesInfo.Detect(k.Model, k.IsFips, k.FirmwareVersion)), k.FirmwareVersion,
        k.Status.ToString(), k.Usage.ToString(), k.Custodian,
        k.Registrations.Where(r => r.RemovedAt == null).OrderBy(r => r.UserDisplayName ?? r.UserPrincipalName)
            .Select(r => new AdvisoryPerson(r.UserPrincipalName, r.UserDisplayName)).DistinctBy(p => p.UserPrincipalName.ToLowerInvariant()).ToList());

    /// <summary>Tabelle für CSV und Excel: eine Zeile pro Hinweis und Key. advisory: nur dieser Hinweis.</summary>
    public static (string[] Header, List<object?[]> Rows) Table(AdvisoryOverview o, string? advisory, bool includeTenant)
    {
        string[] header = [L.T("Hinweis"), L.T("Titel"), L.T("Einstufung"), L.T("Betrifft Passkeys"), .. (includeTenant ? new[] { L.T("Tenant") } : []),
            L.T("Seriennummer"), L.T("Modell"), L.T("Reihe"), L.T("Firmware"), L.T("Status"), L.T("Nutzung"), L.T("Personen"), L.T("Verwahrer"), L.T("Behoben ab Firmware"), L.T("Empfehlung")];
        var rows = new List<object?[]>();
        foreach (var a in o.Advisories.Where(a => advisory is null || string.Equals(a.Id, advisory, StringComparison.OrdinalIgnoreCase)))
        foreach (var k in a.Items)
        {
            rows.Add([a.Id, a.Title, a.Severity, a.AffectsFido ? L.T("ja") : L.T("nein"), .. (includeTenant ? new object?[] { k.TenantName } : []),
                k.SerialNumber, k.Model, k.Series, k.FirmwareVersion, KeyLifecycle.DisplayName(Enum.Parse<KeyStatus>(k.Status)), UsageLabel(k.Usage),
                string.Join(", ", k.People.Select(p => p.DisplayName is null ? p.UserPrincipalName : $"{p.DisplayName} ({p.UserPrincipalName})")),
                k.Custodian, a.FixedIn, a.Recommendation]);
        }
        return (header, rows);
    }

    private static string UsageLabel(string usage) => usage switch
    {
        nameof(KeyUsage.Shared) => L.T("Geteilt"),
        nameof(KeyUsage.Breakglass) => L.T("Breakglass"),
        _ => L.T("Persönlich"),
    };
}
