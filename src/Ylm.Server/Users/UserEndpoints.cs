using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;
using Ylm.Server.Setup;

namespace Ylm.Server.Users;

public static class UserEndpoints
{
    public sealed record UserRequest(string Upn, string? DisplayName, PlatformRole Role, bool AllTenants, List<Guid>? TenantIds, bool IsActive = true, Guid? CustomRoleId = null, UserLoginKind LoginKind = UserLoginKind.Entra);

    public static object ToDto(PlatformUser u) => new
    {
        u.Id, u.Upn, u.DisplayName, Role = u.Role.ToString(), u.CustomRoleId, CustomRoleName = u.CustomRole?.Name, u.AllTenants, TenantIds = u.TenantAssignments.Select(t => t.TenantId),
        u.IsActive, u.CreatedAt, u.LastLoginAt, Linked = u.EntraObjectId is not null || u.Passkeys.Count > 0 || u.PasswordHash != null,
        LoginKind = u.LoginKind.ToString(), PasskeyCount = u.Passkeys.Count, EmergencyReady = u.PasswordHash != null,
        SetupPending = u.SetupTokenHash != null && u.SetupTokenExpiresAt > DateTimeOffset.UtcNow, u.SetupTokenExpiresAt,
        LockedUntil = u.LockedUntil > DateTimeOffset.UtcNow ? u.LockedUntil : null,
    };

    public static void MapUsers(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (CurrentUser cu, SetupService setup) =>
        {
            var settings = await setup.GetSettingsAsync();
            var user = await cu.TryGetAsync();
            if (user is null) return Results.Json(new { authenticated = false, setupComplete = settings.IsSetupComplete }, statusCode: 401);
            return Results.Ok(new
            {
                authenticated = true,
                setupComplete = settings.IsSetupComplete,
                organizationName = settings.OrganizationName,
                version = Updates.UpdateService.CurrentVersion,
                user = ToDto(user),
                permissions = RolePermissions.Split(CurrentUser.Effective(user)).Select(p => p.ToString()),
            });
        }).AllowAnonymous();

        var g = app.MapGroup("/api/users").RequireAuthorization();

        g.MapGet("", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageUsers);
            var users = await db.PlatformUsers.Include(u => u.TenantAssignments).Include(u => u.CustomRole).Include(u => u.Passkeys).OrderBy(u => u.DisplayName).ToListAsync();
            return Results.Ok(users.Select(ToDto));
        });

        g.MapPost("", async (UserRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            if (!Enum.IsDefined(body.LoginKind)) throw ApiException.BadRequest("invalid", "Unbekannte Anmeldeart.");
            if (body.LoginKind == UserLoginKind.Entra && !body.Upn.Contains('@')) throw ApiException.BadRequest("invalid", "Bitte den Anmeldenamen (UPN) angeben.");
            if (string.IsNullOrWhiteSpace(body.Upn) || body.Upn.Trim().Length < 3) throw ApiException.BadRequest("invalid", "Bitte einen Anmeldenamen angeben.");
            if (await db.PlatformUsers.AnyAsync(u => u.Upn.ToLower() == body.Upn.ToLower())) throw ApiException.Conflict("exists", "Dieses Konto ist bereits angelegt.");
            var user = new PlatformUser { Upn = body.Upn.Trim(), LoginKind = body.LoginKind, DisplayName = body.DisplayName?.Trim() is { Length: > 0 } n ? n : body.Upn.Trim() };
            await ApplyAsync(user, body, db);
            db.PlatformUsers.Add(user);
            audit.Add(AuditService.User(actor), AuditActions.UserCreated, target: user.Upn, details: new { role = user.Role.ToString(), customRole = user.CustomRole?.Name, user.AllTenants, tenants = body.TenantIds, loginKind = user.LoginKind.ToString() });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(user));
        });

        g.MapPut("/{id:guid}", async (Guid id, UserRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            var user = await db.PlatformUsers.Include(u => u.TenantAssignments).Include(u => u.CustomRole).Include(u => u.Passkeys).FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("Benutzer");
            var wasOwner = user.Role == PlatformRole.Owner && user.IsActive;
            if (body.DisplayName is { Length: > 0 } n) user.DisplayName = n.Trim();
            await ApplyAsync(user, body, db);
            if (wasOwner && (user.Role != PlatformRole.Owner || !user.IsActive)
                && !await db.PlatformUsers.AnyAsync(u => u.Id != id && u.Role == PlatformRole.Owner && u.IsActive))
                throw ApiException.Conflict("last_owner", "Es muss mindestens ein aktiver Owner bleiben.");
            audit.Add(AuditService.User(actor), AuditActions.UserUpdated, target: user.Upn, details: new { role = user.Role.ToString(), customRole = user.CustomRole?.Name, user.AllTenants, tenants = body.TenantIds, user.IsActive });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(user));
        });
    }

    private static async Task ApplyAsync(PlatformUser user, UserRequest body, YlmDbContext db)
    {
        if (!Enum.IsDefined(body.Role)) throw ApiException.BadRequest("invalid", "Unbekannte Rolle.");
        user.Role = body.Role;
        if (body.Role == PlatformRole.Custom)
        {
            user.CustomRole = await db.CustomRoles.FirstOrDefaultAsync(r => r.Id == body.CustomRoleId)
                              ?? throw ApiException.BadRequest("invalid", "Bitte eine eigene Rolle auswählen.");
            user.CustomRoleId = user.CustomRole.Id;
        }
        else
        {
            user.CustomRoleId = null;
            user.CustomRole = null;
        }
        user.AllTenants = body.AllTenants;
        user.IsActive = body.IsActive;
        var ids = body.AllTenants ? [] : (body.TenantIds ?? []).Distinct().ToList();
        var valid = await db.Tenants.Where(t => ids.Contains(t.Id)).Select(t => t.Id).ToListAsync();
        if (valid.Count != ids.Count) throw ApiException.BadRequest("invalid", "Unbekannter Tenant in der Zuständigkeit.");
        user.TenantAssignments.RemoveAll(a => !ids.Contains(a.TenantId));
        foreach (var t in ids.Where(t => user.TenantAssignments.All(a => a.TenantId != t)))
            user.TenantAssignments.Add(new PlatformUserTenant { PlatformUserId = user.Id, TenantId = t });
    }
}
