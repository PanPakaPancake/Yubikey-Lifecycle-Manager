using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>Small status window: connection, detected key, current operation.</summary>
internal sealed class StatusForm : Form
{
    private readonly Label _connection = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10, FontStyle.Bold) };
    private readonly Label _instance = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _key = new() { AutoSize = false, Width = 380, Height = 70 };
    private readonly Label _operation = new() { AutoSize = false, Width = 380, Height = 60, ForeColor = Color.FromArgb(31, 95, 191) };
    private readonly ProgressBar _progress = new() { Width = 380, Height = 10, Visible = false };

    public StatusForm()
    {
        Text = "Yubikey Lifecycle Manager – Agent";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);
        ShowInTaskbar = false;
        TopMost = true;
        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(_connection);
        layout.Controls.Add(_instance);
        layout.Controls.Add(new Label { Text = "Erkannter Key", AutoSize = true, Margin = new Padding(0, 12, 0, 2), Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 9, FontStyle.Bold) });
        layout.Controls.Add(_key);
        layout.Controls.Add(_progress);
        layout.Controls.Add(_operation);
        Controls.Add(layout);
        Load += (_, _) =>
        {
            var wa = Screen.PrimaryScreen!.WorkingArea;
            Location = new Point(wa.Right - Width - 16, wa.Bottom - Height - 16);
        };
    }

    public void Show(TrayStatus s, bool pipeConnected)
    {
        _connection.Text = !pipeConnected ? "Agent-Dienst nicht erreichbar" : TrayContext.Describe(s.Connection);
        _connection.ForeColor = TrayContext.ColorFor(s.Connection, pipeConnected);
        _instance.Text = s.ServerUrl is null ? "Nicht gekoppelt" : $"{s.InstanceName} · {s.ServerUrl}{(s.PairedUser is null ? "" : $" · {s.PairedUser}")}";
        _key.Text = s.Key is null
            ? "Kein YubiKey eingesteckt."
            : $"{s.Key.Model}\nSeriennummer {s.Key.SerialNumber} · Firmware {s.Key.FirmwareVersion}\n" +
              (s.Key.SupportsForcePinChange ? "PIN-Wechsel erzwingbar: ja" : "PIN-Wechsel erzwingbar: nein (erst ab Firmware 5.7)") +
              (s.Key.PinIsSet ? " · PIN gesetzt" : "");
        if (s.Operation is { } op)
        {
            _operation.Text = op.Title + (op.Step is { } n && op.Total is { } t ? $" ({n}/{t})" : "") + (op.Prompt is null ? "" : "\n" + op.Prompt);
            _progress.Visible = op.Step is not null && op.Total is > 0;
            if (_progress.Visible) { _progress.Maximum = op.Total!.Value; _progress.Value = Math.Min(op.Step!.Value, op.Total.Value); }
        }
        else
        {
            _operation.Text = s.LastError is null ? "" : "Letzter Fehler: " + s.LastError;
            _progress.Visible = false;
        }
    }
}
