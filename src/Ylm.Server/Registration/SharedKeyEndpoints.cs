using Ylm.Core;
using Ylm.Server.Protection;
using Ylm.Server.Security;

namespace Ylm.Server.Registration;

/// <summary>Geteilter Key (V1.1): mehrere Konten auf einem Key mit Verwahrer.</summary>
public static class SharedKeyEndpoints
{
    public static void MapSharedKeys(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants/{tenantId:guid}/shared-registrations").RequireAuthorization();

        g.MapPost("/preview", async (Guid tenantId, SharedPreviewRequest body, CurrentUser cu, SharedKeyService shared,
            ProtectedAccountService protection, Data.YlmDbContext db, Graph.IGraphClientFactory graphs) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            await protection.EnsureAllowedAsync(db, graphs, tenantId, user, body.EntraUserIds ?? []);
            return Results.Ok(await shared.PreviewAsync(tenantId, user, body));
        });

        g.MapPost("", async (Guid tenantId, SharedRegistrationRequest body, CurrentUser cu, SharedKeyService shared, JobStore jobs, IServiceScopeFactory scopes,
            ProtectedAccountService protection, Data.YlmDbContext db, Graph.IGraphClientFactory graphs) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            await protection.EnsureAllowedAsync(db, graphs, tenantId, user, body.EntraUserIds ?? []);
            var job = shared.Start(tenantId, user, body, jobs, scopes);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        });
    }
}
