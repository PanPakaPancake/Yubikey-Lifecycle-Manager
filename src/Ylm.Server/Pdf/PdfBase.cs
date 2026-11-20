using PdfSharp.Drawing;
using PdfSharp.Drawing.Layout;
using PdfSharp.Fonts;
using PdfSharp.WPFonts;

namespace Ylm.Server.Pdf;

/// <summary>Embedded fonts so PDFs render identically in Docker without system fonts.</summary>
public sealed class YlmFontResolver : IFontResolver
{
    private static readonly object Lock = new();
    private static bool _installed;

    public static void Install()
    {
        lock (Lock)
        {
            if (_installed) return;
            GlobalFontSettings.FontResolver = new YlmFontResolver();
            _installed = true;
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "SegoeWP-Bold" : familyName.Contains("Light", StringComparison.OrdinalIgnoreCase) ? "SegoeWP-Light" : "SegoeWP", false, italic);

    public byte[]? GetFont(string faceName) => faceName switch
    {
        "SegoeWP-Bold" => FontDataHelper.SegoeWPBold,
        "SegoeWP-Light" => FontDataHelper.SegoeWPLight,
        _ => FontDataHelper.SegoeWP,
    };
}

internal static class Theme
{
    public const string Font = "Segoe WP";
    public static readonly XColor Ink = XColor.FromArgb(0x1d, 0x23, 0x2e);
    public static readonly XColor Muted = XColor.FromArgb(0x5b, 0x65, 0x75);
    public static readonly XColor DefaultAccent = XColor.FromArgb(0x1f, 0x5f, 0xbf);
    private static readonly AsyncLocal<PdfBrand?> CurrentBrand = new();

    /// <summary>Erscheinungsbild des Dokuments, das gerade erzeugt wird (siehe <see cref="Use"/>).</summary>
    public static PdfBrand Brand => CurrentBrand.Value ?? PdfBrand.Default;
    public static XColor Accent => Brand.Accent;
    /// <summary>Akzentfarbe, stark aufgehellt (Flächen hinter Text).</summary>
    public static XColor AccentSoft => Mix(Brand.Accent, 0.1);

    /// <summary>Setzt das Erscheinungsbild für alle PDFs, die im aktuellen Ablauf erzeugt werden.</summary>
    public static IDisposable Use(PdfBrand? brand)
    {
        var before = CurrentBrand.Value;
        CurrentBrand.Value = brand;
        return new Restore(() => CurrentBrand.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    public static XColor Mix(XColor c, double share) => XColor.FromArgb(
        (int)(255 - (255 - c.R) * share), (int)(255 - (255 - c.G) * share), (int)(255 - (255 - c.B) * share));

    /// <summary>Logo rechtsbündig in das Feld zeichnen (Seitenverhältnis bleibt). Gibt false zurück, wenn kein Logo da ist.</summary>
    public static bool DrawLogo(XGraphics g, double right, double top, double maxWidth, double maxHeight)
    {
        if (Brand.Logo is not { Length: > 0 } bytes) return false;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var img = XImage.FromStream(ms);
            var scale = Math.Min(maxWidth / img.PointWidth, maxHeight / img.PointHeight);
            var w = img.PointWidth * scale;
            var h = img.PointHeight * scale;
            g.DrawImage(img, right - w, top + (maxHeight - h) / 2, w, h);
            return true;
        }
        catch (Exception)
        {
            return false; // defektes Bild: Dokument trotzdem erzeugen
        }
    }

    /// <summary>Kontakt und Fußzeile des Erscheinungsbilds unten auf die Seite.</summary>
    public static void DrawBrandFooter(XGraphics g, double x, double bottom, double width)
    {
        var parts = new[] { Brand.Contact, Brand.Footer }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (parts.Count == 0) return;
        Text(g, string.Join("  ·  ", parts), F(7.5), Muted, x, bottom - 10, width, 10);
    }
    public static readonly XColor Line = XColor.FromArgb(0xd6, 0xdb, 0xe3);
    public static readonly XColor Warn = XColor.FromArgb(0xb4, 0x5f, 0x06);
    public static readonly XColor WarnSoft = XColor.FromArgb(0xfd, 0xf3, 0xe2);
    public static readonly XColor Gold = XColor.FromArgb(0xd4, 0xa5, 0x2a);
    public static readonly XColor KeyBody = XColor.FromArgb(0x2b, 0x2f, 0x36);
    /// <summary>Hellgrauer Key-Körper für Zeichnungen (spart Toner gegenüber Schwarz).</summary>
    public static readonly XColor KeyLight = XColor.FromArgb(0xee, 0xf0, 0xf4);

    public static XFont F(double size, bool bold = false) => new(Font, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    public static void Text(XGraphics g, string text, XFont font, XColor color, double x, double y, double w, double h)
    {
        var tf = new XTextFormatter(g);
        tf.DrawString(text, font, new XSolidBrush(color), new XRect(x, y, w, h), XStringFormats.TopLeft);
    }
}

/// <summary>Erscheinungsbild eines Dokuments (Whitelabeling).</summary>
public sealed record PdfBrand(string? CompanyName, XColor Accent, byte[]? Logo, string? Contact, string? Footer,
    IReadOnlyDictionary<string, string>? LetterTenant = null, IReadOnlyDictionary<string, string>? LetterPartner = null)
{
    public static readonly PdfBrand Default = new(null, Theme.DefaultAccent, null, null, null);

    /// <summary>Wirksamer Brieftext (Tenant, dann Partner, dann Standard), noch mit Platzhaltern.</summary>
    public string Letter(string key) => LetterTemplate.Resolve(key, LetterTenant, LetterPartner);
}
