using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Users;

/// <summary>Feste Rollen ansehen und eigene Rollen aus einzelnen Berechtigungen definieren (nur Owner).</summary>
public static class RoleEndpoints
{
    public sealed record RoleRequest(string Name, string? Description, List<string> Permissions);

    private static (PlatformRole Role, string Description)[] BuiltIn => _builtIn.Value;
    private static readonly L.PerLanguage<(PlatformRole Role, string Description)[]> _builtIn = new(() => [
        (PlatformRole.Owner, L.T("Alles, inkl. Tenants, Benutzer, Rollen, Profile und API-Schlüssel")),
        (PlatformRole.Operator, L.T("Keys registrieren, zurücksetzen, sperren, ausmustern, Lager und Partnerlager pflegen, synchronisieren")),
        (PlatformRole.Helpdesk, L.T("Inventar ansehen und Keys sperren")),
        (PlatformRole.Auditor, L.T("Nur lesen: Inventar, Berichte und Audit-Log")),
    ]);

    private static object ToDto(CustomRole r, int users) => new
    {
        r.Id, r.Name, r.Description,
        Permissions = RolePermissions.Split(RolePermissions.ForCustom(r.Permissions)).Select(p => p.ToString()),
        Users = users, r.CreatedAt, r.UpdatedAt,
    };

    public static void MapRoles(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/roles").RequireAuthorization();

        g.MapGet("", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageUsers);
            var roles = await db.CustomRoles.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
            var counts = await db.PlatformUsers.Where(u => u.CustomRoleId != null).GroupBy(u => u.CustomRoleId)
                .Select(x => new { Id = x.Key, Count = x.Count() }).ToDictionaryAsync(x => x.Id!.Value, x => x.Count);
            return Results.Ok(new
            {
                builtIn = BuiltIn.Select(b => new
                {
                    Role = b.Role.ToString(), b.Description,
                    Permissions = RolePermissions.Split(RolePermissions.For(b.Role)).Select(p => p.ToString()),
                }),
                custom = roles.Select(r => ToDto(r, counts.GetValueOrDefault(r.Id))),
                catalog = RolePermissions.Catalog.Select(p => new { Permission = p.Permission.ToString(), p.Group, p.Label, p.Description, p.OwnerOnly }),
            });
        });

        g.MapPost("", async (RoleRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            var role = new CustomRole();
            await ApplyAsync(role, body, db);
            db.CustomRoles.Add(role);
            audit.Add(AuditService.User(actor), AuditActions.RoleCreated, target: role.Name, details: new { permissions = body.Permissions });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(role, 0));
        });

        g.MapPut("/{id:guid}", async (Guid id, RoleRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            var role = await db.CustomRoles.FirstOrDefaultAsync(r => r.Id == id) ?? throw ApiException.NotFound(L.T("Rolle"));
            var before = new { role.Name, permissions = RolePermissions.Split((Permission)role.Permissions).Select(p => p.ToString()) };
            await ApplyAsync(role, body, db);
            role.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add(AuditService.User(actor), AuditActions.RoleUpdated, target: role.Name, details: new { before, after = new { role.Name, permissions = body.Permissions } });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(role, await db.PlatformUsers.CountAsync(u => u.CustomRoleId == id)));
        });

        g.MapDelete("/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            var role = await db.CustomRoles.FirstOrDefaultAsync(r => r.Id == id) ?? throw ApiException.NotFound(L.T("Rolle"));
            var users = await db.PlatformUsers.CountAsync(u => u.CustomRoleId == id);
            if (users > 0) throw ApiException.Conflict("role_in_use", L.F($"Die Rolle ist noch {users} Benutzer(n) zugewiesen. Bitte zuerst deren Rolle ändern."));
            db.CustomRoles.Remove(role);
            audit.Add(AuditService.User(actor), AuditActions.RoleDeleted, target: role.Name);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task ApplyAsync(CustomRole role, RoleRequest body, YlmDbContext db)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 60) throw ApiException.BadRequest("invalid", L.T("Bitte einen Namen mit 2 bis 60 Zeichen angeben."));
        if (Enum.GetNames<PlatformRole>().Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw ApiException.BadRequest("invalid", L.T("Der Name ist für eine feste Rolle reserviert."));
        if (await db.CustomRoles.AnyAsync(r => r.Id != role.Id && r.Name.ToLower() == name.ToLower()))
            throw ApiException.Conflict("exists", L.T("Eine Rolle mit diesem Namen gibt es schon."));
        var permissions = Permission.None;
        foreach (var p in body.Permissions ?? [])
        {
            if (!Enum.TryParse<Permission>(p, out var parsed) || RolePermissions.Catalog.All(c => c.Permission != parsed))
                throw ApiException.BadRequest("invalid", L.F($"Unbekannte Berechtigung „{p}“."));
            if ((RolePermissions.OwnerOnly & parsed) != 0)
                throw ApiException.BadRequest("owner_only", L.T("Benutzer und Rollen, API-Schlüssel und Plattform-Einstellungen sind dem Owner vorbehalten."));
            permissions |= parsed;
        }
        if (permissions == Permission.None) throw ApiException.BadRequest("invalid", L.T("Bitte mindestens eine Berechtigung wählen."));
        role.Name = name;
        role.Description = string.IsNullOrWhiteSpace(body.Description) ? null : body.Description.Trim();
        role.Permissions = (long)permissions;
    }
}
