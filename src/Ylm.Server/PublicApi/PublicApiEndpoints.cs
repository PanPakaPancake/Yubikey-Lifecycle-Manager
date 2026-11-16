using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Reporting;
using Ylm.Server.Security;

namespace Ylm.Server.PublicApi;

/// <summary>Read-only REST API with one API key per tenant (planning 8). Deliberately no write operations.</summary>
public static class PublicApiEndpoints
{
    public const string GroupName = "v1";

    public sealed record ApiKeyDto(string Id, string? SerialNumber, string? Model, string? Firmware, string Status, string? LockReason, string Source,
        IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string?> CustomFields, IReadOnlyList<ApiRegistrationDto> Registrations,
        IReadOnlyList<string> Advisories, DateTimeOffset UpdatedAt);

    public sealed record ApiRegistrationDto(string UserPrincipalName, string? UserDisplayName, string? DisplayName, DateTimeOffset RegisteredAt, string Source);

    public sealed record ApiPage<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);

    public sealed record ApiSummary(string Tenant, int Total, IReadOnlyDictionary<string, int> ByStatus, int UsersWithKey, int? TargetUsers, double? CoveragePercent, DateTimeOffset? LastSyncAt);

    private static Guid TenantOf(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(YlmClaims.ApiKeyTenant)!);

    private static ApiKeyDto Map(YubiKey k) => new(
        k.Id.ToString(), k.SerialNumber?.ToString(), k.Model, k.FirmwareVersion, k.Status.ToString(), k.LockReason?.ToString(), k.Source.ToString(),
        k.Tags, k.CustomFields,
        k.Registrations.Where(r => r.RemovedAt == null).Select(r => new ApiRegistrationDto(r.UserPrincipalName, r.UserDisplayName, r.DisplayName, r.RegisteredAt, r.Source.ToString())).ToList(),
        FirmwareAdvisories.For(k.FirmwareVersion).Select(a => a.Id).ToList(), k.UpdatedAt);

    public static void MapPublicApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization("ApiKey").WithGroupName(GroupName).WithTags("Lese-API");

        g.MapGet("/keys", async (ClaimsPrincipal p, YlmDbContext db, string? status, string? serial, string? upn, string? tag, int? page, int? pageSize) =>
        {
            var tenantId = TenantOf(p);
            var q = db.Keys.AsNoTracking().Include(k => k.Registrations).Where(k => k.TenantId == tenantId);
            if (Enum.TryParse<KeyStatus>(status, true, out var st)) q = q.Where(k => k.Status == st);
            if (int.TryParse(serial, out var s)) q = q.Where(k => k.SerialNumber == s);
            if (!string.IsNullOrWhiteSpace(upn)) q = q.Where(k => k.Registrations.Any(r => r.RemovedAt == null && r.UserPrincipalName.ToLower() == upn.ToLower()));
            if (!string.IsNullOrWhiteSpace(tag)) q = q.Where(k => k.Tags.Contains(tag));
            var size = Math.Clamp(pageSize ?? 100, 1, 1000);
            var pg = Math.Max(page ?? 1, 1);
            var total = await q.CountAsync();
            var items = await q.OrderBy(k => k.SerialNumber).Skip((pg - 1) * size).Take(size).ToListAsync();
            return TypedResults.Ok(new ApiPage<ApiKeyDto>(total, pg, size, items.Select(Map).ToList()));
        }).WithSummary("Keys des Tenants auflisten").WithDescription("Filter: status (InStock, Active, Locked, Retired), serial, upn, tag. Seitenweise.");

        g.MapGet("/keys/{id:guid}", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<ApiKeyDto>, Microsoft.AspNetCore.Http.HttpResults.NotFound>> (Guid id, ClaimsPrincipal p, YlmDbContext db) =>
        {
            var key = await db.Keys.AsNoTracking().Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id && k.TenantId == TenantOf(p));
            return key is null ? TypedResults.NotFound() : TypedResults.Ok(Map(key));
        }).WithSummary("Einen Key abrufen");

        g.MapGet("/users/{upn}/keys", async (string upn, ClaimsPrincipal p, YlmDbContext db) =>
        {
            var tenantId = TenantOf(p);
            var keys = await db.Keys.AsNoTracking().Include(k => k.Registrations)
                .Where(k => k.TenantId == tenantId && k.Registrations.Any(r => r.RemovedAt == null && r.UserPrincipalName.ToLower() == upn.ToLower())).ToListAsync();
            return TypedResults.Ok(keys.Select(Map).ToList());
        }).WithSummary("Aktive Keys einer Person");

        g.MapGet("/summary", async (ClaimsPrincipal p, DashboardService dash, YlmDbContext db) =>
        {
            var tenantId = TenantOf(p);
            var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == tenantId);
            var d = await dash.BuildAsync([tenantId]);
            return TypedResults.Ok(new ApiSummary(t.DisplayName, d.Total, d.ByStatus, d.UsersWithKey, d.TargetUsers, d.CoveragePercent, t.LastSyncAt));
        }).WithSummary("Kennzahlen des Tenants");
    }

    /// <summary>Management of API keys in the web UI (Owner).</summary>
    public static void MapApiKeyManagement(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants/{tenantId:guid}/api-keys").RequireAuthorization();

        g.MapGet("", async (Guid tenantId, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.ManageApiKeys);
            return Results.Ok(await db.ApiKeys.Where(k => k.TenantId == tenantId).OrderByDescending(k => k.CreatedAt)
                .Select(k => new { k.Id, k.Name, k.Prefix, k.CreatedAt, k.CreatedBy, k.LastUsedAt, k.ExpiresAt, k.RevokedAt }).ToListAsync());
        });

        g.MapPost("", async (Guid tenantId, CreateApiKeyRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.ManageApiKeys);
            if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.BadRequest("invalid", "Bitte einen Namen angeben.");
            var prefix = TokenHashing.NewSecret(6).Replace("_", "x").Replace("-", "y");
            var secret = TokenHashing.NewSecret();
            var key = new ApiKey
            {
                TenantId = tenantId, Name = body.Name.Trim(), Prefix = prefix, SecretHash = TokenHashing.Hash(secret), CreatedBy = user.Upn,
                ExpiresAt = body.ExpiresInDays is { } d and > 0 ? DateTimeOffset.UtcNow.AddDays(d) : null,
            };
            db.ApiKeys.Add(key);
            audit.Add(AuditService.User(user), AuditActions.ApiKeyCreated, tenantId, target: key.Name, details: new { key.Prefix, key.ExpiresAt });
            await db.SaveChangesAsync();
            return Results.Ok(new { key.Id, key.Name, key.Prefix, key.ExpiresAt, apiKey = $"ylm_{prefix}_{secret}" });
        });

        g.MapDelete("/{id:guid}", async (Guid tenantId, Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.ManageApiKeys);
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id && k.TenantId == tenantId) ?? throw ApiException.NotFound("API-Schlüssel");
            key.RevokedAt ??= DateTimeOffset.UtcNow;
            audit.Add(AuditService.User(user), AuditActions.ApiKeyRevoked, tenantId, target: key.Name, details: new { key.Prefix });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    public sealed record CreateApiKeyRequest(string Name, int? ExpiresInDays);
}
