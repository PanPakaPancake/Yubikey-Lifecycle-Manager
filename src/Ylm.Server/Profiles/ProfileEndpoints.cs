using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Registration;
using Ylm.Server.Security;

namespace Ylm.Server.Profiles;

public static class ProfileEndpoints
{
    public sealed record ProfileRequest(
        Guid? TenantId,
        string Name,
        string? Description,
        PinStrategy PinStrategy,
        int PinLength,
        string? FixedPin,
        bool ForcePinChange,
        bool FactoryResetBeforeRegistration,
        string? DisplayNameTemplate,
        List<string>? AllowedModels,
        string? MinimumFirmware,
        bool IsDefault);

    public static object ToDto(RegistrationProfile p) => new
    {
        p.Id, p.TenantId, p.Name, p.Description, PinStrategy = p.PinStrategy.ToString(), p.PinLength,
        HasFixedPin = p.FixedPinProtected is not null, p.ForcePinChange, p.FactoryResetBeforeRegistration,
        p.DisplayNameTemplate, p.AllowedModels, p.MinimumFirmware, p.IsDefault, p.CreatedAt, p.UpdatedAt,
    };

    /// <summary>Default profile (planning 6.1): random PIN + forced PIN change.</summary>
    public static RegistrationProfile CreateStandard() => new()
    {
        Name = "Standard",
        Description = "Zufällige PIN, PIN-Wechsel bei der ersten Verwendung erzwingen.",
        PinStrategy = PinStrategy.Random,
        PinLength = 8,
        ForcePinChange = true,
        DisplayNameTemplate = "YubiKey {Seriennummer}",
        IsDefault = true,
    };

    private static void Apply(RegistrationProfile p, ProfileRequest r, IDataProtector protector)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw ApiException.BadRequest("invalid", "Bitte einen Namen angeben.");
        if (r.PinLength is < PinGenerator.MinLength or > 32) throw ApiException.BadRequest("invalid", $"Die PIN-Länge muss zwischen {PinGenerator.MinLength} und 32 liegen.");
        if (r.MinimumFirmware is { Length: > 0 } fw && !FirmwareVersion.TryParse(fw, out _)) throw ApiException.BadRequest("invalid", "Ungültige Mindest-Firmware (Format 5.7 oder 5.7.0).");
        if (r.PinStrategy == PinStrategy.Fixed)
        {
            if (r.FixedPin is { Length: > 0 } pin)
            {
                if (PinGenerator.ValidateFixedPin(pin, r.PinLength) is { } err) throw ApiException.BadRequest("invalid_pin", err);
                p.FixedPinProtected = protector.Protect(pin);
            }
            else if (p.FixedPinProtected is null) throw ApiException.BadRequest("invalid_pin", "Für die feste PIN bitte eine PIN angeben.");
        }
        else p.FixedPinProtected = null;

        p.TenantId = r.TenantId;
        p.Name = r.Name.Trim();
        p.Description = r.Description;
        p.PinStrategy = r.PinStrategy;
        p.PinLength = r.PinLength;
        p.ForcePinChange = r.ForcePinChange;
        p.FactoryResetBeforeRegistration = r.FactoryResetBeforeRegistration;
        p.DisplayNameTemplate = string.IsNullOrWhiteSpace(r.DisplayNameTemplate) ? "YubiKey {Seriennummer}" : r.DisplayNameTemplate.Trim();
        p.AllowedModels = (r.AllowedModels ?? []).Select(m => m.Trim()).Where(m => m.Length > 0).Distinct().ToList();
        p.MinimumFirmware = string.IsNullOrWhiteSpace(r.MinimumFirmware) ? null : r.MinimumFirmware.Trim();
        p.IsDefault = r.IsDefault;
        p.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static object AuditView(RegistrationProfile p) => new
    {
        p.Name, p.TenantId, PinStrategy = p.PinStrategy.ToString(), p.PinLength, p.ForcePinChange, p.FactoryResetBeforeRegistration,
        p.DisplayNameTemplate, p.AllowedModels, p.MinimumFirmware, p.IsDefault,
    };

    public static void MapProfiles(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/profiles").RequireAuthorization();

        g.MapGet("", async (Guid? tenantId, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            var ids = await cu.AccessibleTenantIdsAsync();
            var q = db.Profiles.AsNoTracking().Where(p => p.TenantId == null || ids.Contains(p.TenantId.Value));
            if (tenantId is { } t) q = q.Where(p => p.TenantId == null || p.TenantId == t);
            var list = await q.OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name).ToListAsync();
            return Results.Ok(list.Select(ToDto));
        });

        g.MapPost("", async (ProfileRequest body, CurrentUser cu, YlmDbContext db, IDataProtectionProvider dp, AuditService audit) =>
        {
            var user = body.TenantId is { } t ? await cu.RequireTenantAsync(t, Permission.ManageProfiles) : await cu.RequireAsync(Permission.ManageProfiles);
            var p = new RegistrationProfile();
            Apply(p, body, dp.CreateProtector(RegistrationService.FixedPinPurpose));
            if (p.IsDefault) await ClearDefaultAsync(db, p.TenantId);
            db.Profiles.Add(p);
            audit.Add(AuditService.User(user), AuditActions.ProfileCreated, p.TenantId, target: p.Name, details: AuditView(p));
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(p));
        });

        g.MapPut("/{id:guid}", async (Guid id, ProfileRequest body, CurrentUser cu, YlmDbContext db, IDataProtectionProvider dp, AuditService audit) =>
        {
            var p = await db.Profiles.FindAsync(id) ?? throw ApiException.NotFound("Profil");
            var user = p.TenantId is { } t ? await cu.RequireTenantAsync(t, Permission.ManageProfiles) : await cu.RequireAsync(Permission.ManageProfiles);
            if (body.TenantId != p.TenantId) throw ApiException.BadRequest("invalid", "Der Geltungsbereich eines Profils kann nicht geändert werden.");
            var before = AuditView(p);
            Apply(p, body, dp.CreateProtector(RegistrationService.FixedPinPurpose));
            if (p.IsDefault) await ClearDefaultAsync(db, p.TenantId, p.Id);
            audit.Add(AuditService.User(user), AuditActions.ProfileUpdated, p.TenantId, target: p.Name, details: new { before, after = AuditView(p) });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(p));
        });

        g.MapDelete("/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var p = await db.Profiles.FindAsync(id) ?? throw ApiException.NotFound("Profil");
            var user = p.TenantId is { } t ? await cu.RequireTenantAsync(t, Permission.ManageProfiles) : await cu.RequireAsync(Permission.ManageProfiles);
            db.Profiles.Remove(p);
            audit.Add(AuditService.User(user), AuditActions.ProfileDeleted, p.TenantId, target: p.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task ClearDefaultAsync(YlmDbContext db, Guid? tenantId, Guid? except = null)
    {
        foreach (var other in await db.Profiles.Where(x => x.TenantId == tenantId && x.IsDefault && x.Id != except).ToListAsync()) other.IsDefault = false;
    }
}

public static class CustomFieldEndpoints
{
    public sealed record FieldRequest(string Key, string Label, CustomFieldType Type, bool Required, List<string>? Options);

    public static void MapCustomFields(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants/{tenantId:guid}/fields").RequireAuthorization();

        g.MapGet("", async (Guid tenantId, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.ReadInventory);
            return Results.Ok(await db.CustomFields.Where(f => f.TenantId == tenantId).OrderBy(f => f.SortOrder)
                .Select(f => new { f.Id, f.Key, f.Label, Type = f.Type.ToString(), f.Required, f.Options, f.SortOrder }).ToListAsync());
        });

        // Replaces the complete field list of the tenant (keeps it simple for the UI editor).
        g.MapPut("", async (Guid tenantId, List<FieldRequest> body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.ManageProfiles);
            if (body.Count > 50) throw ApiException.BadRequest("invalid", "Höchstens 50 Felder.");
            foreach (var f in body)
            {
                if (!CustomFieldValidator.IsValidKey(f.Key)) throw ApiException.BadRequest("invalid", $"Ungültiger Feldschlüssel „{f.Key}“ (erlaubt: Buchstaben, Ziffern, _ und -).");
                if (string.IsNullOrWhiteSpace(f.Label)) throw ApiException.BadRequest("invalid", "Jedes Feld braucht eine Bezeichnung.");
                if (f.Type == CustomFieldType.Select && (f.Options is null || f.Options.Count == 0)) throw ApiException.BadRequest("invalid", $"Auswahlfeld „{f.Label}“ braucht Optionen.");
            }
            if (body.Select(f => f.Key.ToLowerInvariant()).Distinct().Count() != body.Count) throw ApiException.BadRequest("invalid", "Feldschlüssel müssen eindeutig sein.");
            var existing = await db.CustomFields.Where(f => f.TenantId == tenantId).ToListAsync();
            db.CustomFields.RemoveRange(existing.Where(e => !body.Any(b => b.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase))));
            var order = 0;
            foreach (var f in body)
            {
                var e = existing.FirstOrDefault(x => x.Key.Equals(f.Key, StringComparison.OrdinalIgnoreCase));
                if (e is null) { e = new CustomFieldDefinition { TenantId = tenantId, Key = f.Key }; db.CustomFields.Add(e); }
                e.Label = f.Label.Trim();
                e.Type = f.Type;
                e.Required = f.Required;
                e.Options = f.Type == CustomFieldType.Select ? f.Options!.Select(o => o.Trim()).Where(o => o.Length > 0).ToList() : [];
                e.SortOrder = order++;
            }
            audit.Add(AuditService.User(user), AuditActions.FieldsUpdated, tenantId, details: body);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
