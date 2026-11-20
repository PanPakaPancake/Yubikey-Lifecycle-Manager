using Microsoft.Extensions.Caching.Memory;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Protection;

/// <summary>Warum ein Konto geschützt ist (für Meldungen und Audit).</summary>
public sealed record ProtectionStatus(bool IsProtected, string? Reason, bool AdminStatusKnown);

/// <summary>
/// Geschützte Konten: Konten mit Entra-Admin-Rolle und Mitglieder geschützter Gruppen. Für sie erzeugen nur Admins mit
/// „Geschützte Konten bearbeiten“ einen TAP, entfernen Methoden oder registrieren Keys. Sonst könnte z. B. ein Operator
/// per TAP das Konto eines Globalen Administrators im Kunden-Tenant übernehmen.
///
/// Admin-Erkennung über den Registrierungsbericht (isAdmin, braucht Entra ID P1). Ohne P1 ist der Admin-Status unbekannt:
/// Dann schützt nur die Gruppe, das steht in den Tenant-Einstellungen.
/// </summary>
public sealed class ProtectedAccountService(IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    public const string ErrorCode = "protected_account";

    public static bool CanManage(PlatformUser actor) => CurrentUser.Has(actor, Permission.ManageProtectedAccounts);

    public async Task<ProtectionStatus> CheckAsync(Tenant tenant, IGraphClient graph, string userId, CancellationToken ct = default)
    {
        bool? admin = null;
        if (tenant.ProtectAdmins)
        {
            admin = await cache.GetOrCreateAsync($"protect:admin:{tenant.Id}:{userId}", async e =>
            {
                e.AbsoluteExpirationRelativeToNow = CacheFor;
                return await graph.IsAdminAsync(userId, ct);
            });
            if (admin == true) return new(true, "Konto mit Entra-Admin-Rolle", true);
        }
        if (tenant.ProtectedGroups.Count > 0)
        {
            var groups = tenant.ProtectedGroups.Select(g => g.Id).ToList();
            var hits = await graph.CheckMemberGroupsAsync(userId, groups, ct);
            if (hits.Count > 0)
            {
                var name = tenant.ProtectedGroups.First(g => hits.Contains(g.Id)).Name;
                return new(true, $"Mitglied der geschützten Gruppe „{name}“", admin is not null || !tenant.ProtectAdmins);
            }
        }
        return new(false, null, admin is not null || !tenant.ProtectAdmins);
    }

    /// <summary>Null = erlaubt, sonst die Meldung für den Admin.</summary>
    public async Task<string?> DenyReasonAsync(Tenant tenant, IGraphClient graph, PlatformUser actor, string userId, CancellationToken ct = default)
    {
        if (CanManage(actor)) return null;
        var p = await CheckAsync(tenant, graph, userId, ct);
        return p.IsProtected
            ? $"Geschütztes Konto ({p.Reason}). Das darf nur, wer das Recht „Geschützte Konten bearbeiten“ hat, standardmäßig ein Owner."
            : null;
    }

    public async Task EnsureAllowedAsync(Tenant tenant, IGraphClient graph, PlatformUser actor, IEnumerable<string> userIds, CancellationToken ct = default)
    {
        if (CanManage(actor)) return;
        foreach (var id in userIds.Distinct())
            if (await DenyReasonAsync(tenant, graph, actor, id, ct) is { } reason)
                throw new ApiException(403, ErrorCode, reason);
    }

    /// <summary>Für Endpunkte: lädt Tenant und Graph nur, wenn eine Prüfung nötig ist.</summary>
    public async Task EnsureAllowedAsync(YlmDbContext db, IGraphClientFactory graphs, Guid tenantId, PlatformUser actor, IEnumerable<string> userIds, CancellationToken ct = default)
    {
        if (CanManage(actor)) return;
        var tenant = await db.Tenants.FindAsync([tenantId], ct) ?? throw ApiException.NotFound("Tenant");
        if (!tenant.ProtectAdmins && tenant.ProtectedGroups.Count == 0) return;
        var ids = userIds.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (ids.Count == 0) return;
        await EnsureAllowedAsync(tenant, await graphs.ForTenantAsync(tenant.EntraTenantId, ct), actor, ids, ct);
    }
}
