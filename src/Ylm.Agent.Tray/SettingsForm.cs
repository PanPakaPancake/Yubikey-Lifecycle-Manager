using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>
/// Einstellungen des Agents: Autostart, Hinweise und Darstellung pro Benutzer, automatische Updates für den ganzen PC
/// (nur Administratoren, ggf. vom Server vorgegeben), Updateprüfung und Verbindung.
/// </summary>
internal sealed class SettingsForm : BentoForm
{
    // Zwei Spalten, damit das Fenster auch auf kleinen Bildschirmen (768 px hoch) ganz sichtbar ist.
    private const int Col = 350, Gap = 12, W = Col * 2 + Gap, CW = Col - 36;
    private readonly Toggle _autoStart = new("Mit Windows starten", "Tray-Symbol bei der Anmeldung starten.", CW);
    private readonly Toggle _notifications = new("Hinweise anzeigen", "Sprechblasen, solange der Agent nicht mit dem Server verbunden ist.", CW);
    private readonly Segmented _theme = new(["Dunkel", "Hell", "Wie Windows"], CW);
    private readonly Toggle _autoUpdate = new("Updates automatisch installieren", "Gilt für diesen PC.", CW);
    private readonly Label _updateHint = Txt.Muted("", CW);
    private readonly Label _versions = Txt.Value("", CW);
    private readonly Label _lastResult = Txt.Body("", CW);
    private readonly BentoButton _check = new("Jetzt nach Updates suchen", ButtonKind.Primary);
    private readonly Pill _connectionPill = new("Verbinde", Theme.P.Warn);
    private readonly Label _connection = Txt.Value("", CW);
    private readonly Label _instance = Txt.Muted("", CW);
    private bool _rendering;
    private bool _checking;

    public event Func<bool, Task>? AutoUpdateChanged;
    public event Func<Task>? CheckRequested;
    public event Action? PairRequested;
    public event Action? LogsRequested;

    public SettingsForm()
    {
        Text = "Einstellungen – YubiKey LC Manager Agent";
        StartPosition = FormStartPosition.CenterScreen;
        var p = Theme.P;

        var stack = new Stack(W);
        stack.Controls.Add(new Header("Agent · Einstellungen", width: W));
        var columns = new Row();
        var left = new Stack(Col) { Margin = new Padding(0, 0, Gap, 0) };
        var right = new Stack(Col);
        columns.Controls.Add(left);
        columns.Controls.Add(right);

        var general = new Card(p.Violet);
        var g = new Stack(CW);
        g.Controls.Add(new Eyebrow("Allgemein", p.Violet, dot: true));
        g.Controls.Add(_autoStart);
        g.Controls.Add(_notifications);
        g.Controls.Add(new Eyebrow("Darstellung") { Margin = new Padding(0, 6, 0, 6) });
        g.Controls.Add(_theme);
        g.Controls.Add(Txt.Muted("Der Agent-Dienst selbst läuft immer im Hintergrund, damit die Weboberfläche den YubiKey ansprechen kann.", CW));
        general.Controls.Add(g);
        left.Controls.Add(general);

        var updates = new Card(p.Cyan);
        var u = new Stack(CW);
        u.Controls.Add(new Eyebrow("Updates", p.Cyan, dot: true));
        u.Controls.Add(_autoUpdate);
        u.Controls.Add(_updateHint);
        u.Controls.Add(_versions);
        u.Controls.Add(_lastResult);
        u.Controls.Add(_check);
        updates.Controls.Add(u);
        right.Controls.Add(updates);
        var connection = new Card(p.Green);
        var c = new Stack(CW);
        c.Controls.Add(new Eyebrow("Verbindung", p.Green, dot: true));
        c.Controls.Add(_connectionPill);
        c.Controls.Add(_connection);
        c.Controls.Add(_instance);
        var buttons = new Row();
        var pair = new BentoButton("Koppeln …");
        var logs = new BentoButton("Logs öffnen");
        buttons.Controls.Add(pair);
        buttons.Controls.Add(logs);
        c.Controls.Add(buttons);
        connection.Controls.Add(c);
        right.Controls.Add(connection);

        stack.Controls.Add(columns);

        var bottom = new Row(rightToLeft: true, width: W);
        var close = new BentoButton("Schließen") { Margin = new Padding(0, 2, 0, 0) };
        // Das Fenster ist nicht modal (Show statt ShowDialog), DialogResult allein schließt es daher nicht.
        close.Click += (_, _) => Close();
        bottom.Controls.Add(close);
        stack.Controls.Add(bottom);
        Controls.Add(stack);
        CancelButton = close;

        _autoStart.SetSilently(TraySettings.AutoStart);
        _notifications.SetSilently(TraySettings.Notifications);
        _theme.Selected = (int)Theme.Mode;
        _autoStart.CheckedChanged += (_, _) => Save(() => TraySettings.AutoStart = _autoStart.Checked);
        _notifications.CheckedChanged += (_, _) => Save(() => TraySettings.Notifications = _notifications.Checked);
        _theme.SelectedChanged += (_, _) => Save(() => Theme.Set((ThemeMode)_theme.Selected));
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
            _lastResult.ForeColor = Theme.P.Muted;
            _lastResult.Text = "Suche nach Updates …";
            await CheckRequested();
        };
        pair.Click += (_, _) => PairRequested?.Invoke();
        logs.Click += (_, _) => LogsRequested?.Invoke();
    }

    private static void Save(Action write)
    {
        try { write(); }
        catch (Exception ex) { MessageBox.Show("Einstellung nicht gespeichert: " + ex.Message, "YubiKey LC Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>Antwort des Dienstes auf eine Änderung oder "Jetzt nach Updates suchen".</summary>
    public void ShowResult(string text, bool error)
    {
        _lastResult.ForeColor = error ? Theme.P.Error : Theme.P.Ok;
        _lastResult.Text = text;
        _checking = false;
        _check.Enabled = true;
    }

    public void Render(TrayStatus s, bool pipeConnected)
    {
        _rendering = true;
        try
        {
            var p = Theme.P;
            var u = s.Update;
            _autoUpdate.SetSilently(u?.AutoUpdate ?? false);
            _autoUpdate.Enabled = pipeConnected && u is not null && u.Policy == AgentUpdatePolicy.AgentDecides;
            _updateHint.Text = !pipeConnected ? "Der Agent-Dienst ist nicht erreichbar."
                : u is null ? "Dieser Agent-Dienst unterstützt die Einstellung noch nicht."
                : u.Policy == AgentUpdatePolicy.Always ? "Vom Server vorgegeben: Updates werden immer automatisch installiert."
                : u.Policy == AgentUpdatePolicy.Never ? "Vom Server vorgegeben: keine automatischen Updates. „Jetzt nach Updates suchen“ installiert trotzdem."
                : "Der Agent fragt alle 4 Stunden beim Server nach einer neuen Version. Ändern verlangt die Bestätigung als Administrator (UAC).";
            var available = u?.AvailableVersion is { } v ? $"  ·  Server {v}" : "";
            var checkedAt = u?.LastCheckAt is { } at ? $"\nGeprüft {at.LocalDateTime:dd.MM.yyyy, HH:mm}" : "";
            _versions.Text = $"Installiert {(string.IsNullOrEmpty(s.AgentVersion) ? "?" : s.AgentVersion)}{available}{checkedAt}";
            if (u?.LastResult is { } result && (!_checking || u.Installing))
            {
                _lastResult.ForeColor = u.LastResultIsError ? p.Error : p.Muted;
                _lastResult.Text = result;
            }
            _check.Enabled = pipeConnected && u is not null && s.Connection != BackendConnectionState.NotPaired && !u.Installing && !_checking;

            var (text, color, live) = TrayContext.Describe(s.Connection, pipeConnected);
            _connectionPill.Set(text, color, live);
            _connection.Text = s.ServerUrl ?? "Nicht gekoppelt";
            _instance.Text = s.InstanceName is null ? "" : s.InstanceName + (s.PairedUser is null ? "" : " · " + s.PairedUser);
        }
        finally { _rendering = false; }
    }
}
