using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Reporting;

/// <summary>Ein Konto (Person) mit Passkey auf einem ausgegebenen Key.</summary>
public sealed record AssignmentAccount(string EntraUserId, string UserPrincipalName, string? UserDisplayName, DateTimeOffset RegisteredAt, string? RegisteredByName, string Source);

/// <summary>Ein ausgegebener Key mit allen Konten darauf und der Person, die ihn gerade hat.</summary>
/// <param name="Holder">Wer den Key in der Hand hat: bei persönlichen Keys die Person, bei geteilten Keys und Breakglass der Verwahrer.</param>
/// <param name="Issues">Auffälligkeiten, z. B. kein Konto mehr auf dem Key oder Verwahrer fehlt.</param>
public sealed record AssignmentKey(
    Guid KeyId,
    Guid TenantId,
    string TenantName,
    int? SerialNumber,
    string? Model,
    string? FirmwareVersion,
    string Status,
    string Usage,
    string? Holder,
    string? HolderUpn,
    string? Custodian,
    IReadOnlyList<AssignmentAccount> Accounts,
    DateTimeOffset? Since,
    bool? PinChangeForced,
    IReadOnlyList<string> Advisories,
    bool FidoAdvisory,
    IReadOnlyList<string> Issues);

public sealed record AssignmentSummary(int Keys, int People, int Personal, int Shared, int Breakglass, int PeopleWithSeveralKeys, int WithoutSerial, int WithAdvisory, int WithIssues);

public sealed record AssignmentOverview(DateTimeOffset GeneratedAt, AssignmentSummary Summary, IReadOnlyList<AssignmentKey> Keys);

/// <summary>Filter der Übersicht. Gilt gleich für die Web-Ansicht und alle Exporte.</summary>
public sealed record AssignmentFilter(string? Query = null, string? Usage = null, bool OnlyIssues = false);

/// <summary>Wer hat gerade welchen Key? Ausgegebene Keys (aktiv oder mit aktiven Registrierungen) samt Konten.</summary>
public sealed class AssignmentService(YlmDbContext db)
{
    public async Task<AssignmentOverview> BuildAsync(IReadOnlyList<Guid> tenantIds, AssignmentFilter? filter = null, CancellationToken ct = default)
    {
        filter ??= new AssignmentFilter();
        var keys = await db.Keys.AsNoTracking().Include(k => k.Tenant).Include(k => k.Registrations)
            .Where(k => k.TenantId != null && tenantIds.Contains(k.TenantId.Value)
                && (k.Status == KeyStatus.Active || k.Registrations.Any(r => r.RemovedAt == null)))
            .ToListAsync(ct);

        var all = keys.Select(Map).ToList();
        var filtered = Apply(all, filter)
            .OrderBy(k => k.TenantName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(k => k.Holder ?? "￿", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(k => k.SerialNumber)
            .ToList();
        return new AssignmentOverview(DateTimeOffset.UtcNow, Summarize(filtered), filtered);
    }

    private static AssignmentKey Map(YubiKey k)
    {
        var accounts = k.Registrations.Where(r => r.RemovedAt == null).OrderBy(r => r.UserDisplayName ?? r.UserPrincipalName)
            .Select(r => new AssignmentAccount(r.EntraUserId, r.UserPrincipalName, r.UserDisplayName, r.RegisteredAt, r.RegisteredByName, r.Source.ToString()))
            .ToList();
        var personal = k.Usage == KeyUsage.Personal;
        var first = accounts.FirstOrDefault();
        var issues = new List<string>();
        if (accounts.Count == 0) issues.Add(L.T("Kein Konto mehr auf dem Key registriert"));
        if (!personal && string.IsNullOrWhiteSpace(k.Custodian)) issues.Add(L.T("Verwahrer fehlt"));
        if (personal && accounts.Select(a => a.EntraUserId).Distinct().Count() > 1) issues.Add(L.T("Persönlicher Key mit mehreren Konten"));
        if (k.Status != KeyStatus.Active) issues.Add(L.F($"Status {KeyLifecycle.DisplayName(k.Status)}, aber noch Konten registriert"));
        var advisories = FirmwareAdvisories.For(k.FirmwareVersion, k.Model, k.IsFips);
        return new AssignmentKey(
            k.Id, k.TenantId!.Value, k.Tenant?.DisplayName ?? "", k.SerialNumber, k.Model, k.FirmwareVersion, k.Status.ToString(), k.Usage.ToString(),
            personal ? first?.UserDisplayName ?? first?.UserPrincipalName : k.Custodian,
            personal ? first?.UserPrincipalName : null,
            k.Custodian,
            accounts,
            accounts.Count > 0 ? accounts.Min(a => a.RegisteredAt) : null,
            k.PinChangeForced,
            advisories.Select(a => a.Id).ToList(),
            advisories.Any(a => a.AffectsFido),
            issues);
    }

    private static IEnumerable<AssignmentKey> Apply(IEnumerable<AssignmentKey> keys, AssignmentFilter f)
    {
        if (Enum.TryParse<KeyUsage>(f.Usage, true, out var usage)) keys = keys.Where(k => k.Usage == usage.ToString());
        if (f.OnlyIssues) keys = keys.Where(k => k.Issues.Count > 0);
        if (!string.IsNullOrWhiteSpace(f.Query))
        {
            var terms = f.Query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            keys = keys.Where(k => terms.All(t => Matches(k, t)));
        }
        return keys;
    }

    private static bool Matches(AssignmentKey k, string term)
    {
        bool Has(string? s) => s is not null && s.Contains(term, StringComparison.CurrentCultureIgnoreCase);
        return Has(k.SerialNumber?.ToString()) || Has(k.Holder) || Has(k.Custodian) || Has(k.Model) || Has(k.TenantName)
            || k.Accounts.Any(a => Has(a.UserPrincipalName) || Has(a.UserDisplayName));
    }

    private static AssignmentSummary Summarize(IReadOnlyList<AssignmentKey> keys)
    {
        var perPerson = keys.SelectMany(k => k.Accounts.Select(a => (k.TenantId, a.EntraUserId, k.KeyId))).Distinct()
            .GroupBy(x => (x.TenantId, x.EntraUserId)).ToList();
        return new AssignmentSummary(
            keys.Count,
            perPerson.Count,
            keys.Count(k => k.Usage == nameof(KeyUsage.Personal)),
            keys.Count(k => k.Usage == nameof(KeyUsage.Shared)),
            keys.Count(k => k.Usage == nameof(KeyUsage.Breakglass)),
            perPerson.Count(g => g.Count() > 1),
            keys.Count(k => k.SerialNumber is null),
            keys.Count(k => k.FidoAdvisory),
            keys.Count(k => k.Issues.Count > 0));
    }

    public static string UsageLabel(string usage) => usage switch
    {
        nameof(KeyUsage.Shared) => L.T("Geteilter Key"),
        nameof(KeyUsage.Breakglass) => L.T("Breakglass"),
        _ => L.T("Persönlich"),
    };

    /// <summary>Tabellenform für CSV und Excel: eine Zeile pro Person und Key (Keys ohne Konto erscheinen einmal).</summary>
    public static (string[] Header, List<object?[]> Rows) Table(AssignmentOverview o, bool includeTenant)
    {
        string[] header = [.. (includeTenant ? new[] { L.T("Tenant") } : []), L.T("Person"), "UPN", L.T("Seriennummer"), L.T("Modell"), L.T("Firmware"), L.T("Nutzung"), L.T("Hat den Key"), L.T("Konten auf dem Key"), L.T("Registriert am"), L.T("Registriert von"), L.T("PIN-Wechsel erzwungen"), L.T("Sicherheitshinweise"), L.T("Auffälligkeiten")];
        var rows = new List<object?[]>();
        foreach (var k in o.Keys)
        {
            var accounts = k.Accounts.Count > 0 ? k.Accounts : [null!];
            foreach (var a in accounts)
            {
                rows.Add([
                    .. (includeTenant ? new object?[] { k.TenantName } : []),
                    a?.UserDisplayName ?? a?.UserPrincipalName,
                    a?.UserPrincipalName,
                    k.SerialNumber,
                    k.Model,
                    k.FirmwareVersion,
                    UsageLabel(k.Usage),
                    k.Holder,
                    k.Accounts.Count,
                    a?.RegisteredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    a?.RegisteredByName,
                    k.PinChangeForced is null ? null : k.PinChangeForced.Value ? L.T("ja") : L.T("nein"),
                    string.Join(", ", k.Advisories),
                    string.Join("; ", k.Issues),
                ]);
            }
        }
        return (header, rows);
    }
}
