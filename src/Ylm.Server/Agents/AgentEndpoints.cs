using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Ylm.Contracts;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Agents;

public static class AgentEndpoints
{
    public const string PairingRateLimit = "pairing";
    private const Permission AgentPermissions = Permission.RegisterKeys | Permission.ManageInventory | Permission.ResetKeys;

    public static void MapAgents(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/agents").RequireAuthorization();

        g.MapGet("", async (CurrentUser cu, YlmDbContext db, AgentRegistry registry) =>
        {
            var user = await cu.GetAsync();
            var agents = await db.Agents.Where(a => a.PlatformUserId == user.Id && a.RevokedAt == null).OrderBy(a => a.MachineName).ToListAsync();
            return Results.Ok(agents.Select(a =>
            {
                var live = registry.Get(a.Id);
                return new
                {
                    a.Id, a.MachineName, a.AgentVersion, a.CreatedAt, a.LastSeenAt,
                    Online = live is not null,
                    Key = live?.CurrentKey,
                    Busy = live?.BusyWith,
                };
            }));
        });

        // Short-lived code the admin types into the tray app ("Agent koppeln").
        g.MapPost("/pairing-codes", async (CurrentUser cu, YlmDbContext db) =>
        {
            var user = await cu.GetAsync();
            if ((RolePermissions.For(user.Role) & AgentPermissions) == 0) throw ApiException.Forbidden("Ihre Rolle benötigt keinen Agent.");
            var code = TokenHashing.NewPairingCode();
            var expires = DateTimeOffset.UtcNow.AddMinutes(10);
            db.PairingCodes.Add(new PairingCode { CodeHash = TokenHashing.Hash(TokenHashing.NormalizePairingCode(code)), PlatformUserId = user.Id, ExpiresAt = expires });
            await db.SaveChangesAsync();
            return Results.Ok(new { code, expiresAt = expires });
        });

        g.MapDelete("/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit, AgentRegistry registry) =>
        {
            var user = await cu.GetAsync();
            var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == id && (a.PlatformUserId == user.Id || user.Role == PlatformRole.Owner))
                        ?? throw ApiException.NotFound("Agent");
            agent.RevokedAt = DateTimeOffset.UtcNow;
            audit.Add(AuditService.User(user), AuditActions.AgentRevoked, target: agent.MachineName, details: new { agent.Id });
            await db.SaveChangesAsync();
            registry.Disconnect(id);
            return Results.NoContent();
        });

        // Called by the agent service (anonymous, rate limited) to exchange the code for an agent token.
        app.MapPost("/api/agent/pair", async (PairingRequest body, YlmDbContext db, AuditService audit, Setup.SetupService setup) =>
        {
            var hash = TokenHashing.Hash(TokenHashing.NormalizePairingCode(body.Code ?? ""));
            var code = await db.PairingCodes.FirstOrDefaultAsync(c => c.CodeHash == hash);
            if (code is null || code.UsedAt is not null || code.ExpiresAt < DateTimeOffset.UtcNow)
                return Results.Json(new ApiError("invalid_code", "Der Kopplungscode ist ungültig oder abgelaufen."), statusCode: 400);
            var user = await db.PlatformUsers.FirstAsync(u => u.Id == code.PlatformUserId);
            code.UsedAt = DateTimeOffset.UtcNow;
            var secret = TokenHashing.NewSecret();
            var machine = string.IsNullOrWhiteSpace(body.MachineName) ? "Unbekannt" : body.MachineName.Trim()[..Math.Min(body.MachineName.Trim().Length, 100)];
            var agent = new Agent { PlatformUserId = user.Id, MachineName = machine, AgentVersion = body.AgentVersion, TokenHash = TokenHashing.Hash(secret) };
            db.Agents.Add(agent);
            audit.Add(new AuditActor(ActorType.User, user.Id, $"{user.DisplayName} <{user.Upn}>"), AuditActions.AgentPaired, target: machine, details: new { agent.Id, body.AgentVersion });
            await db.SaveChangesAsync();
            var settings = await setup.GetSettingsAsync();
            return Results.Ok(new PairingResponse(agent.Id, $"ylma_{agent.Id:N}_{secret}", user.DisplayName, settings.OrganizationName));
        }).AllowAnonymous().RequireRateLimiting(PairingRateLimit);
    }
}
