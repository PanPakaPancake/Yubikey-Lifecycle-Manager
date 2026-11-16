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
    public static readonly XColor Accent = XColor.FromArgb(0x1f, 0x5f, 0xbf);
    public static readonly XColor AccentSoft = XColor.FromArgb(0xe8, 0xf0, 0xfb);
    public static readonly XColor Line = XColor.FromArgb(0xd6, 0xdb, 0xe3);
    public static readonly XColor Warn = XColor.FromArgb(0xb4, 0x5f, 0x06);
    public static readonly XColor WarnSoft = XColor.FromArgb(0xfd, 0xf3, 0xe2);
    public static readonly XColor Gold = XColor.FromArgb(0xd4, 0xa5, 0x2a);
    public static readonly XColor KeyBody = XColor.FromArgb(0x2b, 0x2f, 0x36);

    public static XFont F(double size, bool bold = false) => new(Font, size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    public static void Text(XGraphics g, string text, XFont font, XColor color, double x, double y, double w, double h)
    {
        var tf = new XTextFormatter(g);
        tf.DrawString(text, font, new XSolidBrush(color), new XRect(x, y, w, h), XStringFormats.TopLeft);
    }
}
