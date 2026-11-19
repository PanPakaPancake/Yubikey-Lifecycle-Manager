using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>
/// Einstellungen des Agents: Autostart und Hinweise pro Benutzer, automatische Updates für den ganzen PC
/// (nur Administratoren, ggf. vom Server vorgegeben), Updateprüfung und Verbindung.
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly CheckBox _autoStart = new() { Text = "Mit Windows starten (Tray-Symbol bei der Anmeldung)", AutoSize = true };
    private readonly CheckBox _notifications = new() { Text = "Hinweise anzeigen, solange der Agent nicht mit dem Server verbunden ist", AutoSize = true };
    private readonly CheckBox _autoUpdate = new() { Text = "Updates automatisch installieren (gilt für diesen PC)", AutoSize = true };
    private readonly Label _updateHint = new() { AutoSize = false, Width = 420, Height = 34, ForeColor = Color.DimGray };
    private readonly Label _versions = new() { AutoSize = false, Width = 420, Height = 36 };
    private readonly Label _lastResult = new() { AutoSize = false, Width = 420, Height = 48 };
    private readonly Button _check = new() { Text = "Jetzt nach Updates suchen", AutoSize = true };
    private readonly Label _connection = new() { AutoSize = false, Width = 420, Height = 36 };
    private readonly bool _isAdmin = TraySettings.IsAdministrator();
    private bool _rendering;
    private bool _checking;

    public event Func<bool, Task>? AutoUpdateChanged;
    public event Func<Task>? CheckRequested;
    public event Action? PairRequested;
    public event Action? LogsRequested;

    public SettingsForm()
    {
        Text = "Einstellungen – Yubikey Lifecycle Manager Agent";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var layout = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Dock = DockStyle.Fill };

        layout.Controls.Add(Heading("Allgemein"));
        layout.Controls.Add(_autoStart);
        layout.Controls.Add(_notifications);
        layout.Controls.Add(new Label
        {
            Text = "Der Agent-Dienst selbst läuft immer im Hintergrund, damit die Weboberfläche den YubiKey ansprechen kann.",
            AutoSize = false, Width = 420, Height = 34, ForeColor = Color.DimGray,
        });

        layout.Controls.Add(Heading("Updates"));
        layout.Controls.Add(_autoUpdate);
        layout.Controls.Add(_updateHint);
        layout.Controls.Add(_versions);
        layout.Controls.Add(_lastResult);
        layout.Controls.Add(_check);

        layout.Controls.Add(Heading("Verbindung"));
        layout.Controls.Add(_connection);
        var connectionButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        var pair = new Button { Text = "Koppeln …", AutoSize = true };
        var logs = new Button { Text = "Logs öffnen", AutoSize = true };
        connectionButtons.Controls.Add(pair);
        connectionButtons.Controls.Add(logs);
        layout.Controls.Add(connectionButtons);

        // Das Fenster ist nicht modal (Show statt ShowDialog), DialogResult allein schließt es daher nicht.
        var close = new Button { Text = "Schließen", AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 14, 0, 0) };
        close.Click += (_, _) => Close();
        layout.Controls.Add(close);
        Controls.Add(layout);
        CancelButton = close;

        _autoStart.Checked = TraySettings.AutoStart;
        _notifications.Checked = TraySettings.Notifications;
        _autoStart.CheckedChanged += (_, _) => Save(() => TraySettings.AutoStart = _autoStart.Checked);
        _notifications.CheckedChanged += (_, _) => Save(() => TraySettings.Notifications = _notifications.Checked);
        _autoUpdate.CheckedChanged += async (_, _) =>
        {
            if (_rendering || AutoUpdateChanged is null) return;
            _autoUpdate.Enabled = false;
            await AutoUpdateChanged(_autoUpdate.Checked);
        };
        _check.Click += async (_, _) =>
        {
            if (CheckRequested is null) return;
            _checking = true;
            _check.Enabled = false;
            _lastResult.ForeColor = Color.DimGray;
            _lastResult.Text = "Suche nach Updates …";
            await CheckRequested();
        };
        pair.Click += (_, _) => PairRequested?.Invoke();
        logs.Click += (_, _) => LogsRequested?.Invoke();
    }

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(0, 12, 0, 4),
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10, FontStyle.Bold),
    };

    private static void Save(Action write)
    {
        try { write(); }
        catch (Exception ex) { MessageBox.Show("Einstellung nicht gespeichert: " + ex.Message, "Yubikey Lifecycle Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>Antwort des Dienstes auf eine Änderung oder "Jetzt nach Updates suchen".</summary>
    public void ShowResult(string text, bool error)
    {
        _lastResult.ForeColor = error ? Color.Firebrick : Color.SeaGreen;
        _lastResult.Text = text;
        _checking = false;
        _check.Enabled = true;
    }

    public void Render(TrayStatus s, bool pipeConnected)
    {
        _rendering = true;
        try
        {
            var u = s.Update;
            _autoUpdate.Checked = u?.AutoUpdate ?? false;
            _autoUpdate.Enabled = pipeConnected && u is not null && _isAdmin && u.Policy == AgentUpdatePolicy.AgentDecides;
            _updateHint.Text = !pipeConnected ? "Der Agent-Dienst ist nicht erreichbar."
                : u is null ? "Dieser Agent-Dienst unterstützt die Einstellung noch nicht."
                : u.Policy == AgentUpdatePolicy.Always ? "Vom Server vorgegeben: Updates werden immer automatisch installiert."
                : u.Policy == AgentUpdatePolicy.Never ? "Vom Server vorgegeben: keine automatischen Updates. „Jetzt nach Updates suchen“ installiert trotzdem."
                : !_isAdmin ? "Nur Administratoren dieses PCs können das ändern."
                : "Der Agent fragt alle 4 Stunden beim Server nach einer neuen Version.";
            var available = u?.AvailableVersion is { } v ? $" · auf dem Server: {v}" : "";
            var checkedAt = u?.LastCheckAt is { } at ? $"\nZuletzt geprüft: {at.LocalDateTime:dd.MM.yyyy HH:mm}" : "";
            _versions.Text = $"Installiert: {(string.IsNullOrEmpty(s.AgentVersion) ? "?" : s.AgentVersion)}{available}{checkedAt}";
            if (u?.LastResult is { } result && (!_checking || u.Installing))
            {
                _lastResult.ForeColor = u.LastResultIsError ? Color.Firebrick : Color.DimGray;
                _lastResult.Text = result;
            }
            _check.Enabled = pipeConnected && u is not null && s.Connection != BackendConnectionState.NotPaired && !u.Installing && !_checking;

            _connection.Text = s.ServerUrl is null
                ? "Nicht gekoppelt."
                : $"{TrayContext.Describe(s.Connection)} mit {s.ServerUrl}" + (s.InstanceName is null ? "" : $"\n{s.InstanceName}{(s.PairedUser is null ? "" : " · " + s.PairedUser)}");
        }
        finally { _rendering = false; }
    }
}
