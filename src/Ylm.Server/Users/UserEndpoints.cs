using Microsoft.Extensions.Caching.Memory;
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
        app.MapGet("/api/me", async (CurrentUser cu, SetupService setup, YlmDbContext db, Microsoft.Extensions.Options.IOptions<Infrastructure.YlmOptions> options) =>
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
                // Nur für Owner: verbleibende Notfallcodes (0 = noch keine erzeugt oder alle verbraucht). Demo: kein Hinweis.
                recoveryCodes = Security.RecoveryCodeService.Eligible(user) && !options.Value.DemoMode
                    ? await db.RecoveryCodes.CountAsync(c => c.PlatformUserId == user.Id && c.UsedAt == null)
                    : (int?)null,
                language = user.Language,
            });
        }).AllowAnonymous();

        // Sprache der Oberfläche im eigenen Konto speichern, damit sie auf jedem PC gilt.
        app.MapPut("/api/me/language", async (LanguageRequest req, CurrentUser cu, YlmDbContext db) =>
        {
            var user = await cu.TryGetAsync();
            if (user is null) return Results.Unauthorized();
            var lang = req.Language?.Trim().ToLowerInvariant();
            if (lang is not (null or "" or "de" or "en")) throw ApiException.BadRequest("invalid_language", "Unbekannte Sprache. Erlaubt sind de und en.");
            var tracked = await db.PlatformUsers.FirstAsync(u => u.Id == user.Id);
            tracked.Language = string.IsNullOrEmpty(lang) ? null : lang;
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization();

        // Profilbild des angemeldeten Entra-Benutzers aus Microsoft 365 (Heim-Tenant, App-Berechtigung User.Read.All).
        // Ohne Bild oder Berechtigung: 404, die Oberfläche zeigt dann die Initialen. Zwischengespeichert, damit nicht jede Seite Graph fragt.
        app.MapGet("/api/me/photo", async (CurrentUser cu, SetupService setup, Graph.IGraphClientFactory graphs, IMemoryCache cache, ILogger<PlatformUser> logger, CancellationToken ct) =>
        {
            var user = await cu.GetAsync();
            var settings = await setup.GetSettingsAsync();
            if (user.LoginKind != UserLoginKind.Entra || user.EntraObjectId is null || settings.HomeTenantId is null) return Results.NotFound();
            var key = "me-photo:" + user.EntraObjectId;
            if (!cache.TryGetValue(key, out byte[]? photo))
            {
                try { photo = await (await graphs.ForTenantAsync(settings.HomeTenantId.Value, ct)).GetUserPhotoAsync(user.EntraObjectId.Value.ToString(), ct); }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogInformation("Profilbild nicht abrufbar: {Message}", e.Message);
                    photo = null;
                }
                cache.Set(key, photo, photo is null ? TimeSpan.FromMinutes(30) : TimeSpan.FromHours(6));
            }
            if (photo is null || photo.Length == 0) return Results.NotFound();
            return Results.File(photo, "image/jpeg");
        }).RequireAuthorization();

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

public sealed record LanguageRequest(string? Language);
