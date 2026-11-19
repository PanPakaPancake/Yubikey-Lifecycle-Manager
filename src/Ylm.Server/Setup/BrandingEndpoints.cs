using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Pdf;
using Ylm.Server.Security;

namespace Ylm.Server.Setup;

/// <summary>Whitelabeling: Erscheinungsbild der PIN-Briefe, Berichte und Exporte.</summary>
public static class BrandingEndpoints
{
    private static object Dto(Branding? b) => new
    {
        b?.CompanyName,
        b?.AccentColor,
        b?.Contact,
        b?.Footer,
        HasLogo = b?.Logo is { Length: > 0 },
        b?.UpdatedAt,
    };

    /// <summary>Partner-Vorgabe braucht „Globale Einstellungen“, ein Tenant „Tenants verwalten“ für diesen Tenant.</summary>
    private static async Task<PlatformUser> RequireEditAsync(CurrentUser cu, Guid? tenantId) =>
        tenantId is { } t ? await cu.RequireTenantAsync(t, Permission.ManageTenants) : await cu.RequireAsync(Permission.ManageSettings);

    public static void MapBranding(this IEndpointRouteBuilder app)
    {
        // Öffentlich (ohne Anmeldung) für die Passkey-Infoseiten der Endnutzer: nur Name, Farbe, Kontakt und Logo.
        // ?t=<Tenant-Id> liefert das wirksame Erscheinungsbild des Tenants, sonst das des Partners.
        var pub = app.MapGroup("/api/public/branding").AllowAnonymous();
        pub.MapGet("", async (Guid? t, BrandingService branding, YlmDbContext db) =>
        {
            var tenantId = t is { } id && await db.Tenants.AnyAsync(x => x.Id == id) ? id : (Guid?)null;
            var b = await branding.ResolveAsync(tenantId);
            return Results.Ok(new
            {
                b.CompanyName,
                AccentColor = $"#{b.Accent.R:x2}{b.Accent.G:x2}{b.Accent.B:x2}",
                b.Contact,
                HasLogo = b.Logo is { Length: > 0 },
            });
        });
        pub.MapGet("/logo", async (Guid? t, BrandingService branding, YlmDbContext db) =>
        {
            var tenantId = t is { } id && await db.Tenants.AnyAsync(x => x.Id == id) ? id : (Guid?)null;
            var row = tenantId is null ? null : await branding.GetAsync(tenantId);
            if (row?.Logo is not { Length: > 0 }) row = await branding.GetAsync(null);
            return row?.Logo is { Length: > 0 } logo ? Results.File(logo, row.LogoContentType ?? "image/png") : Results.NotFound();
        });

        var g = app.MapGroup("/api/branding").RequireAuthorization();

        g.MapGet("", async (Guid? tenantId, CurrentUser cu, BrandingService branding) =>
        {
            if (tenantId is { } t) await cu.RequireTenantAsync(t, Permission.ReadInventory);
            else await cu.RequireAsync(Permission.ReadInventory);
            var partner = await branding.GetAsync(null);
            var effective = await branding.ResolveAsync(tenantId);
            return Results.Ok(new
            {
                Partner = Dto(partner),
                Tenant = tenantId is null ? null : Dto(await branding.GetAsync(tenantId)),
                Effective = new
                {
                    effective.CompanyName,
                    AccentColor = $"#{effective.Accent.R:x2}{effective.Accent.G:x2}{effective.Accent.B:x2}",
                    effective.Contact,
                    effective.Footer,
                    HasLogo = effective.Logo is { Length: > 0 },
                },
            });
        });

        g.MapPut("", async (Guid? tenantId, BrandingService.BrandingValues body, CurrentUser cu, BrandingService branding, AuditService audit) =>
        {
            var user = await RequireEditAsync(cu, tenantId);
            var before = Dto(await branding.GetAsync(tenantId));
            var row = await branding.SaveAsync(tenantId, body);
            await audit.WriteAsync(AuditService.User(user), AuditActions.BrandingChanged, tenantId, details: new { before, after = Dto(row) });
            return Results.Ok(Dto(row));
        });

        // Logo als Rohdaten (PNG/JPEG) im Body.
        g.MapPut("/logo", async (Guid? tenantId, HttpRequest req, CurrentUser cu, BrandingService branding, AuditService audit) =>
        {
            var user = await RequireEditAsync(cu, tenantId);
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            if (ms.Length == 0) throw ApiException.BadRequest("empty", "Keine Bilddatei empfangen.");
            var row = await branding.SetLogoAsync(tenantId, ms.ToArray());
            await audit.WriteAsync(AuditService.User(user), AuditActions.BrandingChanged, tenantId, details: new { logo = "hochgeladen", bytes = ms.Length });
            return Results.Ok(Dto(row));
        });

        g.MapDelete("/logo", async (Guid? tenantId, CurrentUser cu, BrandingService branding, AuditService audit) =>
        {
            var user = await RequireEditAsync(cu, tenantId);
            var row = await branding.SetLogoAsync(tenantId, null);
            await audit.WriteAsync(AuditService.User(user), AuditActions.BrandingChanged, tenantId, details: new { logo = "entfernt" });
            return Results.Ok(Dto(row));
        });

        // effective=true: das Logo, das in den Dokumenten erscheint (eigenes oder vom Partner).
        g.MapGet("/logo", async (Guid? tenantId, bool? effective, CurrentUser cu, BrandingService branding) =>
        {
            if (tenantId is { } t) await cu.RequireTenantAsync(t, Permission.ReadInventory);
            else await cu.RequireAsync(Permission.ReadInventory);
            var row = await branding.GetAsync(tenantId);
            if (row?.Logo is not { Length: > 0 } && effective == true && tenantId is not null) row = await branding.GetAsync(null);
            return row?.Logo is { Length: > 0 } logo ? Results.File(logo, row.LogoContentType ?? "image/png") : Results.NotFound();
        });

        // Muster-PIN-Brief mit dem wirksamen Erscheinungsbild.
        g.MapGet("/preview.pdf", async (Guid? tenantId, CurrentUser cu, BrandingService branding, YlmDbContext db, HttpRequest request, Microsoft.Extensions.Options.IOptions<YlmOptions> options) =>
        {
            if (tenantId is { } t) await cu.RequireTenantAsync(t, Permission.ReadInventory);
            else await cu.RequireAsync(Permission.ReadInventory);
            var brand = await branding.ResolveAsync(tenantId);
            var tenantName = tenantId is { } id ? (await db.Tenants.FindAsync(id))?.DisplayName ?? "Tenant" : "Muster-Tenant";
            using var _ = Theme.Use(brand);
            var pdf = PinLetterPdf.Create(new PinLetterData(brand.CompanyName ?? "", tenantName, "Erika Mustermann", "erika.mustermann@example.com",
                "482913", "YubiKey 5 NFC", 12345678, true, DateTime.Now,
                InfoUrl: PinLetterPdf.InfoUrl(await PinLetterPdf.BaseUrlAsync(db, options.Value, request), tenantId)));
            return Results.File(pdf, "application/pdf", "Muster-PIN-Brief.pdf");
        });
    }
}
