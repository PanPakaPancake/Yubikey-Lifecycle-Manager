using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Reporting;
using Ylm.Server.Security;

namespace Ylm.Server.Audit;

public static class AuditEndpoints
{
    public static void MapAudit(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/audit").RequireAuthorization();

        g.MapGet("", async (Guid? tenantId, Guid? keyId, string? action, string? q, DateTimeOffset? from, DateTimeOffset? to, long? before, int? take, CurrentUser cu, YlmDbContext db) =>
        {
            var user = await cu.RequireAsync(Permission.ReadAudit);
            var ids = await cu.AccessibleTenantIdsAsync();
            var query = db.AuditEntries.AsNoTracking().AsQueryable();
            // Users restricted to some tenants only see entries of those tenants; installation-wide entries need all-tenant scope.
            query = user.AllTenants ? query : query.Where(a => a.TenantId != null && ids.Contains(a.TenantId.Value));
            if (tenantId is { } t) query = query.Where(a => a.TenantId == t);
            if (keyId is { } k) query = query.Where(a => a.KeyId == k);
            if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action.StartsWith(action));
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.ToLower();
                query = query.Where(a => (a.ActorName != null && a.ActorName.ToLower().Contains(term)) || (a.Target != null && a.Target.ToLower().Contains(term)));
            }
            if (from is not null) query = query.Where(a => a.Timestamp >= from);
            if (to is not null) query = query.Where(a => a.Timestamp <= to);
            if (before is not null) query = query.Where(a => a.Sequence < before);
            var items = await query.OrderByDescending(a => a.Sequence).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync();
            return Results.Ok(items.Select(a => new
            {
                a.Sequence, a.Timestamp, ActorType = a.ActorType.ToString(), a.ActorName, a.Action, a.TenantId, a.KeyId, a.Target, a.Details, a.IpAddress, a.Hash,
            }));
        });

        g.MapGet("/verify", async (CurrentUser cu, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ReadAudit);
            if (!user.AllTenants) throw ApiException.Forbidden("Die Prüfung der gesamten Kette ist nur mit Zugriff auf alle Tenants möglich.");
            var (valid, checkedCount, brokenAt) = await audit.VerifyAsync();
            return Results.Ok(new { valid, checkedEntries = checkedCount, brokenAt });
        });

        g.MapGet("/export.csv", async (Guid? tenantId, DateTimeOffset? from, DateTimeOffset? to, CurrentUser cu, ExportService ex, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ReadAudit);
            List<Guid>? ids = user.AllTenants && tenantId is null ? null : await cu.AccessibleTenantIdsAsync();
            if (tenantId is { } t)
            {
                if (ids is not null && !ids.Contains(t)) throw ApiException.Forbidden();
                ids = [t];
            }
            var csv = await ex.AuditCsvAsync(ids, from, to);
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, tenantId, details: new { kind = "audit.csv" });
            return Results.File(csv, "text/csv; charset=utf-8", $"audit-{DateTime.Today:yyyy-MM-dd}.csv");
        });
    }
}
