using System.Drawing.Drawing2D;

namespace Ylm.Agent.Tray;

/// <summary>
/// Das Logo der Weboberfläche (web/src/logo.tsx) mit GDI+ gezeichnet: ein Key in einer Umlaufbahn.
/// Koordinaten wie im SVG (48 × 48).
/// </summary>
internal static class LogoPainter
{
    private static readonly Color RingA = Palette.Hex("#a78bfa"), RingB = Palette.Hex("#22d3ee"), RingC = Palette.Hex("#34d399");

    /// <param name="orbit">Drehung der Umlaufbahn in Grad.</param>
    /// <param name="pulse">0..1, Leuchten der goldenen Fläche („Berühren“).</param>
    public static void Draw(Graphics g, RectangleF bounds, float orbit = 0, float pulse = .5f)
    {
        var state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(bounds.X, bounds.Y);
        g.ScaleTransform(bounds.Width / 48f, bounds.Height / 48f);

        using (var tile = Theme.Rounded(new RectangleF(1, 1, 46, 46), 13))
        using (var bg = new LinearGradientBrush(new PointF(1, 1), new PointF(47, 47), Palette.Hex("#151d36"), Palette.Hex("#070b16")))
            g.FillPath(bg, tile);
        using (var edge = Theme.Rounded(new RectangleF(1.5f, 1.5f, 45, 45), 12.5f))
        using (var edgeBrush = RingBrush(115))
        using (var edgePen = new Pen(edgeBrush, 1f))
            g.DrawPath(edgePen, edge);

        // Umlaufbahn: drei Viertel Bogen mit Pfeilspitze, dreht sich
        var orbitState = g.Save();
        g.TranslateTransform(24, 24);
        g.RotateTransform(orbit);
        g.TranslateTransform(-24, -24);
        using (var ring = RingBrush(255))
        using (var pen = new Pen(ring, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(pen, 8, 8, 32, 32, 0, 272);
        using (var arrow = new Pen(RingC, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            g.DrawLines(arrow, [new PointF(21, 4.6f), new PointF(24.6f, 8), new PointF(21, 11.4f)]);
        using (var spark = new SolidBrush(Palette.Hex("#e0f2fe")))
            g.FillEllipse(spark, 24 - 1.7f, 8 - 1.7f, 3.4f, 3.4f);
        g.Restore(orbitState);

        // Der Key
        using (var body = Theme.Rounded(new RectangleF(15.5f, 19, 15, 10), 3.2f))
        {
            using var fill = new SolidBrush(Palette.Hex("#1f2a44"));
            using var stroke = new Pen(Palette.Hex("#3b4a6b"), .8f);
            g.FillPath(fill, body);
            g.DrawPath(stroke, body);
        }
        using (var plug = Theme.Rounded(new RectangleF(30, 21.2f, 5, 5.6f), 1))
        using (var plugFill = new SolidBrush(Palette.Hex("#9aa4b4")))
            g.FillPath(plugFill, plug);
        using (var hole = new Pen(Palette.Hex("#7d8ba6"), .8f))
            g.DrawEllipse(hole, 18.6f - 1.1f, 24 - 1.1f, 2.2f, 2.2f);
        using (var glow = new Pen(Theme.Alpha(Palette.Hex("#fbbf24"), (int)(90 + 165 * pulse)), 1.2f))
            g.DrawEllipse(glow, 24.5f - 3.4f, 24 - 3.4f, 6.8f, 6.8f);
        using (var discPath = new GraphicsPath())
        {
            discPath.AddEllipse(24.5f - 2.7f, 24 - 2.7f, 5.4f, 5.4f);
            using var disc = new PathGradientBrush(discPath)
            {
                CenterPoint = new PointF(23.6f, 23.1f),
                CenterColor = Palette.Hex("#fde68a"),
                SurroundColors = [Palette.Hex("#c89211")],
            };
            g.FillPath(disc, discPath);
        }
        g.Restore(state);
    }

    private static LinearGradientBrush RingBrush(int alpha)
    {
        var brush = new LinearGradientBrush(new PointF(0, 0), new PointF(48, 48), RingA, RingC)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Theme.Alpha(RingA, alpha), Theme.Alpha(RingB, alpha), Theme.Alpha(RingC, alpha)],
                Positions = [0f, .55f, 1f],
            },
        };
        return brush;
    }
}

/// <summary>Animiertes Logo: Bogen dreht langsam, die goldene Fläche pulsiert. Steht still ohne Animationseffekte.</summary>
internal sealed class LogoMark : Control
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 40 };
    private float _orbit = -40, _phase;

    public LogoMark(int size = 40)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(size, size);
        Margin = new Padding(0);
        _timer.Tick += (_, _) => { _orbit = (_orbit + 1.6f) % 360; _phase += .08f; Invalidate(); };
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        _timer.Enabled = Visible && Theme.Animations;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var pulse = .5f + .5f * MathF.Sin(_phase);
        LogoPainter.Draw(e.Graphics, new RectangleF(0, 0, Width - 1, Height - 1), _orbit, pulse);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Wortmarke wie in der Seitenleiste: „YUBIKEY“ / „LC Manager“ / Unterzeile.</summary>
internal sealed class Wordmark : Control
{
    private readonly string? _sub;

    public Wordmark(string? sub)
    {
        _sub = sub;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Margin = new Padding(10, 0, 0, 0);
        Size = new Size(180, 52);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        var p = Theme.P;
        var y = 0f;
        TextRenderer.DrawText(g, Spaced("YUBIKEY"), Theme.Mono(6.5f, FontStyle.Bold), new Point(0, (int)y), p.Brand2, TextFormatFlags.NoPadding);
        y += 11;
        var lc = Theme.Display(12.5f);
        var lcSize = TextRenderer.MeasureText(g, "LC ", lc, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "LC ", lc, new Point(0, (int)y), p.Ink, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Manager", lc, new Point(lcSize.Width, (int)y), p.Brand2, TextFormatFlags.NoPadding);
        y += lcSize.Height;
        if (_sub is not null) TextRenderer.DrawText(g, _sub, Theme.Ui(8.5f), new Point(0, (int)y), p.Muted, TextFormatFlags.NoPadding);
    }

    /// <summary>Sperrung wie letter-spacing in der Weboberfläche.</summary>
    internal static string Spaced(string text) => string.Join(" ", text.ToCharArray());
}
