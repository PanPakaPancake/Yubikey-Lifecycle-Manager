using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Reporting;
using Ylm.Server.Security;

namespace Ylm.Server.PublicApi;

/// <summary>
/// Read-only REST API. API keys are managed on platform level and are valid for all tenants or only for selected ones.
/// Deliberately no write operations.
/// </summary>
public static class PublicApiEndpoints
{
    public const string GroupName = "v1";

    public sealed record ApiKeyDto(string Id, string TenantId, string? Tenant, string? SerialNumber, string? Model, string? Firmware, string Status, string? LockReason, string Source,
        IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string?> CustomFields, IReadOnlyList<ApiRegistrationDto> Registrations,
        IReadOnlyList<string> Advisories, DateTimeOffset UpdatedAt);

    public sealed record ApiRegistrationDto(string UserPrincipalName, string? UserDisplayName, string? DisplayName, DateTimeOffset RegisteredAt, string Source);

    public sealed record ApiPage<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);

    public sealed record ApiTenant(string Id, Guid EntraTenantId, string DisplayName, string? DefaultDomain, DateTimeOffset? LastSyncAt);

    public sealed record ApiSummary(string Tenant, int Total, IReadOnlyDictionary<string, int> ByStatus, int UsersWithKey, int? TargetUsers, double? CoveragePercent, DateTimeOffset? LastSyncAt);

    /// <summary>Tenants the API key may read (resolved at authentication).</summary>
    private static List<Guid> Allowed(ClaimsPrincipal p) =>
        p.FindAll(YlmClaims.ApiKeyTenant).Select(c => Guid.Parse(c.Value)).ToList();

    /// <summary>
    /// The tenants a request reads: all allowed ones, or the one named in <paramref name="tenant"/>
    /// (platform id, Entra tenant id or default domain).
    /// </summary>
    private static async Task<List<Guid>> ScopeAsync(ClaimsPrincipal p, YlmDbContext db, string? tenant)
    {
        var allowed = Allowed(p);
        if (string.IsNullOrWhiteSpace(tenant)) return allowed;
        var t = tenant.Trim();
        var q = db.Tenants.AsNoTracking().Where(x => allowed.Contains(x.Id));
        var match = Guid.TryParse(t, out var g)
            ? await q.Where(x => x.Id == g || x.EntraTenantId == g).Select(x => x.Id).FirstOrDefaultAsync()
            : await q.Where(x => x.DefaultDomain != null && x.DefaultDomain.ToLower() == t.ToLower()).Select(x => x.Id).FirstOrDefaultAsync();
        if (match == Guid.Empty) throw ApiException.NotFound("Tenant");
        return [match];
    }

    private static ApiKeyDto Map(YubiKey k) => new(
        k.Id.ToString(), k.TenantId.ToString(), k.Tenant?.DisplayName, k.SerialNumber?.ToString(), k.Model, k.FirmwareVersion, k.Status.ToString(), k.LockReason?.ToString(), k.Source.ToString(),
        k.Tags, k.CustomFields,
        k.Registrations.Where(r => r.RemovedAt == null).Select(r => new ApiRegistrationDto(r.UserPrincipalName, r.UserDisplayName, r.DisplayName, r.RegisteredAt, r.Source.ToString())).ToList(),
        FirmwareAdvisories.For(k.FirmwareVersion).Select(a => a.Id).ToList(), k.UpdatedAt);

    public static void MapPublicApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization("ApiKey").WithGroupName(GroupName).WithTags("Lese-API");
        const string tenantHint = " Optional tenant: ID der Plattform, Entra-Tenant-ID oder Standard-Domain; ohne Angabe alle Tenants, für die der Schlüssel gilt.";

        g.MapGet("/tenants", async (ClaimsPrincipal p, YlmDbContext db) =>
        {
            var allowed = Allowed(p);
            var tenants = await db.Tenants.AsNoTracking().Where(t => allowed.Contains(t.Id)).OrderBy(t => t.DisplayName).ToListAsync();
            return TypedResults.Ok(tenants.Select(t => new ApiTenant(t.Id.ToString(), t.EntraTenantId, t.DisplayName, t.DefaultDomain, t.LastSyncAt)).ToList());
        }).WithSummary("Tenants, für die der API-Schlüssel gilt");

        g.MapGet("/keys", async (ClaimsPrincipal p, YlmDbContext db, string? tenant, string? status, string? serial, string? upn, string? tag, int? page, int? pageSize) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            var q = db.Keys.AsNoTracking().Include(k => k.Registrations).Include(k => k.Tenant).Where(k => k.TenantId != null && tenantIds.Contains(k.TenantId.Value));
            if (Enum.TryParse<KeyStatus>(status, true, out var st)) q = q.Where(k => k.Status == st);
            if (int.TryParse(serial, out var s)) q = q.Where(k => k.SerialNumber == s);
            if (!string.IsNullOrWhiteSpace(upn)) q = q.Where(k => k.Registrations.Any(r => r.RemovedAt == null && r.UserPrincipalName.ToLower() == upn.ToLower()));
            if (!string.IsNullOrWhiteSpace(tag)) q = q.Where(k => k.Tags.Contains(tag));
            var size = Math.Clamp(pageSize ?? 100, 1, 1000);
            var pg = Math.Max(page ?? 1, 1);
            var total = await q.CountAsync();
            var items = await q.OrderBy(k => k.Tenant!.DisplayName).ThenBy(k => k.SerialNumber).Skip((pg - 1) * size).Take(size).ToListAsync();
            return TypedResults.Ok(new ApiPage<ApiKeyDto>(total, pg, size, items.Select(Map).ToList()));
        }).WithSummary("Keys auflisten").WithDescription("Filter: tenant, status (InStock, Active, Locked, Retired), serial, upn, tag. Seitenweise." + tenantHint);

        g.MapGet("/keys/{id:guid}", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<ApiKeyDto>, Microsoft.AspNetCore.Http.HttpResults.NotFound>> (Guid id, ClaimsPrincipal p, YlmDbContext db) =>
        {
            var allowed = Allowed(p);
            var key = await db.Keys.AsNoTracking().Include(k => k.Registrations).Include(k => k.Tenant).FirstOrDefaultAsync(k => k.Id == id && k.TenantId != null && allowed.Contains(k.TenantId.Value));
            return key is null ? TypedResults.NotFound() : TypedResults.Ok(Map(key));
        }).WithSummary("Einen Key abrufen");

        g.MapGet("/users/{upn}/keys", async (string upn, string? tenant, ClaimsPrincipal p, YlmDbContext db) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            var keys = await db.Keys.AsNoTracking().Include(k => k.Registrations).Include(k => k.Tenant)
                .Where(k => k.TenantId != null && tenantIds.Contains(k.TenantId.Value) && k.Registrations.Any(r => r.RemovedAt == null && r.UserPrincipalName.ToLower() == upn.ToLower())).ToListAsync();
            return TypedResults.Ok(keys.Select(Map).ToList());
        }).WithSummary("Aktive Keys einer Person").WithDescription(tenantHint.Trim());

        g.MapGet("/summary", async (ClaimsPrincipal p, string? tenant, DashboardService dash, YlmDbContext db) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            var tenants = await db.Tenants.AsNoTracking().Where(x => tenantIds.Contains(x.Id)).ToListAsync();
            var d = await dash.BuildAsync(tenantIds);
            var name = tenants.Count == 1 ? tenants[0].DisplayName : $"Alle ({tenants.Count} Tenants)";
            return TypedResults.Ok(new ApiSummary(name, d.Total, d.ByStatus, d.UsersWithKey, d.TargetUsers, d.CoveragePercent, tenants.Max(t => t.LastSyncAt)));
        }).WithSummary("Kennzahlen").WithDescription("Für einen Tenant oder zusammengefasst über alle Tenants des Schlüssels." + tenantHint);
    }

    public sealed record CreateApiKeyRequest(string Name, int? ExpiresInDays, bool AllTenants, List<Guid>? TenantIds);
    public sealed record UpdateApiKeyRequest(bool AllTenants, List<Guid>? TenantIds);

    /// <summary>Management of API keys in the platform area of the web UI (Owner).</summary>
    public static void MapApiKeyManagement(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/api-keys").RequireAuthorization();

        async Task<List<Guid>> ValidateScopeAsync(PlatformUser user, bool allTenants, List<Guid>? tenantIds, YlmDbContext db)
        {
            if (allTenants)
            {
                if (!user.AllTenants) throw ApiException.Forbidden("Nur Benutzer mit Zugriff auf alle Tenants können Schlüssel für alle Tenants erstellen.");
                return [];
            }
            var ids = (tenantIds ?? []).Distinct().ToList();
            if (ids.Count == 0) throw ApiException.BadRequest("invalid", "Bitte mindestens einen Tenant auswählen oder „Alle Tenants“ wählen.");
            var existing = await db.Tenants.Where(t => ids.Contains(t.Id) && t.Status != TenantStatus.Offboarded).Select(t => t.Id).ToListAsync();
            if (existing.Count != ids.Count) throw ApiException.BadRequest("invalid", "Mindestens ein ausgewählter Tenant existiert nicht oder ist offboarded.");
            if (ids.Any(id => !CurrentUser.CanAccessTenant(user, id))) throw ApiException.Forbidden("Sie sind nicht für alle ausgewählten Tenants zuständig.");
            return ids;
        }

        g.MapGet("", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            var names = await db.Tenants.ToDictionaryAsync(t => t.Id, t => t.DisplayName);
            var keys = await db.ApiKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();
            return Results.Ok(keys.Select(k => new
            {
                k.Id, k.Name, k.Prefix, k.CreatedAt, k.CreatedBy, k.LastUsedAt, k.ExpiresAt, k.RevokedAt, k.AllTenants,
                Tenants = k.TenantIds.Select(id => new { Id = id, DisplayName = names.GetValueOrDefault(id, "(gelöscht)") }),
            }));
        });

        g.MapPost("", async (CreateApiKeyRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.BadRequest("invalid", "Bitte einen Namen angeben.");
            var tenantIds = await ValidateScopeAsync(user, body.AllTenants, body.TenantIds, db);
            var prefix = TokenHashing.NewSecret(6).Replace("_", "x").Replace("-", "y");
            var secret = TokenHashing.NewSecret();
            var key = new ApiKey
            {
                AllTenants = body.AllTenants, TenantIds = tenantIds,
                Name = body.Name.Trim(), Prefix = prefix, SecretHash = TokenHashing.Hash(secret), CreatedBy = user.Upn,
                ExpiresAt = body.ExpiresInDays is { } d and > 0 ? DateTimeOffset.UtcNow.AddDays(d) : null,
            };
            db.ApiKeys.Add(key);
            audit.Add(AuditService.User(user), AuditActions.ApiKeyCreated, tenantIds.Count == 1 ? tenantIds[0] : null, target: key.Name,
                details: new { key.Prefix, key.ExpiresAt, key.AllTenants, key.TenantIds });
            await db.SaveChangesAsync();
            return Results.Ok(new { key.Id, key.Name, key.Prefix, key.ExpiresAt, apiKey = $"ylm_{prefix}_{secret}" });
        });

        g.MapPut("/{id:guid}/scope", async (Guid id, UpdateApiKeyRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("API-Schlüssel");
            if (key.RevokedAt is not null) throw ApiException.Conflict("revoked", "Der Schlüssel ist widerrufen.");
            var before = new { key.AllTenants, key.TenantIds };
            key.TenantIds = await ValidateScopeAsync(user, body.AllTenants, body.TenantIds, db);
            key.AllTenants = body.AllTenants;
            audit.Add(AuditService.User(user), AuditActions.ApiKeyScopeChanged, null, target: key.Name,
                details: new { key.Prefix, before, after = new { key.AllTenants, key.TenantIds } });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("API-Schlüssel");
            key.RevokedAt ??= DateTimeOffset.UtcNow;
            audit.Add(AuditService.User(user), AuditActions.ApiKeyRevoked, null, target: key.Name, details: new { key.Prefix });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
