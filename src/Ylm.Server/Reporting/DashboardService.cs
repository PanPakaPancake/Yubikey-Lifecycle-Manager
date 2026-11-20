using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Reporting;

public sealed record FirmwareBucket(string Firmware, int Count, IReadOnlyList<string> Advisories, bool Vulnerable);
public sealed record MonthBucket(string Month, int Count);

public sealed record Dashboard(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<Guid> TenantIds,
    IReadOnlyDictionary<string, int> ByStatus,
    int InStock,
    int Total,
    int NeedsDetails,
    int? TargetUsers,
    int UsersWithKey,
    double? CoveragePercent,
    IReadOnlyList<MonthBucket> IssuedPerMonth,
    IReadOnlyList<FirmwareBucket> Firmware,
    int VulnerableKeys,
    int ForcePinChangeUnsupported,
    IReadOnlyList<object> Tenants,
    int OpenTodos = 0,
    int PendingApprovals = 0,
    bool TargetAllUsers = false);

/// <summary>Dashboard per tenant and for the whole organization (planning 7).</summary>
public sealed class DashboardService(YlmDbContext db)
{
    public async Task<Dashboard> BuildAsync(IReadOnlyList<Guid> tenantIds, DateTimeOffset? from = null, CancellationToken ct = default)
    {
        var keys = await db.Keys.AsNoTracking().Where(k => k.TenantId != null && tenantIds.Contains(k.TenantId.Value))
            .Select(k => new { k.TenantId, k.Status, k.FirmwareVersion, k.SerialNumber }).ToListAsync(ct);
        var regs = await db.Registrations.AsNoTracking().Where(r => tenantIds.Contains(r.TenantId))
            .Select(r => new { r.TenantId, r.EntraUserId, r.RegisteredAt, r.RemovedAt, r.Source }).ToListAsync(ct);
        var tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id)).OrderBy(t => t.DisplayName).ToListAsync(ct);
        var openTodos = await db.Todos.CountAsync(t => tenantIds.Contains(t.TenantId) && t.Status == TodoStatus.Open, ct);
        var pendingApprovals = await db.Approvals.CountAsync(a => tenantIds.Contains(a.TenantId) && a.Status == ApprovalStatus.Pending, ct);

        var byStatus = Enum.GetValues<KeyStatus>().ToDictionary(s => s.ToString(), s => keys.Count(k => k.Status == s));
        var activeKeys = keys.Where(k => k.Status == KeyStatus.Active).ToList();
        var usersWithKey = regs.Where(r => r.RemovedAt is null).Select(r => (r.TenantId, r.EntraUserId)).Distinct().Count();
        int? targetUsers = tenants.All(t => t.TargetUserCount is null) ? null : tenants.Sum(t => t.TargetUserCount ?? 0);

        var start = from ?? new DateTimeOffset(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(-11);
        var months = new List<MonthBucket>();
        for (var mth = new DateTimeOffset(start.Year, start.Month, 1, 0, 0, 0, TimeSpan.Zero); mth <= DateTimeOffset.UtcNow; mth = mth.AddMonths(1))
        {
            var next = mth.AddMonths(1);
            months.Add(new MonthBucket(mth.ToString("yyyy-MM"),
                regs.Count(r => r.Source == KeySource.Platform && r.RegisteredAt >= mth && r.RegisteredAt < next)));
        }

        var firmware = keys.Where(k => k.Status is KeyStatus.Active or KeyStatus.InStock && k.FirmwareVersion != null)
            .GroupBy(k => k.FirmwareVersion!)
            .Select(gr =>
            {
                var adv = FirmwareAdvisories.For(gr.Key);
                return new FirmwareBucket(gr.Key, gr.Count(), adv.Select(a => a.Id).ToList(), adv.Any(a => a.AffectsFido));
            })
            .OrderByDescending(f => FirmwareVersion.TryParse(f.Firmware, out var v) ? v : default).ToList();

        var forceUnsupported = activeKeys.Count(k => FirmwareVersion.TryParse(k.FirmwareVersion, out var v) && v < FirmwareVersion.ForcePinChangeMinimum);

        return new Dashboard(
            DateTimeOffset.UtcNow,
            tenantIds,
            byStatus,
            byStatus[KeyStatus.InStock.ToString()],
            keys.Count,
            keys.Count(k => k.SerialNumber is null && k.Status == KeyStatus.Active),
            targetUsers,
            usersWithKey,
            targetUsers is > 0 ? Math.Round(100.0 * Math.Min(usersWithKey, targetUsers.Value) / targetUsers.Value, 1) : null,
            months,
            firmware,
            firmware.Where(f => f.Vulnerable).Sum(f => f.Count),
            forceUnsupported,
            tenants.Select(t => (object)new
            {
                t.Id, t.DisplayName, t.LastSyncAt, t.LastSyncSucceeded, t.TargetUserCount,
                Active = keys.Count(k => k.TenantId == t.Id && k.Status == KeyStatus.Active),
                InStock = keys.Count(k => k.TenantId == t.Id && k.Status == KeyStatus.InStock),
                UsersWithKey = regs.Where(r => r.TenantId == t.Id && r.RemovedAt == null).Select(r => r.EntraUserId).Distinct().Count(),
                ChecksFailed = Tenants.TenantCheckService.ReadChecks(t).Count(c => c.Status == CheckStatus.Failed),
            }).ToList(),
            openTodos,
            pendingApprovals,
            // No include groups anywhere: the target audience is every enabled member account.
            tenants.Count > 0 && tenants.All(t => t.TargetIncludeGroups.Count == 0));
    }
}
