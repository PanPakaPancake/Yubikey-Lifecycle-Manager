using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Contracts.Localization;

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
        var tenant = await db.Tenants.FirstAsync(t => t.Id == key.TenantId, ct); // keys with registrations always belong to a tenant
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
        await RemoveEntraRegistrationsAsync(key, L.F($"Gesperrt: {KeyLifecycle.DisplayName(reason)}"), actor, ct);
        SetStatus(key, KeyStatus.Locked, note);
        key.LockReason = reason;
        audit.Add(actor, AuditActions.KeyLocked, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { reason = reason.ToString(), note });
        await db.SaveChangesAsync(ct);
    }

    public async Task RetireAsync(YubiKey key, string? note, AuditActor actor, bool toPartnerStock = false, CancellationToken ct = default)
    {
        SetStatus(key, KeyStatus.Retired, note);
        audit.Add(actor, AuditActions.KeyRetired, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { note, toPartnerStock });
        if (toPartnerStock) MoveToPartnerStock(key, actor, audit);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Trennt den Key vom Kunden-Tenant und legt ihn ins allgemeine Partnerlager.
    /// Nur ohne aktive Registrierungen (also im Lager oder ausgemustert).
    /// </summary>
    public static void MoveToPartnerStock(YubiKey key, AuditActor actor, AuditService audit)
    {
        if (key.TenantId is null) return;
        if (key.Status is not (KeyStatus.InStock or KeyStatus.Retired) || key.Registrations.Any(r => r.RemovedAt is null))
            throw new DomainException("not_in_stock", L.T("Nur Keys im Lager oder ausgemusterte Keys ohne aktive Registrierung können ins Partnerlager."));
        var from = key.TenantId;
        audit.Add(actor, AuditActions.KeyMovedToPartnerStock, from, key.Id, key.SerialNumber?.ToString(), new { fromTenantId = from });
        key.TenantId = null;
        key.Tenant = null;
        key.ProfileId = null;
        key.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task MoveToPartnerStockAsync(YubiKey key, AuditActor actor, CancellationToken ct = default)
    {
        if (key.SerialNumber is { } serial && await db.Keys.AnyAsync(k => k.Id != key.Id && k.TenantId == null && k.SerialNumber == serial, ct))
            throw ApiException.Conflict("serial_exists", L.F($"Im Partnerlager gibt es bereits einen Key mit der Seriennummer {serial}."));
        MoveToPartnerStock(key, actor, audit);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Weist einen Key aus dem Partnerlager einem Kunden-Tenant zu; dort liegt er dann bereit im Lager.</summary>
    public async Task AssignToTenantAsync(YubiKey key, Guid tenantId, AuditActor actor, CancellationToken ct = default)
    {
        if (key.TenantId is not null) throw ApiException.Conflict("not_partner_stock", L.T("Der Key ist bereits einem Tenant zugeordnet."));
        if (key.Status != KeyStatus.InStock) throw ApiException.Conflict("not_in_stock", L.T("Nur Keys im Lager können einem Tenant zugewiesen werden."));
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, ct) ?? throw ApiException.NotFound(L.T("Tenant"));
        if (key.SerialNumber is { } serial && await db.Keys.AnyAsync(k => k.TenantId == tenantId && k.SerialNumber == serial, ct))
            throw ApiException.Conflict("serial_exists", L.F($"Im Tenant „{tenant.DisplayName}“ ist die Seriennummer {serial} schon erfasst (evtl. ausgemustert)."));
        key.TenantId = tenantId;
        key.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(actor, AuditActions.KeyAssignedToTenant, tenantId, key.Id, key.SerialNumber?.ToString(), new { from = "partner_stock" });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Nacherfassen (planning 5.1): ein selbst registrierter Key (aus dem Entra-Sync, ohne Seriennummer) wird eingesteckt,
    /// der Agent liest Seriennummer und Firmware aus und der Eintrag wird ergänzt. Gibt es die Seriennummer schon
    /// (z. B. im Lager), werden die Registrierungen in diesen Eintrag übernommen.
    /// </summary>
    public async Task<YubiKey> IdentifyAsync(YubiKey placeholder, Contracts.KeyInfo info, string? custodian, bool ignoreModelMismatch, AuditActor actor, string agentName, CancellationToken ct = default)
    {
        if (placeholder.SerialNumber is not null) throw ApiException.Conflict("already_identified", L.T("Der Key hat schon eine Seriennummer."));
        if (placeholder.TenantId is null) throw ApiException.Conflict("partner_stock", L.T("Nacherfassen gibt es nur für Keys eines Tenants."));
        if (info.SerialNumber is not { } serial) throw ApiException.Conflict("no_serial", L.T("Die Seriennummer des Keys ist nicht lesbar."));
        if (!ignoreModelMismatch && placeholder.AaGuid is { } expected && info.AaGuid is { } actual && expected != actual)
            throw ApiException.Conflict("model_mismatch",
                L.F($"Der eingesteckte Key ({info.Model ?? "unbekanntes Modell"}) ist ein anderes Modell als der Passkey in Entra ({placeholder.Model ?? expected.ToString()}). Wahrscheinlich ist es der falsche Key."));
        var active = placeholder.Registrations.Where(r => r.RemovedAt == null).ToList();
        var target = await FindBySerialAsync(serial, ct);
        if (target is not null && target.Id == placeholder.Id) target = null;
        if (target is not null)
        {
            if (target.TenantId is { } other && other != placeholder.TenantId)
                throw ApiException.Conflict("other_tenant", L.F($"Key {serial} ist im Inventar des Tenants „{target.Tenant?.DisplayName}“ erfasst."));
            if (target.Status == KeyStatus.Locked)
                throw ApiException.Conflict("locked", L.F($"Key {serial} ist im Inventar als gesperrt erfasst. Bitte zuerst prüfen, ob er wieder genutzt werden darf."));
            var others = target.Registrations.Where(r => r.RemovedAt == null && active.All(a => a.EntraUserId != r.EntraUserId)).ToList();
            if (others.Count > 0 && target.Usage == KeyUsage.Personal)
            {
                if (string.IsNullOrWhiteSpace(custodian))
                    throw ApiException.Conflict("custodian_required",
                        L.F($"Auf Key {serial} sind schon Konten registriert ({string.Join(", ", others.Select(r => r.UserPrincipalName))}). Er wird damit zum geteilten Key; bitte einen Verwahrer angeben."));
                target.Usage = KeyUsage.Shared;
                target.Custodian = custodian.Trim();
            }
            if (target.TenantId is null)
            {
                target.TenantId = placeholder.TenantId;
                audit.Add(actor, AuditActions.KeyAssignedToTenant, target.TenantId, target.Id, serial.ToString(), new { from = "partner_stock", context = "identify" });
            }
            foreach (var r in placeholder.Registrations) r.KeyId = target.Id;
            target.Tags = Tags.Normalize([.. target.Tags, .. placeholder.Tags]).ToList();
            foreach (var (k, v) in placeholder.CustomFields) target.CustomFields.TryAdd(k, v);
            if (!string.IsNullOrWhiteSpace(placeholder.Notes)) target.Notes = string.IsNullOrWhiteSpace(target.Notes) ? placeholder.Notes : target.Notes + "\n" + placeholder.Notes;
            db.Keys.Remove(placeholder);
        }
        else target = placeholder;

        target.SerialNumber = serial;
        target.FirmwareVersion = info.FirmwareVersion;
        target.Model = info.Model ?? target.Model;
        target.FormFactor = info.FormFactor;
        target.AaGuid = info.AaGuid ?? target.AaGuid;
        target.IsFips = info.IsFips;
        target.IdentifiedAt = DateTimeOffset.UtcNow;
        if (active.Count > 0 && target.Status != KeyStatus.Active) SetStatus(target, KeyStatus.Active);
        target.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Add(actor, AuditActions.KeyIdentified, target.TenantId, target.Id, serial.ToString(), new
        {
            info.Model, info.FirmwareVersion, agent = agentName, mergedFrom = target.Id != placeholder.Id ? placeholder.Id : (Guid?)null,
            accounts = active.Select(r => r.UserPrincipalName),
        });
        await db.SaveChangesAsync(ct);
        return target;
    }

    /// <summary>Finds a key by serial across tenants; a serial can only live in one tenant's inventory.</summary>
    public async Task<YubiKey?> FindBySerialAsync(int serial, CancellationToken ct = default) =>
        await db.Keys.Include(k => k.Registrations).Include(k => k.Tenant)
            .Where(k => k.SerialNumber == serial && k.Status != KeyStatus.Retired)
            .OrderByDescending(k => k.UpdatedAt).FirstOrDefaultAsync(ct);

    /// <summary>Fields of a tenant after inheritance: global fields (with the tenant's changes) and the tenant's own fields.</summary>
    public async Task<IReadOnlyList<ResolvedCustomField>> ResolveFieldsAsync(Guid? tenantId, CancellationToken ct = default)
    {
        // Partnerlager (tenantId null): nur die globalen Felder, ohne Tenant-Änderungen.
        var defs = await db.CustomFields.AsNoTracking().Where(f => f.TenantId == null || (tenantId != null && f.TenantId == tenantId)).ToListAsync(ct);
        Dictionary<Guid, CustomFieldOverrideSpec> overrides = tenantId is null
            ? new()
            : await db.CustomFieldOverrides.AsNoTracking().Where(o => o.TenantId == tenantId).ToDictionaryAsync(o => o.FieldId, o => o.ToSpec(), ct);
        return CustomFieldInheritance.Resolve(defs.Where(f => f.TenantId == null).Select(f => f.ToDef()), overrides, defs.Where(f => f.TenantId != null).Select(f => f.ToDef()));
    }

    /// <summary>Active field specs of a tenant (disabled global fields left out).</summary>
    public async Task<IReadOnlyList<CustomFieldSpec>> FieldSpecsAsync(Guid? tenantId, CancellationToken ct = default) =>
        (await ResolveFieldsAsync(tenantId, ct)).Where(f => f.IsActive).Select(f => f.Spec).ToList();

    /// <param name="existing">Current values of the key. Values of fields that are not active (e.g. disabled for the tenant) are kept.</param>
    public async Task<Dictionary<string, string?>> ValidateFieldsAsync(Guid? tenantId, Dictionary<string, string?>? values,
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
