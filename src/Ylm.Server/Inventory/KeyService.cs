using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Inventory;

public sealed class KeyService(YlmDbContext db, IGraphClientFactory graphFactory, AuditService audit)
{
    public static void SetStatus(YubiKey key, KeyStatus to, string? note = null)
    {
        KeyLifecycle.EnsureTransition(key.Status, to);
        key.Status = to;
        key.StatusNote = note;
        key.StatusChangedAt = DateTimeOffset.UtcNow;
        key.UpdatedAt = key.StatusChangedAt;
        if (to != KeyStatus.Locked) key.LockReason = null;
    }

    /// <summary>Deletes the key's passkeys in Entra and marks the registrations as removed.</summary>
    public async Task RemoveEntraRegistrationsAsync(YubiKey key, string reason, AuditActor actor, CancellationToken ct = default)
    {
        var active = key.Registrations.Where(r => r.RemovedAt is null).ToList();
        if (active.Count == 0) return;
        var tenant = await db.Tenants.FirstAsync(t => t.Id == key.TenantId, ct);
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        foreach (var reg in active)
        {
            await graph.DeleteFido2MethodAsync(reg.EntraUserId, reg.EntraMethodId, ct);
            reg.RemovedAt = DateTimeOffset.UtcNow;
            reg.RemovedReason = reason;
            audit.Add(actor, "registration.deleted_in_entra", key.TenantId, key.Id, reg.UserPrincipalName, new { reg.EntraMethodId, reason });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Sperren (lost/defective): removes the passkeys in Entra so the key can no longer sign in.</summary>
    public async Task LockAsync(YubiKey key, LockReason reason, string? note, AuditActor actor, CancellationToken ct = default)
    {
        KeyLifecycle.EnsureTransition(key.Status, KeyStatus.Locked);
        await RemoveEntraRegistrationsAsync(key, "Gesperrt: " + KeyLifecycle.DisplayName(reason), actor, ct);
        SetStatus(key, KeyStatus.Locked, note);
        key.LockReason = reason;
        audit.Add(actor, AuditActions.KeyLocked, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { reason = reason.ToString(), note });
        await db.SaveChangesAsync(ct);
    }

    public async Task RetireAsync(YubiKey key, string? note, AuditActor actor, CancellationToken ct = default)
    {
        SetStatus(key, KeyStatus.Retired, note);
        audit.Add(actor, AuditActions.KeyRetired, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { note });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Finds a key by serial across tenants; a serial can only live in one tenant's inventory.</summary>
    public async Task<YubiKey?> FindBySerialAsync(int serial, CancellationToken ct = default) =>
        await db.Keys.Include(k => k.Registrations).Include(k => k.Tenant)
            .Where(k => k.SerialNumber == serial && k.Status != KeyStatus.Retired)
            .OrderByDescending(k => k.UpdatedAt).FirstOrDefaultAsync(ct);

    /// <summary>Fields of a tenant after inheritance: global fields (with the tenant's changes) and the tenant's own fields.</summary>
    public async Task<IReadOnlyList<ResolvedCustomField>> ResolveFieldsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var defs = await db.CustomFields.AsNoTracking().Where(f => f.TenantId == null || f.TenantId == tenantId).ToListAsync(ct);
        var overrides = await db.CustomFieldOverrides.AsNoTracking().Where(o => o.TenantId == tenantId).ToDictionaryAsync(o => o.FieldId, o => o.ToSpec(), ct);
        return CustomFieldInheritance.Resolve(defs.Where(f => f.TenantId == null).Select(f => f.ToDef()), overrides, defs.Where(f => f.TenantId != null).Select(f => f.ToDef()));
    }

    /// <summary>Active field specs of a tenant (disabled global fields left out).</summary>
    public async Task<IReadOnlyList<CustomFieldSpec>> FieldSpecsAsync(Guid tenantId, CancellationToken ct = default) =>
        (await ResolveFieldsAsync(tenantId, ct)).Where(f => f.IsActive).Select(f => f.Spec).ToList();

    /// <param name="existing">Current values of the key. Values of fields that are not active (e.g. disabled for the tenant) are kept.</param>
    public async Task<Dictionary<string, string?>> ValidateFieldsAsync(Guid tenantId, Dictionary<string, string?>? values,
        IReadOnlyDictionary<string, string?>? existing = null, CancellationToken ct = default)
    {
        var specs = await FieldSpecsAsync(tenantId, ct);
        var v = values ?? [];
        var errors = CustomFieldValidator.Validate(specs, v);
        if (errors.Count > 0) throw ApiException.BadRequest("invalid_fields", string.Join(" ", errors), errors);
        var result = v.Where(kv => !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => specs.First(s => s.Key.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)).Key, kv => kv.Value?.Trim());
        foreach (var (key, value) in existing ?? new Dictionary<string, string?>())
            if (!specs.Any(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) && !result.ContainsKey(key)) result[key] = value;
        return result;
    }
}
