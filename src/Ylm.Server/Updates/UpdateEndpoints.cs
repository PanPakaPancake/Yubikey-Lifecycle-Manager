using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Security;

namespace Ylm.Server.Updates;

public static class UpdateEndpoints
{
    public sealed record ApplyRequest(string? Version);

    public static void MapUpdates(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/platform/updates").RequireAuthorization();

        g.MapGet("", async (bool? refresh, CurrentUser cu, UpdateService updates, YlmDbContext db, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            var check = await updates.CheckAsync(refresh == true, ct);
            var package = updates.GetAgentPackage();
            var agents = await db.Agents.AsNoTracking().Where(a => a.RevokedAt == null).Select(a => a.AgentVersion).ToListAsync(ct);
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
                    outdated = package is null ? 0 : agents.Count(v => UpdateService.IsNewer(package.Version, v)),
                },
            });
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
        app.MapGet("/api/agent/update", (string? current, UpdateService updates) =>
        {
            var p = updates.GetAgentPackage();
            if (p is null || !UpdateService.IsNewer(p.Version, current)) return Results.NoContent();
            return Results.Ok(new { p.Version, p.Sha256, p.Size, url = "/api/agent/package" });
        }).RequireAuthorization(pb => pb.AddAuthenticationSchemes(AuthSchemes.Agent).RequireAuthenticatedUser());

        app.MapGet("/api/agent/package", (UpdateService updates) =>
            updates.GetAgentPackage() is { } p ? Results.File(p.Path, "application/x-msi", UpdateService.AgentFileName) : Results.NotFound())
            .RequireAuthorization(pb => pb.AddAuthenticationSchemes(AuthSchemes.Agent).RequireAuthenticatedUser());
    }
}
