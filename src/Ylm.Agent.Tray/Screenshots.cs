using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>
/// Erzeugt Vorschaubilder aller Fenster mit Beispieldaten, dunkel und hell: <c>YlmTray.exe --screenshots &lt;Ordner&gt;</c>.
/// Spricht nicht mit dem Dienst und speichert keine Einstellungen.
/// </summary>
internal static class Screenshots
{
    public static void Run(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
        {
            Theme.Preview(mode);
            var suffix = mode == ThemeMode.Dark ? "dunkel" : "hell";
            var status = Sample();

            using (var settings = new SettingsForm())
            {
                settings.StartPosition = FormStartPosition.Manual;
                settings.Location = new Point(40, 40);
                settings.Render(status, true);
                Capture(settings, Path.Combine(directory, $"einstellungen-{suffix}.png"));
            }
            using (var form = new StatusForm())
            {
                form.Show(status with
                {
                    Operation = new AgentProgress("Passkey erzeugen", "Bitte jetzt den YubiKey berühren", 3, 4),
                }, true);
                Capture(form, Path.Combine(directory, $"status-{suffix}.png"), place: false);
            }
            using (var pair = new PairForm("https://ylm.example.com"))
            {
                pair.StartPosition = FormStartPosition.Manual;
                pair.Location = new Point(40, 40);
                pair.PreviewValues("https://ylm.example.com", "K7QM-4XPA", "Gekoppelt mit Muster IT als Timo.", ok: true);
                Capture(pair, Path.Combine(directory, $"koppeln-{suffix}.png"));
            }
            var context = new TrayContext(preview: true);
            context.Preview(status, true);
            CaptureMenu(context.Menu, Path.Combine(directory, $"menue-{suffix}.png"));
            context.Menu.Dispose();
        }

        // Tray-Symbole in den vier Zuständen, vergrößert
        var states = new[] { ("verbunden", Palette.Dark.Ok), ("verbinde", Palette.Dark.Warn), ("getrennt", Palette.Dark.Error), ("nicht-gekoppelt", Palette.Hex("#94a3b8")) };
        using var sheet = new Bitmap(states.Length * 112, 112);
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Palette.Hex("#1f2937"));
            for (var i = 0; i < states.Length; i++)
            {
                using var big = TrayIcons.Render(states[i].Item2, 64);
                using var small = TrayIcons.Render(states[i].Item2, 16);
                g.DrawImage(big, i * 112 + 8, 8, 64, 64);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(small, i * 112 + 80, 24, 16, 16);
                g.DrawImage(small, i * 112 + 8, 80, 32, 32);
            }
        }
        sheet.Save(Path.Combine(directory, "tray-symbole.png"), ImageFormat.Png);
    }

    private static TrayStatus Sample() => new()
    {
        Connection = BackendConnectionState.Connected,
        ServerUrl = "https://ylm.example.com",
        InstanceName = "Muster IT",
        PairedUser = "Timo",
        AgentVersion = "0.9.0",
        Key = new KeyInfo
        {
            Model = "YubiKey 5 NFC", SerialNumber = 22449382, FirmwareVersion = "5.7.1", SupportsForcePinChange = true, PinIsSet = true,
        },
        Update = new AgentUpdateState
        {
            AutoUpdate = true, LocalAutoUpdate = true, Policy = AgentUpdatePolicy.AgentDecides,
            LastCheckAt = DateTimeOffset.Now.AddMinutes(-12), LastResult = "Aktuell (Version 0.9.0).",
        },
    };

    private static void Capture(Form form, string file, bool place = true)
    {
        form.Show();
        Pump(700);
        Save(form.Handle, form, file);
        form.Hide();
    }

    private static void CaptureMenu(ContextMenuStrip menu, string file)
    {
        menu.Show(new Point(60, 60));
        Pump(500);
        Save(menu.Handle, menu, file);
        menu.Close();
    }

    private static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(15); }
    }

    /// <summary>Bildschirmfoto des Fensters (mit Titelleiste); ohne sichtbaren Desktop ersatzweise DrawToBitmap.</summary>
    private static void Save(IntPtr handle, Control control, string file)
    {
        Rectangle bounds;
        if (DwmGetWindowAttribute(handle, 9, out var rect, Marshal.SizeOf<Rect>()) == 0) bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        else bounds = control.Bounds;
        using var shot = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height));
        var ok = false;
        try
        {
            using var g = Graphics.FromImage(shot);
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            ok = !IsBlank(shot);
        }
        catch (Exception) { }
        if (ok) { shot.Save(file, ImageFormat.Png); return; }
        using var fallback = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(fallback, new Rectangle(Point.Empty, control.Size));
        fallback.Save(file, ImageFormat.Png);
    }

    private static bool IsBlank(Bitmap bmp)
    {
        var first = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        for (var i = 1; i < 20; i++)
            if (bmp.GetPixel(bmp.Width * i / 21, bmp.Height * (i % 7 + 1) / 8) != first) return false;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
}
