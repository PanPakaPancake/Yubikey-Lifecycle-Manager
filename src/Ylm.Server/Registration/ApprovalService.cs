using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Registration;

/// <summary>
/// Vier-Augen-Freigabe für Breakglass-Registrierungen (planning 6.3, V2). Standardmäßig aus, pro Tenant aktivierbar.
/// Admin A beantragt, ein zweiter Admin B mit dem Recht „Breakglass freigeben“ gibt frei, danach führt A die Registrierung einmal aus.
/// </summary>
public static class ApprovalService
{
    public static readonly TimeSpan Validity = TimeSpan.FromHours(24);

    public sealed record CreateApprovalRequest(List<string> EntraUserIds, string? Custodian, string Reason);
    public sealed record DecisionRequest(string? Note);

    public static object ToDto(RegistrationApproval a) => new
    {
        a.Id, a.TenantId, Usage = a.Usage.ToString(), a.EntraUserIds, a.UserPrincipalNames, a.Custodian, a.Reason, Status = a.Status.ToString(),
        a.RequestedById, a.RequestedByName, a.RequestedAt, a.DecidedByName, a.DecidedAt, a.DecisionNote, a.ValidUntil, a.UsedAt, a.KeyId,
        Expired = a.Status == ApprovalStatus.Approved && a.ValidUntil < DateTimeOffset.UtcNow,
    };

    /// <summary>Freigaben, die der Admin jetzt für einen Lauf verwenden kann.</summary>
    public static async Task<List<RegistrationApproval>> UsableAsync(YlmDbContext db, Guid tenantId, Guid actorId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await db.Approvals.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.RequestedById == actorId && a.Status == ApprovalStatus.Approved && a.ValidUntil > now)
            .OrderByDescending(a => a.DecidedAt).ToListAsync(ct);
    }

    public static async Task<RegistrationApproval> RequireUsableAsync(YlmDbContext db, Guid tenantId, Guid actorId, Guid? approvalId, IReadOnlyCollection<string> entraUserIds, CancellationToken ct = default)
    {
        if (approvalId is null)
            throw ApiException.Conflict("approval_required", "Für Breakglass-Keys verlangt dieser Tenant eine Vier-Augen-Freigabe. Bitte zuerst eine Freigabe beantragen.");
        var a = await db.Approvals.FirstOrDefaultAsync(x => x.Id == approvalId && x.TenantId == tenantId, ct) ?? throw ApiException.NotFound("Freigabe");
        if (a.RequestedById != actorId) throw ApiException.Conflict("approval_not_yours", "Diese Freigabe wurde von einem anderen Admin beantragt.");
        if (a.Status != ApprovalStatus.Approved) throw ApiException.Conflict("approval_not_approved", "Die Freigabe ist nicht (mehr) erteilt.");
        if (a.ValidUntil <= DateTimeOffset.UtcNow) throw ApiException.Conflict("approval_expired", "Die Freigabe ist abgelaufen. Bitte neu beantragen.");
        var allowed = a.EntraUserIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = entraUserIds.Where(id => !allowed.Contains(id)).ToList();
        if (extra.Count > 0) throw ApiException.Conflict("approval_mismatch", "Die Freigabe gilt nicht für alle gewählten Konten. Bitte nur freigegebene Konten registrieren oder neu beantragen.");
        return a;
    }

    public static void MarkUsed(RegistrationApproval a, Guid keyId, AuditActor actor, AuditService audit)
    {
        a.Status = ApprovalStatus.Used;
        a.UsedAt = DateTimeOffset.UtcNow;
        a.KeyId = keyId;
        audit.Add(actor, AuditActions.ApprovalUsed, a.TenantId, keyId, string.Join(", ", a.UserPrincipalNames), new { approval = a.Id });
    }

    public static void MapApprovals(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // Offene und letzte Freigaben; ohne tenantId alle zuständigen Tenants (für „Handlungsbedarf“).
        g.MapGet("/approvals", async (Guid? tenantId, string? status, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            var ids = await cu.AccessibleTenantIdsAsync();
            if (tenantId is { } t) ids = ids.Where(x => x == t).ToList();
            var q = db.Approvals.AsNoTracking().Where(a => ids.Contains(a.TenantId));
            if (Enum.TryParse<ApprovalStatus>(status, true, out var st)) q = q.Where(a => a.Status == st);
            var items = await q.OrderByDescending(a => a.RequestedAt).Take(200).ToListAsync();
            var names = await db.Tenants.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName);
            return Results.Ok(items.Select(a => new { approval = ToDto(a), tenantName = names.GetValueOrDefault(a.TenantId) }));
        });

        g.MapPost("/tenants/{tenantId:guid}/approvals", async (Guid tenantId, CreateApprovalRequest body, CurrentUser cu, YlmDbContext db, IGraphClientFactory graphs, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);
            var ids = (body.EntraUserIds ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
            if (ids.Count == 0) throw ApiException.BadRequest("no_users", "Bitte mindestens ein Konto auswählen.");
            if (string.IsNullOrWhiteSpace(body.Reason)) throw ApiException.BadRequest("reason_required", "Bitte einen Grund angeben.");
            if (string.IsNullOrWhiteSpace(body.Custodian)) throw ApiException.BadRequest("custodian_required", "Bei Breakglass-Keys ist der Verwahrer Pflicht.");
            var graph = await graphs.ForTenantAsync(tenant.EntraTenantId);
            var upns = new List<string>();
            foreach (var id in ids) upns.Add((await graph.GetUserAsync(id) ?? throw ApiException.NotFound($"Konto {id}")).UserPrincipalName);
            var a = new RegistrationApproval
            {
                TenantId = tenantId, EntraUserIds = ids, UserPrincipalNames = upns, Custodian = body.Custodian.Trim(), Reason = body.Reason.Trim(),
                RequestedById = user.Id, RequestedByName = user.DisplayName,
            };
            db.Approvals.Add(a);
            audit.Add(AuditService.User(user), AuditActions.ApprovalRequested, tenantId, target: string.Join(", ", upns), details: new { approval = a.Id, a.Custodian, a.Reason });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(a));
        });

        g.MapPost("/approvals/{id:guid}/approve", (Guid id, DecisionRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
            DecideAsync(id, true, body, cu, db, audit));
        g.MapPost("/approvals/{id:guid}/reject", (Guid id, DecisionRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
            DecideAsync(id, false, body, cu, db, audit));

        g.MapPost("/approvals/{id:guid}/cancel", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var a = await db.Approvals.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Freigabe");
            var user = await cu.RequireTenantAsync(a.TenantId, Permission.RegisterKeys);
            if (a.RequestedById != user.Id) throw ApiException.Forbidden("Nur der Antragsteller kann den Antrag zurückziehen.");
            if (a.Status is not (ApprovalStatus.Pending or ApprovalStatus.Approved)) throw ApiException.Conflict("closed", "Der Antrag ist bereits abgeschlossen.");
            a.Status = ApprovalStatus.Cancelled;
            audit.Add(AuditService.User(user), AuditActions.ApprovalCancelled, a.TenantId, target: string.Join(", ", a.UserPrincipalNames), details: new { approval = a.Id });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(a));
        });
    }

    private static async Task<IResult> DecideAsync(Guid id, bool approve, DecisionRequest body, CurrentUser cu, YlmDbContext db, AuditService audit)
    {
        var a = await db.Approvals.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("Freigabe");
        var user = await cu.RequireTenantAsync(a.TenantId, Permission.ApproveRegistrations);
        if (a.RequestedById == user.Id) throw ApiException.Conflict("four_eyes", "Vier-Augen-Prinzip: Der eigene Antrag muss von einem anderen Admin freigegeben werden.");
        if (a.Status != ApprovalStatus.Pending) throw ApiException.Conflict("closed", "Über den Antrag wurde bereits entschieden.");
        if (!approve && string.IsNullOrWhiteSpace(body.Note)) throw ApiException.BadRequest("note_required", "Bitte einen Grund für die Ablehnung angeben.");
        var now = DateTimeOffset.UtcNow;
        a.Status = approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
        a.DecidedById = user.Id;
        a.DecidedByName = user.DisplayName;
        a.DecidedAt = now;
        a.DecisionNote = string.IsNullOrWhiteSpace(body.Note) ? null : body.Note.Trim();
        a.ValidUntil = approve ? now.Add(Validity) : null;
        audit.Add(AuditService.User(user), approve ? AuditActions.ApprovalApproved : AuditActions.ApprovalRejected, a.TenantId,
            target: string.Join(", ", a.UserPrincipalNames), details: new { approval = a.Id, requestedBy = a.RequestedByName, note = a.DecisionNote, a.ValidUntil });
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(a));
    }
}
