using System.Drawing.Drawing2D;

namespace Ylm.Agent.Tray;

/// <summary>Tray-Menü im Design der Weboberfläche: Fläche mit Leuchtkante, Auswahl als runde Fläche mit Farbbalken.</summary>
internal sealed class BentoMenuRenderer : ToolStripProfessionalRenderer
{
    public BentoMenuRenderer() : base(new Colors()) { RoundedEdges = false; }

    public static void Style(ContextMenuStrip menu)
    {
        var p = Theme.P;
        menu.Renderer = new BentoMenuRenderer();
        menu.BackColor = p.Surface;
        menu.ForeColor = p.Ink;
        menu.Font = Theme.Ui(9.5f);
        menu.ShowImageMargin = true;
        menu.Padding = new Padding(6, 8, 6, 8);
        menu.DropShadowEnabled = true;
        menu.HandleCreated -= Round;
        menu.HandleCreated += Round;
        foreach (ToolStripItem item in menu.Items)
        {
            if (item is ToolStripSeparator) continue;
            item.Padding = new Padding(4, 6, 4, 6);
            item.ForeColor = p.Ink;
        }
    }

    private static void Round(object? sender, EventArgs e)
    {
        if (sender is Control c) Theme.RoundCorners(c.Handle);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var p = Theme.P;
        var g = e.Graphics;
        g.Clear(p.Surface);
        if (!p.IsDark) return;
        using var tint = new LinearGradientBrush(new Point(0, 0), new Point(e.AffectedBounds.Width, e.AffectedBounds.Height), Theme.Alpha(p.Accent, 22), Color.Transparent);
        g.FillRectangle(tint, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var p = Theme.P;
        var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var pen = new Pen(p.IsDark ? Theme.Alpha(p.Accent2, 70) : p.Line);
        e.Graphics.DrawRectangle(pen, r);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { /* keine graue Spalte */ }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var p = Theme.P;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        using var path = Theme.Rounded(r, 9);
        using (var fill = new SolidBrush(p.IsDark ? Theme.Alpha(p.Accent, 34) : p.Surface2)) g.FillPath(fill, path);
        using var bar = new LinearGradientBrush(new RectangleF(r.X + 4, r.Y + 6, 3, r.Height - 12), p.Accent, p.Accent2, 90f);
        using var barPath = Theme.Rounded(new RectangleF(r.X + 4, r.Y + 6, 3, r.Height - 12), 1.5f);
        g.FillPath(bar, barPath);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var p = Theme.P;
        e.TextColor = e.Item.Tag as string == "muted" ? p.Muted : e.Item.Enabled ? p.Ink : (e.Item.Tag as string == "info" ? p.Ink : p.Muted);
        if (e.Item.Tag as string == "muted") e.TextFont = Theme.Mono(8f);
        if (e.Item.Tag as string == "info") e.TextFont = Theme.Ui(9.5f, FontStyle.Bold);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var p = Theme.P;
        var y = e.Item.Height / 2;
        using var pen = new Pen(p.IsDark ? Theme.Mix(p.Line, p.Accent2, .1f) : p.Line);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.P.Muted;
        base.OnRenderArrow(e);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.P.Surface;
        public override Color ImageMarginGradientBegin => Theme.P.Surface;
        public override Color ImageMarginGradientMiddle => Theme.P.Surface;
        public override Color ImageMarginGradientEnd => Theme.P.Surface;
        public override Color MenuBorder => Theme.P.Line;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.P.Surface2;
        public override Color SeparatorDark => Theme.P.Line;
        public override Color SeparatorLight => Theme.P.Line;
    }
}

/// <summary>Kleine Symbole für die Menüzeilen: Punkt in Statusfarbe, Symbole in Bereichsfarben.</summary>
internal static class MenuGlyphs
{
    public static Bitmap Dot(Color color, int size = 16)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var halo = new SolidBrush(Theme.Alpha(color, 60))) g.FillEllipse(halo, 1, 1, size - 2, size - 2);
        using (var dot = new SolidBrush(color)) g.FillEllipse(dot, size * .3f, size * .3f, size * .4f, size * .4f);
        return bmp;
    }

    /// <summary>Farbkästchen mit Buchstaben-/Liniensymbol wie die Icons der Seitenleiste.</summary>
    public static Bitmap Tile(Color color, Action<Graphics, RectangleF, Pen> draw, int size = 18)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Theme.Rounded(new RectangleF(0, 0, size - 1, size - 1), 5))
        using (var fill = new SolidBrush(Theme.Alpha(color, Theme.P.IsDark ? 40 : 34)))
            g.FillPath(fill, path);
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        draw(g, new RectangleF(4, 4, size - 9, size - 9), pen);
        return bmp;
    }

    public static Bitmap Status => Tile(Theme.P.Violet, (g, r, pen) => { g.DrawRectangle(pen, r.X, r.Y + 1, r.Width, r.Height - 2); g.DrawLine(pen, r.X + 2, r.Y + r.Height / 2, r.Right - 2, r.Y + r.Height / 2); });
    public static Bitmap Web => Tile(Theme.P.Cyan, (g, r, pen) => { g.DrawEllipse(pen, r); g.DrawLine(pen, r.X, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2); g.DrawEllipse(pen, r.X + r.Width * .3f, r.Y, r.Width * .4f, r.Height); });
    public static Bitmap Pair => Tile(Theme.P.Green, (g, r, pen) => { g.DrawEllipse(pen, r.X, r.Y + r.Height * .3f, r.Width * .5f, r.Height * .4f); g.DrawEllipse(pen, r.X + r.Width * .5f, r.Y + r.Height * .3f, r.Width * .5f, r.Height * .4f); });
    public static Bitmap Unpair => Tile(Theme.P.Error, (g, r, pen) => { g.DrawLine(pen, r.X, r.Y, r.Right, r.Bottom); g.DrawLine(pen, r.Right, r.Y, r.X, r.Bottom); });
    public static Bitmap Settings => Tile(Theme.P.Amber, (g, r, pen) => { for (var i = 0; i < 3; i++) { var y = r.Y + r.Height * (i / 2f); g.DrawLine(pen, r.X, y, r.Right, y); } using var b = new SolidBrush(pen.Color); g.FillEllipse(b, r.X + r.Width * .55f, r.Y - 1.5f, 3, 3); g.FillEllipse(b, r.X + r.Width * .15f, r.Y + r.Height / 2 - 1.5f, 3, 3); g.FillEllipse(b, r.X + r.Width * .7f, r.Bottom - 1.5f, 3, 3); });
    public static Bitmap Logs => Tile(Theme.P.Muted, (g, r, pen) => { g.DrawRectangle(pen, r.X + 1, r.Y, r.Width - 2, r.Height); g.DrawLine(pen, r.X + 3, r.Y + 3, r.Right - 3, r.Y + 3); g.DrawLine(pen, r.X + 3, r.Y + 6, r.Right - 3, r.Y + 6); });
    public static Bitmap Exit => Tile(Theme.P.Muted, (g, r, pen) => { g.DrawArc(pen, r, -60, 300); g.DrawLine(pen, r.X + r.Width / 2, r.Y - 1, r.X + r.Width / 2, r.Y + r.Height / 2); });
    public static Bitmap Key(Color color) => Tile(color, (g, r, pen) => { g.DrawEllipse(pen, r.X, r.Y + r.Height * .25f, r.Width * .45f, r.Height * .5f); g.DrawLine(pen, r.X + r.Width * .45f, r.Y + r.Height / 2, r.Right, r.Y + r.Height / 2); g.DrawLine(pen, r.Right - 2, r.Y + r.Height / 2, r.Right - 2, r.Y + r.Height * .8f); });
}
