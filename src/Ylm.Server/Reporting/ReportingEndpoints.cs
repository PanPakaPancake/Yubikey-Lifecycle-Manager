using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Infrastructure;
using Ylm.Server.Pdf;
using Ylm.Server.Security;
using Ylm.Server.Setup;

namespace Ylm.Server.Reporting;

public static class ReportingEndpoints
{
    private static async Task<List<Guid>> ScopeAsync(CurrentUser cu, Guid? tenantId)
    {
        var ids = await cu.AccessibleTenantIdsAsync();
        if (tenantId is not { } t) return ids;
        if (!ids.Contains(t)) throw ApiException.Forbidden("Sie sind für diesen Tenant nicht zuständig.");
        return [t];
    }

    public static void MapReporting(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        g.MapGet("/dashboard", async (Guid? tenantId, CurrentUser cu, DashboardService dash) =>
        {
            await cu.RequireAsync(Permission.ReadReports);
            return Results.Ok(await dash.BuildAsync(await ScopeAsync(cu, tenantId)));
        });

        g.MapGet("/exports/keys.csv", async (Guid? tenantId, CurrentUser cu, ExportService ex, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ReadReports);
            var ids = await ScopeAsync(cu, tenantId);
            var csv = await ex.KeysCsvAsync(ids);
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, tenantId, details: new { kind = "keys.csv" });
            return Results.File(csv, "text/csv; charset=utf-8", $"yubikeys-{DateTime.Today:yyyy-MM-dd}.csv");
        });

        g.MapGet("/exports/registrations.csv", async (Guid? tenantId, CurrentUser cu, ExportService ex, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ReadReports);
            var csv = await ex.RegistrationsCsvAsync(await ScopeAsync(cu, tenantId));
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, tenantId, details: new { kind = "registrations.csv" });
            return Results.File(csv, "text/csv; charset=utf-8", $"registrierungen-{DateTime.Today:yyyy-MM-dd}.csv");
        });

        g.MapGet("/exports/dashboard.pdf", async (Guid? tenantId, CurrentUser cu, DashboardService dash, SetupService setup, AuditService audit, Data.YlmDbContext db) =>
        {
            var user = await cu.RequireAsync(Permission.ReadReports);
            var ids = await ScopeAsync(cu, tenantId);
            var d = await dash.BuildAsync(ids);
            var settings = await setup.GetSettingsAsync();
            var scope = tenantId is { } t ? (await db.Tenants.FindAsync(t))?.DisplayName ?? "Tenant" : "Alle Tenants";
            var pdf = ReportPdf.Create(settings.OrganizationName, scope, d);
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, tenantId, details: new { kind = "dashboard.pdf" });
            return Results.File(pdf, "application/pdf", $"yubikey-bericht-{DateTime.Today:yyyy-MM-dd}.pdf");
        });
    }
}
