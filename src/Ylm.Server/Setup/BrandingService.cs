using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PdfSharp.Drawing;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Pdf;

namespace Ylm.Server.Setup;

/// <summary>Whitelabeling der erzeugten Dokumente: Vorgabe des Partners, pro Tenant einzeln überschreibbar.</summary>
public sealed partial class BrandingService(YlmDbContext db)
{
    public const int MaxLogoBytes = 1024 * 1024;

    public sealed record BrandingValues(string? CompanyName, string? AccentColor, string? Contact, string? Footer);

    public Task<Branding?> GetAsync(Guid? tenantId, CancellationToken ct = default) =>
        db.Brandings.AsNoTracking().FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);

    /// <summary>Wirksames Erscheinungsbild für einen Tenant (oder für Dokumente über mehrere Tenants: nur Partner).</summary>
    public async Task<PdfBrand> ResolveAsync(Guid? tenantId, CancellationToken ct = default)
    {
        var rows = await db.Brandings.AsNoTracking().Where(b => b.TenantId == null || (tenantId != null && b.TenantId == tenantId)).ToListAsync(ct);
        var partner = rows.FirstOrDefault(r => r.TenantId == null);
        var tenant = tenantId is null ? null : rows.FirstOrDefault(r => r.TenantId == tenantId);
        var org = (await db.Installation.AsNoTracking().FirstOrDefaultAsync(ct))?.OrganizationName;
        string? Pick(Func<Branding, string?> f) => (tenant is null ? null : Clean(f(tenant))) ?? (partner is null ? null : Clean(f(partner)));
        var color = Pick(b => b.AccentColor);
        return new PdfBrand(
            Pick(b => b.CompanyName) ?? Clean(org),
            color is not null && TryParseColor(color, out var c) ? c : Theme.DefaultAccent,
            tenant?.Logo ?? partner?.Logo,
            Pick(b => b.Contact),
            Pick(b => b.Footer),
            ParseLetter(tenant?.LetterTexts),
            ParseLetter(partner?.LetterTexts));
    }

    public static Dictionary<string, string>? ParseLetter(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>Eigene Brieftexte speichern; nur bekannte Felder, Standardtexte und leere Felder werden nicht gespeichert.</summary>
    public async Task<Branding> SaveLetterAsync(Guid? tenantId, IReadOnlyDictionary<string, string?>? texts, CancellationToken ct = default)
    {
        // Partner: Standardtext nicht speichern. Tenant: auch den Standardtext behalten, er kann bewusst den Partnertext ersetzen.
        var clean = LetterTemplate.Clean(texts, dropDefaults: tenantId is null);
        var row = await RowAsync(tenantId, ct);
        row.LetterTexts = clean.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(clean);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<Branding> SaveAsync(Guid? tenantId, BrandingValues v, CancellationToken ct = default)
    {
        var color = Clean(v.AccentColor);
        if (color is not null && !TryParseColor(color, out _))
            throw ApiException.BadRequest("invalid_color", "Die Farbe bitte als #rrggbb angeben, z. B. #1f5fbf.");
        if ((v.CompanyName?.Length ?? 0) > 120 || (v.Contact?.Length ?? 0) > 200 || (v.Footer?.Length ?? 0) > 200)
            throw ApiException.BadRequest("too_long", "Firmenname höchstens 120, Kontakt und Fußzeile höchstens 200 Zeichen.");
        var row = await RowAsync(tenantId, ct);
        row.CompanyName = Clean(v.CompanyName);
        row.AccentColor = color?.ToLowerInvariant();
        row.Contact = Clean(v.Contact);
        row.Footer = Clean(v.Footer);
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<Branding> SetLogoAsync(Guid? tenantId, byte[]? logo, CancellationToken ct = default)
    {
        string? type = null;
        if (logo is not null)
        {
            if (logo.Length > MaxLogoBytes) throw ApiException.BadRequest("too_large", "Das Logo darf höchstens 1 MB groß sein.");
            type = ImageType(logo) ?? throw ApiException.BadRequest("invalid_image", "Bitte ein Logo als PNG oder JPEG hochladen.");
            try
            {
                using var ms = new MemoryStream(logo);
                using var _ = XImage.FromStream(ms);
            }
            catch (Exception)
            {
                throw ApiException.BadRequest("invalid_image", "Die Bilddatei lässt sich nicht lesen.");
            }
        }
        var row = await RowAsync(tenantId, ct);
        row.Logo = logo;
        row.LogoContentType = type;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return row;
    }

    private async Task<Branding> RowAsync(Guid? tenantId, CancellationToken ct)
    {
        var row = await db.Brandings.FirstOrDefaultAsync(b => b.TenantId == tenantId, ct);
        if (row is not null) return row;
        row = new Branding { TenantId = tenantId };
        db.Brandings.Add(row);
        return row;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static bool TryParseColor(string s, out XColor color)
    {
        color = default;
        if (!ColorRegex().IsMatch(s)) return false;
        var v = int.Parse(s.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        color = XColor.FromArgb((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff);
        return true;
    }

    private static string? ImageType(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4e && b[3] == 0x47 ? "image/png"
        : b.Length > 3 && b[0] == 0xff && b[1] == 0xd8 && b[2] == 0xff ? "image/jpeg"
        : null;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorRegex();
}
