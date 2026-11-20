using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;

namespace Ylm.Server.Rollout;

/// <summary>Passkey-Rollout pro Tenant: Übersicht nach MFA-Stand, TAPs erzeugen, andere Methoden entfernen.</summary>
public static class RolloutEndpoints
{
    public sealed record TapRequest(List<string> UserIds, DateTimeOffset? StartDateTime, int? LifetimeInMinutes, bool? IsUsableOnce, bool ReplaceExisting = true);
    public sealed record BulkRemoveRequest(List<string> UserIds, List<string> Kinds, bool KeepPhishingResistantOnly = true);

    private static async Task<Tenant> TenantAsync(YlmDbContext db, Guid id) =>
        await db.Tenants.FirstOrDefaultAsync(t => t.Id == id && t.Status == TenantStatus.Active) ?? throw ApiException.NotFound("Tenant");

    private static RolloutFilter Filter(string? stages, bool? guests, bool? disabled, bool? target) => new(
        string.IsNullOrWhiteSpace(stages) ? null : stages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        guests == true, disabled == true, target == true);

    public static void MapRollout(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants/{id:guid}/rollout").RequireAuthorization();

        g.MapGet("", async (Guid id, bool? refresh, CurrentUser cu, YlmDbContext db, RolloutService rollout, CancellationToken ct) =>
        {
            await cu.RequireTenantAsync(id, Permission.ReadReports);
            var s = await rollout.GetAsync(await TenantAsync(db, id), refresh == true, ct);
            return Results.Ok(new
            {
                s.GeneratedAt, s.FromReport, s.HasSignInData, s.TargetDefined,
                Methods = AuthMethodCatalog.Known.DistinctBy(m => m.Label).Select(m => new { m.Name, m.Label, Strength = m.Strength.ToString(), m.IsPasskey }),
                s.Users,
            });
        });

        g.MapGet("/export.{format}", async (Guid id, string format, string? stages, bool? guests, bool? disabled, bool? target,
            CurrentUser cu, YlmDbContext db, RolloutService rollout, AuditService audit, CancellationToken ct) =>
        {
            format = format.ToLowerInvariant();
            if (format is not ("csv" or "xlsx")) return Results.NotFound();
            var user = await cu.RequireTenantAsync(id, Permission.ReadReports);
            var tenant = await TenantAsync(db, id);
            var s = await rollout.GetAsync(tenant, false, ct);
            var rows = RolloutService.Apply(s, Filter(stages, guests, disabled, target)).ToList();
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, id, details: new { kind = "rollout." + format, stages, guests, disabled, target });
            string[] header = ["Name", "UPN", "Abteilung", "Stand", "Methoden", "Standard-MFA", "Typ", "Aktiv", "Admin", "Zielgruppe", "Keys im Inventar", "Letzte Anmeldung"];
            var table = rows.Select(u => (IReadOnlyList<object?>)new object?[]
            {
                u.DisplayName, u.Upn, u.Department, StageLabel(u.Stage),
                string.Join(", ", u.Methods.Where(m => m != "password").Select(m => AuthMethodCatalog.Get(m).Label).Distinct()),
                u.DefaultMfaMethod is null ? null : AuthMethodCatalog.Get(u.DefaultMfaMethod).Label,
                u.UserType == "Guest" ? "Gast" : "Mitglied", u.AccountEnabled ? "ja" : "nein", u.IsAdmin ? "ja" : "nein",
                u.InTarget ? "ja" : "nein", u.PlatformKeys, u.LastSignIn?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            }).ToList();
            var name = $"passkey-rollout-{DateTime.Today:yyyy-MM-dd}";
            return format == "csv"
                ? Results.File(Csv.Write(header, table.Select(r => (IReadOnlyList<string?>)r.Select(v => v?.ToString()).ToList())), "text/csv; charset=utf-8", name + ".csv")
                : Results.File(Xlsx.Write("Passkey-Rollout", header, table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        });

        g.MapGet("/users/{userId}/methods", async (Guid id, string userId, CurrentUser cu, YlmDbContext db, RolloutService rollout, CancellationToken ct) =>
        {
            await cu.RequireTenantAsync(id, Permission.ReadReports);
            var methods = await rollout.ListMethodsAsync(await TenantAsync(db, id), userId, ct);
            return Results.Ok(methods.Select(RolloutService.ToDto));
        });

        g.MapDelete("/users/{userId}/methods/{kind}/{methodId}", async (Guid id, string userId, string kind, string methodId,
            CurrentUser cu, YlmDbContext db, RolloutService rollout, CancellationToken ct) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.RemoveAuthMethods);
            var methods = await rollout.RemoveMethodAsync(await TenantAsync(db, id), userId, kind, methodId, user, ct);
            return Results.Ok(methods.Select(RolloutService.ToDto));
        });

        g.MapPost("/remove-methods", async (Guid id, BulkRemoveRequest body, CurrentUser cu, YlmDbContext db, RolloutService rollout, CancellationToken ct) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.RemoveAuthMethods);
            return Results.Ok(await rollout.RemoveBulkAsync(await TenantAsync(db, id), body.UserIds ?? [], body.Kinds ?? [], body.KeepPhishingResistantOnly, user, ct));
        });

        g.MapGet("/tap-policy", async (Guid id, CurrentUser cu, YlmDbContext db, RolloutService rollout, CancellationToken ct) =>
        {
            await cu.RequireTenantAsync(id, Permission.ReadReports);
            return Results.Ok(await rollout.GetTapPolicyAsync(await TenantAsync(db, id), ct));
        });

        g.MapPost("/tap-policy/enable", async (Guid id, CurrentUser cu, YlmDbContext db, RolloutService rollout, CancellationToken ct) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.ManageTenants);
            return Results.Ok(await rollout.EnableTapPolicyAsync(await TenantAsync(db, id), AuditService.User(user), ct));
        });

        g.MapPost("/taps", async (Guid id, TapRequest body, HttpContext http, CurrentUser cu, YlmDbContext db, RolloutService rollout, CancellationToken ct) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.IssueTemporaryAccessPass);
            var results = await rollout.CreateTapsAsync(await TenantAsync(db, id), body.UserIds ?? [], body.StartDateTime, body.LifetimeInMinutes, body.IsUsableOnce,
                body.ReplaceExisting, user, ct);
            // Die Codes sollen in keinem Zwischenspeicher landen.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(results);
        });
    }

    public static string StageLabel(string stage) => stage switch
    {
        nameof(RolloutStage.SecurityKey) => "Sicherheitsschlüssel",
        nameof(RolloutStage.OtherPasskey) => "Anderer Passkey / Windows Hello",
        nameof(RolloutStage.ClassicMfa) => "Nur klassische MFA",
        _ => "Keine MFA",
    };
}
