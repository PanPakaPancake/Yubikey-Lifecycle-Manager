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
    public sealed record UserRequest(string Upn, string? DisplayName, PlatformRole Role, bool AllTenants, List<Guid>? TenantIds, bool IsActive = true);

    public static object ToDto(PlatformUser u) => new
    {
        u.Id, u.Upn, u.DisplayName, Role = u.Role.ToString(), u.AllTenants, TenantIds = u.TenantAssignments.Select(t => t.TenantId),
        u.IsActive, u.CreatedAt, u.LastLoginAt, Linked = u.EntraObjectId is not null,
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
                user = ToDto(user),
                permissions = Enum.GetValues<Permission>().Where(p => p != Permission.None && RolePermissions.Has(user.Role, p)).Select(p => p.ToString()),
            });
        }).AllowAnonymous();

        var g = app.MapGroup("/api/users").RequireAuthorization();

        g.MapGet("", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageUsers);
            var users = await db.PlatformUsers.Include(u => u.TenantAssignments).OrderBy(u => u.DisplayName).ToListAsync();
            return Results.Ok(users.Select(ToDto));
        });

        g.MapPost("", async (UserRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            if (!body.Upn.Contains('@')) throw ApiException.BadRequest("invalid", "Bitte den Anmeldenamen (UPN) angeben.");
            if (await db.PlatformUsers.AnyAsync(u => u.Upn.ToLower() == body.Upn.ToLower())) throw ApiException.Conflict("exists", "Dieses Konto ist bereits angelegt.");
            var user = new PlatformUser { Upn = body.Upn.Trim(), DisplayName = body.DisplayName?.Trim() is { Length: > 0 } n ? n : body.Upn.Trim() };
            await ApplyAsync(user, body, db);
            db.PlatformUsers.Add(user);
            audit.Add(AuditService.User(actor), AuditActions.UserCreated, target: user.Upn, details: new { role = user.Role.ToString(), user.AllTenants, tenants = body.TenantIds });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(user));
        });

        g.MapPut("/{id:guid}", async (Guid id, UserRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            var user = await db.PlatformUsers.Include(u => u.TenantAssignments).FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound("Benutzer");
            var wasOwner = user.Role == PlatformRole.Owner && user.IsActive;
            if (body.DisplayName is { Length: > 0 } n) user.DisplayName = n.Trim();
            await ApplyAsync(user, body, db);
            if (wasOwner && (user.Role != PlatformRole.Owner || !user.IsActive)
                && !await db.PlatformUsers.AnyAsync(u => u.Id != id && u.Role == PlatformRole.Owner && u.IsActive))
                throw ApiException.Conflict("last_owner", "Es muss mindestens ein aktiver Owner bleiben.");
            audit.Add(AuditService.User(actor), AuditActions.UserUpdated, target: user.Upn, details: new { role = user.Role.ToString(), user.AllTenants, tenants = body.TenantIds, user.IsActive });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(user));
        });
    }

    private static async Task ApplyAsync(PlatformUser user, UserRequest body, YlmDbContext db)
    {
        user.Role = body.Role;
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
