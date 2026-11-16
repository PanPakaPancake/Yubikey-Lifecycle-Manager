using System.Drawing.Drawing2D;

namespace Ylm.Agent.Tray;

/// <summary>Draws the tray icon (a small key) with a status dot, so no binary assets are needed.</summary>
internal static class TrayIcons
{
    public static Icon Create(Color status)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var body = new SolidBrush(Color.FromArgb(43, 47, 54));
            using var metal = new SolidBrush(Color.FromArgb(184, 190, 199));
            using var gold = new SolidBrush(Color.FromArgb(212, 165, 42));
            g.FillRectangle(metal, 21, 12, 9, 8);
            using (var path = RoundedRect(new Rectangle(2, 8, 22, 16), 6)) g.FillPath(body, path);
            g.FillEllipse(gold, 6, 11, 10, 10);
            using var dot = new SolidBrush(status);
            using var ring = new Pen(Color.White, 2);
            g.FillEllipse(dot, 18, 18, 13, 13);
            g.DrawEllipse(ring, 18, 18, 13, 13);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
