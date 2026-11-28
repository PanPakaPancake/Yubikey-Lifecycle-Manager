using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Pdf;
using Ylm.Server.Security;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Setup;

/// <summary>Whitelabeling: Erscheinungsbild der PIN-Briefe, Berichte und Exporte.</summary>
public static class BrandingEndpoints
{
    public sealed record LetterRequest(LetterLayout? Layout, bool? PinChangeForced);

    private static object Dto(Branding? b) => new
    {
        b?.CompanyName,
        b?.AccentColor,
        b?.Contact,
        b?.Footer,
        b?.Language,
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
                    Language = effective.Language ?? L.German,
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
            if (ms.Length == 0) throw ApiException.BadRequest("empty", L.T("Keine Bilddatei empfangen."));
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

        // ----- Brief-Editor: PIN-Brief aus Bausteinen mit Platzhaltern -----
        g.MapGet("/letter", async (Guid? tenantId, CurrentUser cu, BrandingService branding, YlmDbContext db, HttpRequest request, Microsoft.Extensions.Options.IOptions<YlmOptions> options) =>
        {
            if (tenantId is { } t) await cu.RequireTenantAsync(t, Permission.ReadInventory);
            else await cu.RequireAsync(Permission.ReadInventory);
            var brand = await branding.ResolveAsync(tenantId);
            // Bezeichnungen in der Sprache der Oberfläche, Vorgabetexte, Beispielwerte und geerbter Aufbau in der Sprache des Briefs.
            var types = LetterBlocks.Types;
            using var letterLanguage = L.Use(brand.Language ?? L.German);
            var letterTypes = LetterBlocks.Types;
            var (own, inherited) = await branding.LayoutsAsync(tenantId);
            var tenantName = tenantId is { } id ? (await db.Tenants.FindAsync(id))?.DisplayName ?? L.T("Tenant") : L.T("Muster-Tenant");
            var infoUrl = PinLetterPdf.InfoUrl(await PinLetterPdf.BaseUrlAsync(db, options.Value, request), tenantId);
            Dictionary<string, string?> sample;
            using (Theme.Use(brand))
                sample = PinLetterPdf.Values(new PinLetterData(brand.CompanyName ?? "", tenantName, L.T("Erika Mustermann"), L.T("erika.mustermann@example.com"),
                    "482913", "YubiKey 5 NFC", 12345678, true, DateTime.Now));
            return Results.Ok(new
            {
                Language = brand.Language ?? L.German,
                Types = types.Select((t, i) => t with { Props = t.Props.Select((p, j) => p with { Default = letterTypes[i].Props[j].Default }).ToList() }),
                Variables = LetterTemplate.Variables,
                Layout = own,
                Inherited = inherited,
                // Für die Darstellung im Editor: Beispielwerte und Erscheinungsbild wie im PDF.
                Sample = sample,
                Brand = new
                {
                    brand.CompanyName,
                    Accent = $"#{brand.Accent.R:x2}{brand.Accent.G:x2}{brand.Accent.B:x2}",
                    HasLogo = brand.Logo is { Length: > 0 },
                },
                InfoUrl = infoUrl?.Split('?')[0],
            });
        });

        // layout = null: eigenen Aufbau entfernen und wieder übernehmen (Tenant → Partner → Standard).
        g.MapPut("/letter", async (Guid? tenantId, LetterRequest body, CurrentUser cu, BrandingService branding, AuditService audit) =>
        {
            var user = await RequireEditAsync(cu, tenantId);
            var before = await branding.GetAsync(tenantId);
            var row = await branding.SaveLayoutAsync(tenantId, body.Layout);
            await audit.WriteAsync(AuditService.User(user), AuditActions.BrandingChanged, tenantId, details: new
            {
                letter = body.Layout is null ? L.T("Briefaufbau zurückgesetzt") : L.T("Briefaufbau geändert"),
                before = before?.LetterLayout ?? before?.LetterTexts,
                after = row.LetterLayout,
            });
            using var letterLanguage = L.Use((await branding.ResolveAsync(tenantId)).Language ?? L.German);
            var (own, inherited) = await branding.LayoutsAsync(tenantId);
            return Results.Ok(new { Layout = own, Inherited = inherited });
        });

        // Vorschau mit noch nicht gespeichertem Aufbau (für den Editor); ohne Aufbau der geerbte.
        g.MapPost("/letter/preview.pdf", async (Guid? tenantId, LetterRequest body, CurrentUser cu, BrandingService branding, YlmDbContext db, HttpRequest request, Microsoft.Extensions.Options.IOptions<YlmOptions> options) =>
        {
            await RequireEditAsync(cu, tenantId);
            var brand = await branding.ResolveAsync(tenantId);
            using var letterLanguage = L.Use(brand.Language ?? L.German);
            var layout = body.Layout is null ? (await branding.LayoutsAsync(tenantId)).Inherited : LetterBlocks.Validate(body.Layout);
            brand = brand with { Layout = layout };
            var tenantName = tenantId is { } id ? (await db.Tenants.FindAsync(id))?.DisplayName ?? L.T("Tenant") : L.T("Muster-Tenant");
            using var _ = Theme.Use(brand);
            var pdf = PinLetterPdf.Create(new PinLetterData(brand.CompanyName ?? "", tenantName, L.T("Erika Mustermann"), L.T("erika.mustermann@example.com"),
                "482913", "YubiKey 5 NFC", 12345678, body.PinChangeForced ?? true, DateTime.Now,
                InfoUrl: PinLetterPdf.InfoUrl(await PinLetterPdf.BaseUrlAsync(db, options.Value, request), tenantId)));
            return Results.File(pdf, "application/pdf");
        });

        // Muster-PIN-Brief mit dem wirksamen Erscheinungsbild.
        g.MapGet("/preview.pdf", async (Guid? tenantId, CurrentUser cu, BrandingService branding, YlmDbContext db, HttpRequest request, Microsoft.Extensions.Options.IOptions<YlmOptions> options) =>
        {
            if (tenantId is { } t) await cu.RequireTenantAsync(t, Permission.ReadInventory);
            else await cu.RequireAsync(Permission.ReadInventory);
            var brand = await branding.ResolveAsync(tenantId);
            var fileName = L.T("Muster-PIN-Brief.pdf");
            using var letterLanguage = L.Use(brand.Language ?? L.German);
            var tenantName = tenantId is { } id ? (await db.Tenants.FindAsync(id))?.DisplayName ?? L.T("Tenant") : L.T("Muster-Tenant");
            using var _ = Theme.Use(brand);
            var pdf = PinLetterPdf.Create(new PinLetterData(brand.CompanyName ?? "", tenantName, L.T("Erika Mustermann"), L.T("erika.mustermann@example.com"),
                "482913", "YubiKey 5 NFC", 12345678, true, DateTime.Now,
                InfoUrl: PinLetterPdf.InfoUrl(await PinLetterPdf.BaseUrlAsync(db, options.Value, request), tenantId)));
            return Results.File(pdf, "application/pdf", fileName);
        });
    }
}
