using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Security;

public static class YlmClaims
{
    public const string UserId = "ylm_uid";
    public const string ApiKeyId = "ylm_api_key";
    /// <summary>One claim per tenant the API key may read.</summary>
    public const string ApiKeyTenant = "ylm_api_tenant";
    public const string AgentId = "ylm_agent";
}

/// <summary>The signed-in platform user, loaded fresh from the database on each request.</summary>
public sealed class CurrentUser(IHttpContextAccessor http, YlmDbContext db)
{
    private PlatformUser? _user;
    private bool _loaded;

    public async Task<PlatformUser?> TryGetAsync()
    {
        if (_loaded) return _user;
        _loaded = true;
        var principal = http.HttpContext?.User;
        var idValue = principal?.FindFirstValue(YlmClaims.UserId);
        if (idValue is null || !Guid.TryParse(idValue, out var id)) return null;
        _user = await db.PlatformUsers.Include(u => u.TenantAssignments).Include(u => u.CustomRole).Include(u => u.Passkeys).FirstOrDefaultAsync(u => u.Id == id && u.IsActive);
        return _user;
    }

    public async Task<PlatformUser> GetAsync() =>
        await TryGetAsync() ?? throw new ApiException(401, "unauthenticated", "Bitte melden Sie sich an.");

    /// <summary>Wirksame Rechte: feste Rolle oder eigene Rolle (CustomRole muss geladen sein).</summary>
    public static Permission Effective(PlatformUser user) =>
        user.Role == PlatformRole.Custom ? RolePermissions.ForCustom(user.CustomRole?.Permissions ?? 0) : RolePermissions.For(user.Role);

    public static bool Has(PlatformUser user, Permission permission) => (Effective(user) & permission) == permission;

    public async Task<bool> HasAsync(Permission permission) =>
        await TryGetAsync() is { } u && Has(u, permission);

    public async Task<PlatformUser> RequireAsync(Permission permission)
    {
        var user = await GetAsync();
        if (!Has(user, permission)) throw ApiException.Forbidden();
        return user;
    }

    /// <summary>
    /// Rechte für einen Key: im Kunden-Tenant wie <see cref="RequireTenantAsync"/>,
    /// im Partnerlager (tenantId null) zusätzlich das Recht „Partnerlager verwalten“.
    /// </summary>
    public async Task<PlatformUser> RequireKeyAccessAsync(Guid? tenantId, Permission permission) =>
        tenantId is { } id ? await RequireTenantAsync(id, permission) : await RequireAsync(permission | Permission.ManagePartnerStock);

    public static bool CanAccessTenant(PlatformUser user, Guid tenantId) =>
        user.AllTenants || user.TenantAssignments.Any(t => t.TenantId == tenantId);

    /// <summary>Requires the permission and access to the tenant (Zuständigkeit pro Tenant).</summary>
    public async Task<PlatformUser> RequireTenantAsync(Guid tenantId, Permission permission)
    {
        var user = await RequireAsync(permission);
        if (!CanAccessTenant(user, tenantId)) throw ApiException.Forbidden("Sie sind für diesen Tenant nicht zuständig.");
        return user;
    }

    public async Task<IQueryable<Tenant>> AccessibleTenantsAsync()
    {
        var user = await GetAsync();
        var q = db.Tenants.Where(t => t.Status != TenantStatus.Offboarded || t.DataDeletionDueAt != null);
        if (user.AllTenants) return q;
        var ids = user.TenantAssignments.Select(a => a.TenantId).ToList();
        return q.Where(t => ids.Contains(t.Id));
    }

    public async Task<List<Guid>> AccessibleTenantIdsAsync() =>
        await (await AccessibleTenantsAsync()).Select(t => t.Id).ToListAsync();
}
