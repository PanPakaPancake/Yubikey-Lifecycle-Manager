using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Ylm.Agent.Tray;

/// <summary>Darstellung der Tray-App: wie in der Weboberfläche dunkel (Vorgabe), hell oder wie Windows.</summary>
internal enum ThemeMode { Dark = 0, Light = 1, System = 2 }

/// <summary>Farben des Designs „Bento“ aus der Weboberfläche (web/src/bento.css), für WinForms.</summary>
internal sealed record Palette(
    bool IsDark,
    Color Bg, Color Surface, Color Surface2, Color Ink, Color Muted, Color Line,
    Color Accent, Color Accent2, Color AccentInk, Color Brand2,
    Color Ok, Color Warn, Color Error, Color Violet, Color Green, Color Cyan, Color Amber)
{
    public static readonly Palette Dark = new(true,
        Hex("#050912"), Hex("#0b111e"), Hex("#101828"), Hex("#e3eeff"), Hex("#7d8ba6"), Hex("#1b2640"),
        Hex("#a78bfa"), Hex("#22d3ee"), Hex("#0b0618"), Hex("#22d3ee"),
        Hex("#34d399"), Hex("#fbbf24"), Hex("#fb7185"), Hex("#a78bfa"), Hex("#34d399"), Hex("#22d3ee"), Hex("#fbbf24"));

    public static readonly Palette Light = new(false,
        Hex("#f3f2fa"), Hex("#ffffff"), Hex("#f6f5fc"), Hex("#1e1b2e"), Hex("#6b6880"), Hex("#e7e4f3"),
        Hex("#6d28d9"), Hex("#8b5cf6"), Color.White, Hex("#0ea5e9"),
        Hex("#059669"), Hex("#d97706"), Hex("#e11d48"), Hex("#7c3aed"), Hex("#10b981"), Hex("#3b82f6"), Hex("#f59e0b"));

    public static Color Hex(string hex) => ColorTranslator.FromHtml(hex);
}

internal static class Theme
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.Dark;
    public static Palette P { get; private set; } = Palette.Dark;

    /// <summary>Darstellung wurde gewechselt: offene Fenster bauen sich neu auf.</summary>
    public static event Action? Changed;

    public static void Load() => Apply(TraySettings.Theme, raise: false);

    public static void Set(ThemeMode mode)
    {
        TraySettings.Theme = mode;
        Apply(mode, raise: true);
    }

    /// <summary>Nur für Vorschaubilder: Darstellung setzen, ohne sie zu speichern.</summary>
    internal static void Preview(ThemeMode mode) => Apply(mode, raise: false);

    private static void Apply(ThemeMode mode, bool raise)
    {
        Mode = mode;
        P = mode switch
        {
            ThemeMode.Light => Palette.Light,
            ThemeMode.System => WindowsUsesLightTheme() ? Palette.Light : Palette.Dark,
            _ => Palette.Dark,
        };
        if (raise) Changed?.Invoke();
    }

    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Bewegung erlaubt? Windows-Einstellung „Animationseffekte“ (entspricht prefers-reduced-motion).</summary>
    public static bool Animations => SystemInformation.UIEffectsEnabled;

    // ---------- Schriften ----------

    private static readonly Dictionary<(string, float, FontStyle), Font> Fonts = [];

    private static readonly string UiFamily = Pick("Segoe UI Variable Text", "Segoe UI");
    private static readonly string DisplayFamily = Pick("Segoe UI Variable Display", "Segoe UI");
    private static readonly string MonoFamily = Pick("Cascadia Mono", "Consolas");

    public static Font Ui(float size = 9.5f, FontStyle style = FontStyle.Regular) => Get(UiFamily, size, style);
    public static Font Display(float size, FontStyle style = FontStyle.Bold) => Get(DisplayFamily, size, style);
    public static Font Mono(float size = 8f, FontStyle style = FontStyle.Regular) => Get(MonoFamily, size, style);

    private static Font Get(string family, float size, FontStyle style)
    {
        var key = (family, size, style);
        if (!Fonts.TryGetValue(key, out var font)) Fonts[key] = font = new Font(family, size, style);
        return font;
    }

    private static string Pick(params string[] families)
    {
        using var installed = new System.Drawing.Text.InstalledFontCollection();
        var names = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return families.FirstOrDefault(names.Contains) ?? families[^1];
    }

    // ---------- Helfer ----------

    public static Color Alpha(Color c, int alpha) => Color.FromArgb(Math.Clamp(alpha, 0, 255), c);

    public static Color Mix(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new System.Drawing.Drawing2D.GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Titelleiste passend zur Darstellung (Windows 10 20H1+: dunkel; Windows 11: Farbe der Fläche, runde Ecken).</summary>
    public static void ApplyWindowFrame(IntPtr handle)
    {
        try
        {
            var dark = P.IsDark ? 1 : 0;
            DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = ColorTranslator.ToWin32(P.Bg);
            DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR (Windows 11)
            var text = ColorTranslator.ToWin32(P.Ink);
            DwmSetWindowAttribute(handle, 36, ref text, sizeof(int)); // DWMWA_TEXT_COLOR
            var border = ColorTranslator.ToWin32(P.IsDark ? Mix(P.Bg, P.Accent2, .35f) : P.Line);
            DwmSetWindowAttribute(handle, 34, ref border, sizeof(int)); // DWMWA_BORDER_COLOR
        }
        catch (Exception) { /* ältere Windows-Versionen: Standardrahmen */ }
    }

    /// <summary>Runde Ecken für Popups wie das Tray-Menü (Windows 11).</summary>
    public static void RoundCorners(IntPtr handle)
    {
        try
        {
            var round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(handle, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch (Exception) { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
