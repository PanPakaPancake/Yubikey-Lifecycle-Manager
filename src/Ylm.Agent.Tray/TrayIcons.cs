namespace Ylm.Agent.Tray;

/// <summary>Tray-Symbol: das Logo der Weboberfläche mit einem Statuspunkt unten rechts.</summary>
internal static class TrayIcons
{
    public static Icon Create(Color status, int size = 32)
    {
        using var bmp = Render(status, size);
        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Bitmap Render(Color status, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        LogoPainter.Draw(g, new RectangleF(0, 0, size - 1, size - 1), orbit: -40);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var d = size * .42f;
        var x = size - d - .5f;
        using var ring = new SolidBrush(Palette.Hex("#050912"));
        g.FillEllipse(ring, x - 1.5f, x - 1.5f, d + 3, d + 3);
        using var dot = new SolidBrush(status);
        g.FillEllipse(dot, x, x, d, d);
        return bmp;
    }
}
