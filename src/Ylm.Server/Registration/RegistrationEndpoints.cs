using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Infrastructure;
using Ylm.Server.Protection;
using Ylm.Server.Security;

namespace Ylm.Server.Registration;

public static class RegistrationEndpoints
{
    public static void MapRegistration(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapPost("/tenants/{tenantId:guid}/registrations/preview", async (Guid tenantId, RegistrationPreviewRequest body, CurrentUser cu, RegistrationService reg,
            ProtectedAccountService protection, Data.YlmDbContext db, Graph.IGraphClientFactory graphs) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            await protection.EnsureAllowedAsync(db, graphs, tenantId, user, [body.EntraUserId]);
            return Results.Ok(await reg.PreviewAsync(tenantId, user, body));
        });

        g.MapPost("/tenants/{tenantId:guid}/registrations", async (Guid tenantId, RegistrationRequest body, CurrentUser cu, RegistrationService reg, JobStore jobs, IServiceScopeFactory scopes,
            ProtectedAccountService protection, Data.YlmDbContext db, Graph.IGraphClientFactory graphs) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            await protection.EnsureAllowedAsync(db, graphs, tenantId, user, [body.EntraUserId]);
            var job = reg.Start(tenantId, user, body, jobs, scopes);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        });

        g.MapGet("/jobs/{id:guid}", async (Guid id, CurrentUser cu, JobStore jobs) =>
        {
            var user = await cu.GetAsync();
            var job = jobs.Get(id, user.Id) ?? throw ApiException.NotFound("Vorgang");
            return Results.Ok(job.ToDto());
        });

        g.MapGet("/pin-letters/{id:guid}", async (Guid id, CurrentUser cu, PinLetterStore letters, AuditService audit, JobStore jobs) =>
        {
            var user = await cu.GetAsync();
            var letter = letters.Get(id, user.Id) ?? throw new ApiException(410, "expired", "Der PIN-Brief ist nicht mehr verfügbar. PINs werden nicht gespeichert.");
            await audit.WriteAsync(AuditService.User(user), AuditActions.PinLetterDownloaded, target: letter.FileName);
            return Results.File(letter.Pdf, "application/pdf", letter.FileName);
        });

        g.MapDelete("/pin-letters/{id:guid}", async (Guid id, CurrentUser cu, PinLetterStore letters) =>
        {
            var user = await cu.GetAsync();
            if (letters.Get(id, user.Id) is not null) letters.Remove(id);
            return Results.NoContent();
        });
    }
}
