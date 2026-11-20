using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.PublicApi;
using Ylm.Server.Security;

namespace Ylm.Server.Registration;

/// <summary>
/// ToDo-Liste (planning 8, V2): Ein Ticketsystem (oder ein Admin) meldet Personen, die einen Key bekommen sollen.
/// Sobald die Plattform für die Person einen Key registriert, wird das ToDo automatisch erledigt.
/// </summary>
public static class TodoService
{
    public sealed record CreateTodoRequest(string? EntraUserId, string? UserPrincipalName, string? TicketId, string? TicketUrl, string? Note, Guid? ProfileId, DateOnly? DueDate);
    public sealed record CancelTodoRequest(string? Reason);
    public sealed record TodoBatchRequest(List<Guid> TodoIds, Guid DefaultProfileId, string? Name);

    /// <summary>Request body of the public API: the tenant is named by platform id, Entra tenant id or default domain.</summary>
    public sealed record ApiCreateTodo(string? Tenant, string UserPrincipalName, string? TicketId, string? TicketUrl, string? Note, string? Profile, DateOnly? DueDate);

    public sealed record ApiTodo(string Id, string TenantId, string? Tenant, string UserPrincipalName, string? UserDisplayName, string? TicketId, string? TicketUrl, string? Note,
        string? Profile, DateOnly? DueDate, string Status, string Source, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt, string? CompletedBy, string? KeySerialNumber, string? CancelReason);

    public static object ToDto(RegistrationTodo t, string? tenantName = null, string? profileName = null) => new
    {
        t.Id, t.TenantId, TenantName = tenantName, t.EntraUserId, t.UserPrincipalName, t.UserDisplayName, t.Department, t.TicketId, t.TicketUrl, t.Note,
        t.ProfileId, ProfileName = profileName, t.DueDate, Status = t.Status.ToString(), t.Source, t.CreatedAt, t.CreatedByName, t.CompletedAt, t.CompletedByName, t.KeyId, t.CancelReason,
    };

    /// <summary>Marks the open ToDos of a person as done; called by every registration flow before saving.</summary>
    public static async Task CompleteForUserAsync(YlmDbContext db, AuditService audit, Guid tenantId, string entraUserId, Guid keyId, AuditActor actor, CancellationToken ct = default)
    {
        var open = await db.Todos.Where(t => t.TenantId == tenantId && t.EntraUserId == entraUserId && t.Status == TodoStatus.Open).ToListAsync(ct);
        foreach (var t in open)
        {
            t.Status = TodoStatus.Done;
            t.CompletedAt = DateTimeOffset.UtcNow;
            t.CompletedByName = actor.Name;
            t.KeyId = keyId;
            audit.Add(actor, AuditActions.TodoCompleted, tenantId, keyId, t.UserPrincipalName, new { todo = t.Id, t.TicketId, t.TicketUrl, automatic = true });
        }
    }

    private static string? Clean(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > max ? s.Trim()[..max] : s.Trim();

    /// <summary>Creates a ToDo; an open ToDo for the same person is returned instead of a duplicate.</summary>
    private static async Task<(RegistrationTodo Todo, bool Created)> CreateAsync(YlmDbContext db, IGraphClientFactory graphs, AuditService audit, AuditActor actor, Guid tenantId,
        string userRef, string? ticketId, string? ticketUrl, string? note, Guid? profileId, DateOnly? dueDate, string source, Guid? apiKeyId)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active) ?? throw ApiException.NotFound("Tenant");
        if (ticketUrl is not null && !(Uri.TryCreate(ticketUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
            throw ApiException.BadRequest("invalid_ticket_url", "Die Ticket-URL muss mit http:// oder https:// beginnen.");
        if (profileId is { } pid && !await db.Profiles.AnyAsync(p => p.Id == pid && (p.TenantId == tenantId || p.TenantId == null)))
            throw ApiException.BadRequest("invalid_profile", "Das Profil gibt es in diesem Tenant nicht.");
        var graph = await graphs.ForTenantAsync(tenant.EntraTenantId);
        var user = await graph.GetUserAsync(userRef) ?? throw new ApiException(422, "user_not_found", $"Die Person „{userRef}“ gibt es im Tenant „{tenant.DisplayName}“ nicht.");
        var existing = await db.Todos.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.EntraUserId == user.Id && t.Status == TodoStatus.Open);
        if (existing is not null) return (existing, false);
        var todo = new RegistrationTodo
        {
            TenantId = tenantId, EntraUserId = user.Id, UserPrincipalName = user.UserPrincipalName, UserDisplayName = user.DisplayName, Department = user.Department,
            TicketId = Clean(ticketId, 200), TicketUrl = Clean(ticketUrl, 2000), Note = Clean(note, 2000), ProfileId = profileId, DueDate = dueDate,
            Source = source, ApiKeyId = apiKeyId, CreatedByName = actor.Name,
        };
        db.Todos.Add(todo);
        audit.Add(actor, AuditActions.TodoCreated, tenantId, target: user.UserPrincipalName, details: new { todo = todo.Id, todo.TicketId, todo.TicketUrl, source, todo.DueDate });
        await db.SaveChangesAsync();
        return (todo, true);
    }

    private static async Task CancelAsync(YlmDbContext db, AuditService audit, AuditActor actor, RegistrationTodo t, string? reason)
    {
        if (t.Status != TodoStatus.Open) throw ApiException.Conflict("closed", "Das ToDo ist bereits abgeschlossen.");
        t.Status = TodoStatus.Cancelled;
        t.CompletedAt = DateTimeOffset.UtcNow;
        t.CompletedByName = actor.Name;
        t.CancelReason = Clean(reason, 500);
        audit.Add(actor, AuditActions.TodoCancelled, t.TenantId, target: t.UserPrincipalName, details: new { todo = t.Id, t.TicketId, reason = t.CancelReason });
        await db.SaveChangesAsync();
    }

    public static void MapTodos(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/todos", async (Guid? tenantId, string? status, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            var ids = await cu.AccessibleTenantIdsAsync();
            if (tenantId is { } tid) ids = ids.Where(x => x == tid).ToList();
            var q = db.Todos.AsNoTracking().Where(t => ids.Contains(t.TenantId));
            q = Enum.TryParse<TodoStatus>(status, true, out var st) ? q.Where(t => t.Status == st) : status == "all" ? q : q.Where(t => t.Status == TodoStatus.Open);
            var items = await q.OrderBy(t => t.Status).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.CreatedAt).Take(1000).ToListAsync();
            var tenants = await db.Tenants.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.DisplayName);
            var profiles = await db.Profiles.ToDictionaryAsync(p => p.Id, p => p.Name);
            return Results.Ok(items.Select(t => ToDto(t, tenants.GetValueOrDefault(t.TenantId), t.ProfileId is { } p ? profiles.GetValueOrDefault(p) : null)));
        });

        g.MapPost("/tenants/{tenantId:guid}/todos", async (Guid tenantId, CreateTodoRequest body, CurrentUser cu, YlmDbContext db, IGraphClientFactory graphs, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            var userRef = body.EntraUserId ?? body.UserPrincipalName;
            if (string.IsNullOrWhiteSpace(userRef)) throw ApiException.BadRequest("no_user", "Bitte eine Person angeben.");
            var (todo, created) = await CreateAsync(db, graphs, audit, AuditService.User(user), tenantId, userRef.Trim(), body.TicketId, body.TicketUrl, body.Note, body.ProfileId, body.DueDate, "Manuell", null);
            return Results.Ok(new { created, todo = ToDto(todo) });
        });

        g.MapPost("/todos/{id:guid}/cancel", async (Guid id, CancelTodoRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var t = await db.Todos.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("ToDo");
            var user = await cu.RequireTenantAsync(t.TenantId, Permission.RegisterKeys);
            await CancelAsync(db, audit, AuditService.User(user), t, body.Reason);
            return Results.Ok(ToDto(t));
        });

        // Manuell erledigt, z. B. wenn die Person ihren Key anders bekommen hat.
        g.MapPost("/todos/{id:guid}/done", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var t = await db.Todos.FirstOrDefaultAsync(x => x.Id == id) ?? throw ApiException.NotFound("ToDo");
            var user = await cu.RequireTenantAsync(t.TenantId, Permission.RegisterKeys);
            if (t.Status != TodoStatus.Open) throw ApiException.Conflict("closed", "Das ToDo ist bereits abgeschlossen.");
            t.Status = TodoStatus.Done;
            t.CompletedAt = DateTimeOffset.UtcNow;
            t.CompletedByName = user.DisplayName;
            audit.Add(AuditService.User(user), AuditActions.TodoCompleted, t.TenantId, target: t.UserPrincipalName, details: new { todo = t.Id, t.TicketId, t.TicketUrl, automatic = false });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(t));
        });

        // Offene ToDos als Stapelregistrierung abarbeiten.
        g.MapPost("/tenants/{tenantId:guid}/todos/batch", async (Guid tenantId, TodoBatchRequest body, CurrentUser cu, YlmDbContext db, BatchService batches) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.RegisterKeys);
            var ids = body.TodoIds ?? [];
            var todos = await db.Todos.Where(t => t.TenantId == tenantId && ids.Contains(t.Id) && t.Status == TodoStatus.Open).OrderBy(t => t.CreatedAt).ToListAsync();
            if (todos.Count == 0) throw ApiException.BadRequest("empty", "Keine offenen ToDos ausgewählt.");
            var candidates = await batches.CandidatesAsync(tenantId, new BatchCandidatesRequest(null, null, todos.Select(t => t.EntraUserId).ToList()));
            var byId = candidates.Candidates.ToDictionary(c => c.EntraUserId);
            var items = todos.Select(t =>
            {
                var c = byId.GetValueOrDefault(t.EntraUserId);
                return new BatchItemInput(t.EntraUserId, t.UserPrincipalName, t.UserDisplayName, t.Department,
                    t.ProfileId ?? c?.ProfileId, t.ProfileId is null ? c?.ProfileGroupName : null, null);
            }).ToList();
            var batch = await batches.CreateAsync(tenantId, user, new CreateBatchRequest(body.Name ?? $"ToDo-Liste {DateTime.Now:dd.MM.yyyy}", body.DefaultProfileId, "ToDo-Liste", items));
            return Results.Ok(new { batch.Id });
        });
    }

    /// <summary>Public API part: the only write function (planning 8). Requires an API key with ToDo write permission.</summary>
    public static void MapTodoApi(this RouteGroupBuilder g, Func<ClaimsPrincipal, YlmDbContext, string?, Task<List<Guid>>> scope)
    {
        static ApiTodo Map(RegistrationTodo t, IReadOnlyDictionary<Guid, string> tenants, IReadOnlyDictionary<Guid, string> profiles, IReadOnlyDictionary<Guid, int?> serials) => new(
            t.Id.ToString(), t.TenantId.ToString(), tenants.GetValueOrDefault(t.TenantId), t.UserPrincipalName, t.UserDisplayName, t.TicketId, t.TicketUrl, t.Note,
            t.ProfileId is { } p ? profiles.GetValueOrDefault(p) : null, t.DueDate, t.Status.ToString(), t.Source, t.CreatedAt, t.CompletedAt, t.CompletedByName,
            t.KeyId is { } k ? serials.GetValueOrDefault(k)?.ToString() : null, t.CancelReason);

        async Task<List<ApiTodo>> MapAll(YlmDbContext db, List<RegistrationTodo> items)
        {
            var tids = items.Select(t => t.TenantId).Distinct().ToList();
            var kids = items.Where(t => t.KeyId != null).Select(t => t.KeyId!.Value).ToList();
            var tenants = await db.Tenants.Where(t => tids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.DisplayName);
            var profiles = await db.Profiles.ToDictionaryAsync(p => p.Id, p => p.Name);
            var serials = await db.Keys.Where(k => kids.Contains(k.Id)).ToDictionaryAsync(k => k.Id, k => k.SerialNumber);
            return items.Select(t => Map(t, tenants, profiles, serials)).ToList();
        }

        g.MapGet("/todos", async (ClaimsPrincipal p, YlmDbContext db, string? tenant, string? status, string? ticketId, string? upn) =>
        {
            var ids = await scope(p, db, tenant);
            var q = db.Todos.AsNoTracking().Where(t => ids.Contains(t.TenantId));
            if (Enum.TryParse<TodoStatus>(status, true, out var st)) q = q.Where(t => t.Status == st);
            if (!string.IsNullOrWhiteSpace(ticketId)) q = q.Where(t => t.TicketId == ticketId);
            if (!string.IsNullOrWhiteSpace(upn)) q = q.Where(t => t.UserPrincipalName.ToLower() == upn.ToLower());
            var items = await q.OrderByDescending(t => t.CreatedAt).Take(1000).ToListAsync();
            return TypedResults.Ok(await MapAll(db, items));
        }).WithSummary("ToDo-Liste lesen").WithDescription("Filter: tenant, status (Open, Done, Cancelled), ticketId, upn.").RequireScope(ApiScopes.TodosRead);

        g.MapGet("/todos/{id:guid}", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<ApiTodo>, Microsoft.AspNetCore.Http.HttpResults.NotFound>> (Guid id, ClaimsPrincipal p, YlmDbContext db) =>
        {
            var ids = await scope(p, db, null);
            var t = await db.Todos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && ids.Contains(x.TenantId));
            return t is null ? TypedResults.NotFound() : TypedResults.Ok((await MapAll(db, [t]))[0]);
        }).WithSummary("Ein ToDo abrufen").RequireScope(ApiScopes.TodosRead);

        g.MapPost("/todos", async (ApiCreateTodo body, ClaimsPrincipal p, YlmDbContext db, IGraphClientFactory graphs, AuditService audit) =>
        {
            var ids = await scope(p, db, body.Tenant);
            if (ids.Count != 1) throw ApiException.BadRequest("tenant_required", "Bitte den Tenant angeben (ID der Plattform, Entra-Tenant-ID oder Standard-Domain).");
            if (string.IsNullOrWhiteSpace(body.UserPrincipalName)) throw ApiException.BadRequest("no_user", "userPrincipalName fehlt.");
            Guid? profileId = null;
            if (!string.IsNullOrWhiteSpace(body.Profile))
            {
                var tid = ids[0];
                var name = body.Profile.Trim();
                profileId = await db.Profiles.Where(x => (x.TenantId == tid || x.TenantId == null) && (x.Name.ToLower() == name.ToLower() || x.Id.ToString() == name))
                    .OrderBy(x => x.TenantId == null).Select(x => (Guid?)x.Id).FirstOrDefaultAsync()
                    ?? throw ApiException.BadRequest("invalid_profile", $"Das Profil „{name}“ gibt es in diesem Tenant nicht.");
            }
            var keyName = p.Identity?.Name?.Replace("api:", "") ?? "API";
            var actor = ApiScopes.Actor(p);
            var kid = actor.Id;
            var (todo, created) = await CreateAsync(db, graphs, audit, actor, ids[0], body.UserPrincipalName.Trim(), body.TicketId, body.TicketUrl, body.Note, profileId, body.DueDate, "API: " + keyName, kid);
            var dto = (await MapAll(db, [todo]))[0];
            return created ? Results.Created($"/api/v1/todos/{todo.Id}", dto) : Results.Ok(dto);
        }).WithSummary("Person auf die ToDo-Liste setzen")
          .WithDescription("Gibt es für die Person schon ein offenes ToDo, kommt dieses mit 200 zurück, sonst 201.").RequireScope(ApiScopes.TodosWrite);

        g.MapDelete("/todos/{id:guid}", async (Guid id, string? reason, ClaimsPrincipal p, YlmDbContext db, AuditService audit) =>
        {
            var ids = await scope(p, db, null);
            var t = await db.Todos.FirstOrDefaultAsync(x => x.Id == id && ids.Contains(x.TenantId)) ?? throw ApiException.NotFound("ToDo");
            await CancelAsync(db, audit, ApiScopes.Actor(p), t, reason ?? "Im Ticketsystem zurückgezogen");
            return Results.NoContent();
        }).WithSummary("Offenes ToDo zurückziehen").RequireScope(ApiScopes.TodosWrite);
    }
}
