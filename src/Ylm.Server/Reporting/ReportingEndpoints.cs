using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Infrastructure;
using Ylm.Server.Pdf;
using Ylm.Server.Security;
using Ylm.Server.Setup;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Reporting;

public static class ReportingEndpoints
{
    private static async Task<List<Guid>> ScopeAsync(CurrentUser cu, Guid? tenantId)
    {
        var ids = await cu.AccessibleTenantIdsAsync();
        if (tenantId is not { } t) return ids;
        if (!ids.Contains(t)) throw ApiException.Forbidden(L.T("Sie sind für diesen Tenant nicht zuständig."));
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

        // Wer hat gerade welchen Key? Gewählter Tenant oder alle zuständigen Tenants.
        g.MapGet("/assignments", async (Guid? tenantId, string? q, string? usage, bool? issues, CurrentUser cu, AssignmentService assignments) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            return Results.Ok(await assignments.BuildAsync(await ScopeAsync(cu, tenantId), new AssignmentFilter(q, usage, issues == true)));
        });

        g.MapGet("/exports/assignments.{format}", async (string format, Guid? tenantId, string? q, string? usage, bool? issues, CurrentUser cu, AssignmentService assignments,
            SetupService setup, BrandingService branding, AuditService audit, Data.YlmDbContext db) =>
        {
            format = format.ToLowerInvariant();
            if (format is not ("csv" or "xlsx" or "pdf")) return Results.NotFound();
            var user = await cu.RequireAsync(Permission.ReadReports);
            var ids = await ScopeAsync(cu, tenantId);
            var o = await assignments.BuildAsync(ids, new AssignmentFilter(q, usage, issues == true));
            var includeTenant = tenantId is null;
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, tenantId, details: new { kind = "assignments." + format, q, usage, issues });
            var name = $"key-zuweisungen-{DateTime.Today:yyyy-MM-dd}";
            if (format == "pdf")
            {
                var settings = await setup.GetSettingsAsync();
                var scope = tenantId is { } t ? (await db.Tenants.FindAsync(t))?.DisplayName ?? L.T("Tenant") : L.T("Alle Tenants");
                var brand = await branding.ResolveAsync(tenantId);
                using var _ = Pdf.Theme.Use(brand);
                return Results.File(Pdf.AssignmentPdf.Create(brand.CompanyName ?? settings.OrganizationName, scope, o, includeTenant), "application/pdf", name + ".pdf");
            }
            var (header, rows) = AssignmentService.Table(o, includeTenant);
            return format == "csv"
                ? Results.File(Csv.Write(header, rows.Select(r => (IReadOnlyList<string?>)r.Select(v => v?.ToString()).ToList())), "text/csv; charset=utf-8", name + ".csv")
                : Results.File(Xlsx.Write(L.T("Key-Zuweisungen"), header, rows), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        });

        // Yubico-Sicherheitshinweise: betroffene Keys und Personen je Hinweis. Ohne tenantId zusätzlich das Partnerlager (mit Recht).
        g.MapGet("/advisories", async (Guid? tenantId, CurrentUser cu, AdvisoryService advisories) =>
        {
            var me = await cu.RequireAsync(Permission.ReadInventory);
            return Results.Ok(await advisories.BuildAsync(await ScopeAsync(cu, tenantId), tenantId is null && CurrentUser.Has(me, Permission.ManagePartnerStock)));
        });

        g.MapGet("/advisories/catalog", () => Results.Ok(FirmwareAdvisories.Known.Select(a => new { a.Id, a.Title, a.AffectsFido })));

        g.MapGet("/exports/advisories.{format}", async (string format, Guid? tenantId, string? advisory, CurrentUser cu, AdvisoryService advisories, AuditService audit) =>
        {
            format = format.ToLowerInvariant();
            if (format is not ("csv" or "xlsx")) return Results.NotFound();
            if (advisory is not null && FirmwareAdvisories.Find(advisory) is null) throw ApiException.NotFound(L.T("Sicherheitshinweis"));
            var user = await cu.RequireAsync(Permission.ReadReports);
            var o = await advisories.BuildAsync(await ScopeAsync(cu, tenantId), tenantId is null && CurrentUser.Has(user, Permission.ManagePartnerStock));
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, tenantId, details: new { kind = "advisories." + format, advisory });
            var (header, rows) = AdvisoryService.Table(o, advisory, tenantId is null);
            var name = $"sicherheitshinweise{(advisory is null ? "" : "-" + advisory.ToLowerInvariant())}-{DateTime.Today:yyyy-MM-dd}";
            return format == "csv"
                ? Results.File(Csv.Write(header, rows.Select(r => (IReadOnlyList<string?>)r.Select(v => v?.ToString()).ToList())), "text/csv; charset=utf-8", name + ".csv")
                : Results.File(Xlsx.Write(L.T("Sicherheitshinweise"), header, rows), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", name + ".xlsx");
        });

        g.MapGet("/exports/dashboard.pdf", async (Guid? tenantId, CurrentUser cu, DashboardService dash, SetupService setup, BrandingService branding, AuditService audit, Data.YlmDbContext db) =>
        {
            var user = await cu.RequireAsync(Permission.ReadReports);
            var ids = await ScopeAsync(cu, tenantId);
            var d = await dash.BuildAsync(ids);
            var settings = await setup.GetSettingsAsync();
            var scope = tenantId is { } t ? (await db.Tenants.FindAsync(t))?.DisplayName ?? L.T("Tenant") : L.T("Alle Tenants");
            var brand = await branding.ResolveAsync(tenantId);
            byte[] pdf;
            using (Theme.Use(brand)) pdf = ReportPdf.Create(brand.CompanyName ?? settings.OrganizationName, scope, d);
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, tenantId, details: new { kind = "dashboard.pdf" });
            return Results.File(pdf, "application/pdf", $"yubikey-bericht-{DateTime.Today:yyyy-MM-dd}.pdf");
        });
    }
}
