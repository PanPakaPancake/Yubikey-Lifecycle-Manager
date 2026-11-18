using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Security;

namespace Ylm.Server.Registration;

/// <summary>Stapelregistrierung: list of people, keys are plugged in one after the other.</summary>
public static class BatchEndpoints
{
    public sealed record StatusRequest(BatchStatus Status);

    public static void MapBatches(this IEndpointRouteBuilder app)
    {
        var t = app.MapGroup("/api/tenants/{tenantId:guid}/batches").RequireAuthorization();

        t.MapGet("", async (Guid tenantId, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            var batches = await db.Batches.AsNoTracking().Include(b => b.Items).Where(b => b.TenantId == tenantId)
                .OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync();
            return Results.Ok(batches.Select(b => new
            {
                b.Id, b.Name, b.Source, Status = b.Status.ToString(), b.CreatedAt, b.CreatedByName, b.CompletedAt, Counts = BatchService.Counts(b.Items),
            }));
        });

        t.MapPost("/candidates", async (Guid tenantId, BatchCandidatesRequest body, CurrentUser cu, BatchService batches) =>
        {
            await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            return Results.Ok(await batches.CandidatesAsync(tenantId, body));
        });

        t.MapPost("", async (Guid tenantId, CreateBatchRequest body, CurrentUser cu, BatchService batches) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            var batch = await batches.CreateAsync(tenantId, user, body);
            return Results.Created($"/api/batches/{batch.Id}", batches.Dto(batch, user.Id));
        });

        var g = app.MapGroup("/api/batches/{id:guid}").RequireAuthorization();

        static async Task<(RegistrationBatch Batch, PlatformUser User)> Load(Guid id, CurrentUser cu, BatchService batches)
        {
            var batch = await batches.LoadAsync(id);
            var user = await cu.RequireTenantAsync(batch.TenantId, Permission.RegisterKeys);
            return (batch, user);
        }

        g.MapGet("", async (Guid id, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            return Results.Ok(batches.Dto(batch, user.Id));
        });

        g.MapPost("/status", async (Guid id, StatusRequest body, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            await batches.SetStatusAsync(batch, body.Status, user);
            return Results.Ok(batches.Dto(await batches.LoadAsync(id), user.Id));
        });

        g.MapPost("/items", async (Guid id, AddBatchItemsRequest body, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            await batches.AddItemsAsync(batch, user, body);
            return Results.Ok(batches.Dto(await batches.LoadAsync(id), user.Id));
        });

        g.MapPatch("/items/{itemId:guid}", async (Guid id, Guid itemId, UpdateBatchItemRequest body, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            await batches.UpdateItemAsync(batch, itemId, body);
            return Results.Ok(batches.Dto(await batches.LoadAsync(id), user.Id));
        });

        g.MapPost("/items/{itemId:guid}/skip", async (Guid id, Guid itemId, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            await batches.SetSkippedAsync(batch, itemId, true, user);
            return Results.Ok(batches.Dto(await batches.LoadAsync(id), user.Id));
        });

        g.MapPost("/items/{itemId:guid}/requeue", async (Guid id, Guid itemId, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            await batches.SetSkippedAsync(batch, itemId, false, user);
            return Results.Ok(batches.Dto(await batches.LoadAsync(id), user.Id));
        });

        g.MapDelete("/items/{itemId:guid}", async (Guid id, Guid itemId, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            await batches.RemoveItemAsync(batch, itemId);
            return Results.Ok(batches.Dto(await batches.LoadAsync(id), user.Id));
        });

        g.MapPost("/next", async (Guid id, BatchNextRequest body, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            return Results.Ok(await batches.NextAsync(batch, user, body));
        });

        g.MapPost("/items/{itemId:guid}/register", async (Guid id, Guid itemId, BatchRegisterRequest body, CurrentUser cu, BatchService batches, IServiceScopeFactory scopes) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            var job = await batches.RegisterAsync(batch, itemId, user, body, scopes);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        });

        g.MapGet("/pin-letters", async (Guid id, string? items, CurrentUser cu, BatchService batches) =>
        {
            var (batch, user) = await Load(id, cu, batches);
            var ids = items?.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();
            var (pdf, fileName, _) = await batches.MergedLettersAsync(batch, user, ids);
            return Results.File(pdf, "application/pdf", fileName);
        });
    }
}
