using Microsoft.EntityFrameworkCore;
using Ylm.Contracts;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Updates;

public static class UpdateEndpoints
{
    public sealed record ApplyRequest(string? Version);
    public sealed record AgentPolicyRequest(AgentUpdatePolicy Policy);

    public static void MapUpdates(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/platform/updates").RequireAuthorization();

        g.MapGet("", async (bool? refresh, CurrentUser cu, UpdateService updates, YlmDbContext db, Setup.SetupService setup, Agents.AgentRegistry registry, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            var check = await updates.CheckAsync(refresh == true, ct);
            var package = updates.GetAgentPackage();
            var settings = await setup.GetSettingsAsync();
            var agents = await db.Agents.AsNoTracking().Where(a => a.RevokedAt == null)
                .OrderBy(a => a.MachineName)
                .Select(a => new { a.Id, a.MachineName, a.AgentVersion, a.AutoUpdate, a.UpdateError, a.LastSeenAt, owner = a.PlatformUser!.DisplayName })
                .ToListAsync(ct);
            return Results.Ok(new
            {
                currentVersion = UpdateService.CurrentVersion,
                latestVersion = check.Latest?.Version,
                updateAvailable = UpdateService.IsNewer(check.Latest?.Version, UpdateService.CurrentVersion),
                releaseUrl = check.Latest?.Url,
                publishedAt = check.Latest?.PublishedAt,
                notes = check.Latest?.Notes,
                error = check.Error,
                checkedAt = check.CheckedAt,
                updater = new { configured = updates.UpdaterConfigured, status = await updates.UpdaterStatusAsync(ct) },
                agent = new
                {
                    bundledVersion = package?.Version,
                    latestVersion = check.Latest?.Version,
                    agents = agents.Count,
                    outdated = package is null ? 0 : agents.Count(a => UpdateService.IsNewer(package.Version, a.AgentVersion)),
                    autoUpdateOff = agents.Count(a => a.AutoUpdate == false),
                    failed = agents.Count(a => a.UpdateError != null),
                    policy = settings.AgentUpdatePolicy,
                    list = agents.Select(a => new
                    {
                        a.Id, a.MachineName, a.AgentVersion, a.AutoUpdate, a.UpdateError, a.LastSeenAt, a.owner,
                        online = registry.Get(a.Id) is not null,
                        outdated = package is not null && UpdateService.IsNewer(package.Version, a.AgentVersion),
                    }),
                },
            });
        });

        g.MapPut("/agent-policy", async (AgentPolicyRequest body, CurrentUser cu, Setup.SetupService setup, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageSettings);
            if (!Enum.IsDefined(body.Policy)) throw ApiException.BadRequest("invalid_policy", "Unbekannte Vorgabe.");
            var s = await setup.GetSettingsAsync();
            audit.Add(AuditService.User(user), AuditActions.AgentUpdatePolicyChanged, details: new { before = s.AgentUpdatePolicy.ToString(), after = body.Policy.ToString() });
            s.AgentUpdatePolicy = body.Policy;
            await db.SaveChangesAsync();
            return Results.Ok(new { policy = s.AgentUpdatePolicy });
        });

        g.MapGet("/status", async (CurrentUser cu, UpdateService updates, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            return Results.Ok(new { currentVersion = UpdateService.CurrentVersion, status = await updates.UpdaterStatusAsync(ct) });
        });

        g.MapPost("/apply", async (ApplyRequest body, CurrentUser cu, UpdateService updates, AuditService audit, CancellationToken ct) =>
        {
            var user = await cu.RequireAsync(Permission.ManageSettings);
            await updates.StartUpdateAsync(body.Version, ct);
            await audit.WriteAsync(AuditService.User(user), AuditActions.UpdateStarted, details: new { from = UpdateService.CurrentVersion, to = body.Version ?? "latest" });
            return Results.Accepted();
        });

        // ----- Versionsstand für alle Angemeldeten (Übersicht, Versionsverlauf) -----
        // Nur lesend: welche Version läuft, welche ist die neueste, was ist neu. Das Update starten bleibt bei ManageSettings.
        app.MapGet("/api/updates/summary", async (CurrentUser cu, UpdateService updates, CancellationToken ct) =>
        {
            await cu.GetAsync();
            var check = await updates.CheckAsync(false, ct);
            return Results.Ok(new
            {
                currentVersion = UpdateService.CurrentVersion,
                latestVersion = check.Latest?.Version,
                updateAvailable = UpdateService.IsNewer(check.Latest?.Version, UpdateService.CurrentVersion),
                releaseUrl = check.Latest?.Url,
                publishedAt = check.Latest?.PublishedAt,
                notes = check.Latest?.Notes,
                error = check.Error,
                checkedAt = check.CheckedAt,
                canApply = await cu.HasAsync(Permission.ManageSettings),
            });
        }).RequireAuthorization();

        // ----- Agent zum Herunterladen (Admin im Browser) -----
        app.MapGet("/api/agent-package", async (CurrentUser cu, UpdateService updates, CancellationToken ct) =>
        {
            await cu.GetAsync();
            var package = updates.GetAgentPackage();
            var latest = package is null ? (await updates.CheckAsync(false, ct)).Latest : null;
            return Results.Ok(new
            {
                available = package is not null || latest?.AgentDownloadUrl is not null,
                version = package?.Version ?? latest?.Version,
                size = package?.Size,
                sha256 = package?.Sha256,
                downloadUrl = package is not null ? "/api/agent-package/download" : latest?.AgentDownloadUrl,
            });
        }).RequireAuthorization();

        app.MapGet("/api/agent-package/download", async (CurrentUser cu, UpdateService updates, CancellationToken ct) =>
        {
            await cu.GetAsync();
            if (updates.GetAgentPackage() is { } p) return Results.File(p.Path, "application/x-msi", UpdateService.AgentFileName);
            var latest = (await updates.CheckAsync(false, ct)).Latest?.AgentDownloadUrl;
            return latest is null ? Results.NotFound() : Results.Redirect(latest);
        }).RequireAuthorization();

        // ----- Auto-Update des Agents (Agent-Token) -----
        // info=1 (Agent ab 0.5): immer 200 mit Vorgabe, der Agent entscheidet selbst. Ohne info (ältere Agents): 204 = nichts zu tun,
        // denn diese installieren jede gemeldete Version sofort. Bei der Vorgabe "Nie" bekommen sie daher kein Update angeboten.
        app.MapGet("/api/agent/update", async (string? current, string? info, UpdateService updates, Setup.SetupService setup) =>
        {
            var policy = (await setup.GetSettingsAsync()).AgentUpdatePolicy;
            var p = updates.GetAgentPackage();
            var available = p is not null && UpdateService.IsNewer(p.Version, current);
            if (info is "1" or "true")
                return Results.Ok(available
                    ? new AgentUpdateInfo(true, p!.Version, p.Sha256, p.Size, "/api/agent/package", policy)
                    : new AgentUpdateInfo(false, p?.Version, null, null, null, policy));
            if (!available || policy == AgentUpdatePolicy.Never) return Results.NoContent();
            return Results.Ok(new { p!.Version, p.Sha256, p.Size, url = "/api/agent/package" });
        }).RequireAuthorization(pb => pb.AddAuthenticationSchemes(AuthSchemes.Agent).RequireAuthenticatedUser());

        app.MapGet("/api/agent/package", (UpdateService updates) =>
            updates.GetAgentPackage() is { } p ? Results.File(p.Path, "application/x-msi", UpdateService.AgentFileName) : Results.NotFound())
            .RequireAuthorization(pb => pb.AddAuthenticationSchemes(AuthSchemes.Agent).RequireAuthenticatedUser());
    }
}
