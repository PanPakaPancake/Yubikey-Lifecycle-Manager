using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Inventory;
using Ylm.Server.Security;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Profiles;

/// <summary>
/// Custom fields for keys. Global fields (platform level) are inherited by every tenant; a tenant can change label,
/// required flag and options of an inherited field or disable it, and can add its own fields.
/// </summary>
public static class CustomFieldEndpoints
{
    public sealed record FieldRequest(string Key, string Label, CustomFieldType Type, bool Required, List<string>? Options);
    public sealed record OverrideRequest(Guid FieldId, bool Disabled, string? Label, bool? Required, List<string>? Options);
    public sealed record TenantFieldsRequest(List<FieldRequest> Local, List<OverrideRequest> Overrides);

    public static object ToDto(ResolvedCustomField f) => new
    {
        f.Id, f.Spec.Key, f.Spec.Label, Type = f.Spec.Type.ToString(), f.Spec.Required, f.Spec.Options, f.SortOrder,
        Origin = f.Origin.ToString(), State = f.State.ToString(), f.IsActive,
        Inherited = f.Inherited is { } i ? new { i.Label, i.Required, i.Options } : null,
    };

    private static void Validate(IReadOnlyList<FieldRequest> body)
    {
        if (body.Count > 50) throw ApiException.BadRequest("invalid", L.T("Höchstens 50 Felder."));
        foreach (var f in body)
        {
            if (!CustomFieldValidator.IsValidKey(f.Key)) throw ApiException.BadRequest("invalid", L.F($"Ungültiger Feldschlüssel „{f.Key}“ (erlaubt: Buchstaben, Ziffern, _ und -, höchstens 40 Zeichen)."));
            if (string.IsNullOrWhiteSpace(f.Label)) throw ApiException.BadRequest("invalid", L.T("Jedes Feld braucht eine Bezeichnung."));
            if (f.Type == CustomFieldType.Select && (f.Options is null || f.Options.Count(o => !string.IsNullOrWhiteSpace(o)) == 0))
                throw ApiException.BadRequest("invalid", L.F($"Auswahlfeld „{f.Label}“ braucht Optionen."));
        }
        if (body.Select(f => f.Key.ToLowerInvariant()).Distinct().Count() != body.Count) throw ApiException.BadRequest("invalid", L.T("Feldschlüssel müssen eindeutig sein."));
    }

    private static List<string> CleanOptions(IEnumerable<string>? options) =>
        (options ?? []).Select(o => o.Trim()).Where(o => o.Length > 0).Distinct().ToList();

    /// <summary>Replaces the field list of one level (global = tenantId null). Keys of existing fields stay stable.</summary>
    private static void ApplyList(YlmDbContext db, Guid? tenantId, List<CustomFieldDefinition> existing, IReadOnlyList<FieldRequest> body)
    {
        db.CustomFields.RemoveRange(existing.Where(e => !body.Any(b => b.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase))));
        var order = 0;
        foreach (var f in body)
        {
            var e = existing.FirstOrDefault(x => x.Key.Equals(f.Key, StringComparison.OrdinalIgnoreCase));
            if (e is null) { e = new CustomFieldDefinition { TenantId = tenantId, Key = f.Key }; db.CustomFields.Add(e); }
            e.Label = f.Label.Trim();
            e.Type = f.Type;
            e.Required = f.Required;
            e.Options = f.Type == CustomFieldType.Select ? CleanOptions(f.Options) : [];
            e.SortOrder = order++;
        }
    }

    public static void MapCustomFields(this IEndpointRouteBuilder app)
    {
        // ---- Platform level ----
        var global = app.MapGroup("/api/fields").RequireAuthorization();

        global.MapGet("", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            var fields = await db.CustomFields.AsNoTracking().Where(f => f.TenantId == null).OrderBy(f => f.SortOrder).ToListAsync();
            var overrides = await db.CustomFieldOverrides.AsNoTracking().GroupBy(o => o.FieldId)
                .Select(x => new { FieldId = x.Key, Modified = x.Count(o => !o.Disabled), Disabled = x.Count(o => o.Disabled) }).ToListAsync();
            return Results.Ok(fields.Select(f =>
            {
                var o = overrides.FirstOrDefault(x => x.FieldId == f.Id);
                return new
                {
                    f.Id, f.Key, f.Label, Type = f.Type.ToString(), f.Required, f.Options, f.SortOrder,
                    TenantsModified = o?.Modified ?? 0, TenantsDisabled = o?.Disabled ?? 0,
                };
            }));
        });

        global.MapPut("", async (List<FieldRequest> body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageProfiles);
            if (!user.AllTenants) throw ApiException.Forbidden(L.T("Globale Felder können nur Benutzer mit Zugriff auf alle Tenants bearbeiten."));
            Validate(body);
            var keys = body.Select(f => f.Key.ToLower()).ToList();
            var conflicts = await db.CustomFields.Where(f => f.TenantId != null && keys.Contains(f.Key.ToLower()))
                .Join(db.Tenants, f => f.TenantId, t => t.Id, (f, t) => new { f.Key, t.DisplayName }).ToListAsync();
            if (conflicts.Count > 0)
                throw ApiException.Conflict("field_conflict",
                    L.F($"Diese Schlüssel gibt es schon als lokale Felder in Tenants: {string.Join("; ", conflicts.GroupBy(c => c.Key).Select(g => L.F($"„{g.Key}“ in {string.Join(", ", g.Select(x => x.DisplayName))}")))}. Bitte dort zuerst entfernen oder einen anderen Schlüssel wählen."));
            var existing = await db.CustomFields.Where(f => f.TenantId == null).ToListAsync();
            ApplyList(db, null, existing, body);
            audit.Add(AuditService.User(user), AuditActions.FieldsUpdated, null, target: "global", details: body);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---- Tenant level ----
        var g = app.MapGroup("/api/tenants/{tenantId:guid}/fields").RequireAuthorization();

        // Active fields of the tenant (global incl. tenant changes + local), used by key forms.
        g.MapGet("", async (Guid tenantId, CurrentUser cu, KeyService keys) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.ReadInventory);
            return Results.Ok((await keys.ResolveFieldsAsync(tenantId)).Where(f => f.IsActive).Select(ToDto));
        });

        // Complete inheritance view for the editor, including disabled global fields.
        g.MapGet("/config", async (Guid tenantId, CurrentUser cu, KeyService keys) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.ReadInventory);
            return Results.Ok((await keys.ResolveFieldsAsync(tenantId)).Select(ToDto));
        });

        g.MapPut("", async (Guid tenantId, TenantFieldsRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.ManageProfiles);
            Validate(body.Local);
            var globals = await db.CustomFields.Where(f => f.TenantId == null).ToListAsync();
            var clash = body.Local.FirstOrDefault(l => globals.Any(gl => gl.Key.Equals(l.Key, StringComparison.OrdinalIgnoreCase)));
            if (clash is not null)
                throw ApiException.BadRequest("invalid", L.F($"Der Schlüssel „{clash.Key}“ ist bereits ein globales Feld. Ändern Sie stattdessen das geerbte Feld."));

            var existing = await db.CustomFields.Where(f => f.TenantId == tenantId).ToListAsync();
            ApplyList(db, tenantId, existing, body.Local);

            var oldOverrides = await db.CustomFieldOverrides.Where(o => o.TenantId == tenantId).ToListAsync();
            db.CustomFieldOverrides.RemoveRange(oldOverrides);
            foreach (var o in body.Overrides.DistinctBy(o => o.FieldId))
            {
                var field = globals.FirstOrDefault(f => f.Id == o.FieldId) ?? throw ApiException.BadRequest("invalid", L.T("Unbekanntes globales Feld."));
                var spec = new CustomFieldOverrideSpec(o.Disabled, string.IsNullOrWhiteSpace(o.Label) ? null : o.Label.Trim(), o.Required,
                    field.Type == CustomFieldType.Select && o.Options is not null ? CleanOptions(o.Options) : null);
                if (CustomFieldInheritance.IsNoOp(field.ToSpec(), spec)) continue;
                // Store only what differs, so later changes on platform level still reach the tenant.
                db.CustomFieldOverrides.Add(new CustomFieldOverride
                {
                    TenantId = tenantId, FieldId = field.Id, Disabled = spec.Disabled,
                    Label = spec.Label == field.Label ? null : spec.Label,
                    Required = spec.Required == field.Required ? null : spec.Required,
                    Options = spec.Options is { Count: > 0 } opts && !opts.SequenceEqual(field.Options) ? [.. opts] : null,
                });
            }
            audit.Add(AuditService.User(user), AuditActions.FieldsUpdated, tenantId, details: body);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

/// <summary>Group level: registration profile per Entra group, suggested in the wizard.</summary>
public static class GroupProfileEndpoints
{
    public sealed record AssignmentRequest(string GroupId, string GroupName, Guid ProfileId);

    public static void MapGroupProfiles(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants/{tenantId:guid}").RequireAuthorization();

        g.MapGet("/group-profiles", async (Guid tenantId, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.ReadInventory);
            return Results.Ok(await db.GroupProfiles.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Priority)
                .Join(db.Profiles, a => a.ProfileId, p => p.Id, (a, p) => new { a.Id, a.GroupId, a.GroupName, a.ProfileId, ProfileName = p.Name, a.Priority })
                .ToListAsync());
        });

        // Replaces the list; the order is the priority (first match wins).
        g.MapPut("/group-profiles", async (Guid tenantId, List<AssignmentRequest> body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.ManageProfiles);
            if (body.Count > 50) throw ApiException.BadRequest("invalid", L.T("Höchstens 50 Zuordnungen."));
            if (body.Any(a => string.IsNullOrWhiteSpace(a.GroupId))) throw ApiException.BadRequest("invalid", L.T("Jede Zuordnung braucht eine Gruppe."));
            if (body.Select(a => a.GroupId).Distinct().Count() != body.Count) throw ApiException.BadRequest("invalid", L.T("Jede Gruppe darf nur einmal zugeordnet sein."));
            var profileIds = body.Select(a => a.ProfileId).Distinct().ToList();
            var valid = await db.Profiles.CountAsync(p => profileIds.Contains(p.Id) && (p.TenantId == null || p.TenantId == tenantId));
            if (valid != profileIds.Count) throw ApiException.BadRequest("invalid", L.T("Mindestens ein Profil gilt nicht für diesen Tenant."));

            db.GroupProfiles.RemoveRange(await db.GroupProfiles.Where(a => a.TenantId == tenantId).ToListAsync());
            var prio = 0;
            foreach (var a in body)
                db.GroupProfiles.Add(new GroupProfileAssignment { TenantId = tenantId, GroupId = a.GroupId.Trim(), GroupName = a.GroupName.Trim(), ProfileId = a.ProfileId, Priority = prio++ });
            audit.Add(AuditService.User(user), AuditActions.GroupProfilesUpdated, tenantId, details: body);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/users/{userId}/suggested-profile", async (Guid tenantId, string userId, CurrentUser cu, YlmDbContext db, Graph.IGraphClientFactory gf) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            var assignments = await db.GroupProfiles.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Priority).ToListAsync();
            if (assignments.Count == 0) return Results.Ok(new { profileId = (Guid?)null, groupName = (string?)null });
            var t = await db.Tenants.FindAsync(tenantId) ?? throw ApiException.NotFound(L.T("Tenant"));
            var member = await (await gf.ForTenantAsync(t.EntraTenantId)).CheckMemberGroupsAsync(userId, assignments.Select(a => a.GroupId).ToList());
            var match = assignments.FirstOrDefault(a => member.Contains(a.GroupId));
            return Results.Ok(new { profileId = match?.ProfileId, groupName = match?.GroupName });
        });
    }
}
