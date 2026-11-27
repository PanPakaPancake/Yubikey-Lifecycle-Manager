using System.Diagnostics;
using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>Tray app (planning 2.3): status, detected key, progress, notifications, menu.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly PipeClient _pipe = new();
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _statusItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _keyItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _openWeb = new("WebGUI öffnen");
    private readonly ToolStripMenuItem _pair = new("Koppeln …");
    private readonly ToolStripMenuItem _unpair = new("Kopplung lösen");
    private StatusForm _statusForm = new();
    private TrayStatus _status = new();
    private bool _pipeConnected;
    private PairForm? _pairForm;
    private SettingsForm? _settingsForm;
    private bool _announceUpdate;
    private bool _exiting;
    private TaskCompletionSource<(bool, string)>? _pairResult;
    private Icon? _currentIcon;

    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _versionItem = new() { Enabled = false, Tag = "muted" };
    private readonly ToolStripMenuItem _showStatus = new("Status anzeigen");
    private readonly ToolStripMenuItem _settings = new("Einstellungen …");
    private readonly ToolStripMenuItem _logs = new("Logs öffnen");
    private readonly ToolStripMenuItem _exit = new("Tray beenden");
    private readonly bool _preview;

    public TrayContext(bool justUpdated = false, bool preview = false)
    {
        _announceUpdate = justUpdated;
        _preview = preview;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _versionItem.Text = $"Version {typeof(TrayContext).Assembly.GetName().Version?.ToString(3)}";
        _statusItem.Tag = "info";
        _keyItem.Tag = "info";
        _menu.Items.AddRange([_statusItem, _keyItem, new ToolStripSeparator(), _showStatus, _openWeb, _pair, _unpair, _settings, _logs,
            new ToolStripSeparator(), _versionItem, _exit]);
        _showStatus.Click += (_, _) => ShowStatus();
        _settings.Click += (_, _) => ShowSettings();
        _logs.Click += (_, _) => OpenLogs();
        _exit.Click += (_, _) => ExitThread();
        _openWeb.Click += (_, _) => { if (_status.ServerUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); };
        _pair.Click += (_, _) => ShowPairing();
        _unpair.Click += async (_, _) =>
        {
            if (MessageBox.Show("Die Kopplung dieses PCs mit dem Server lösen?", "YubiKey LC Manager", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (Elevation.IsElevated()) await _pipe.SendAsync(new TrayMessage { Type = TrayMessageType.Unpair });
            else if (!Elevation.Run("unpair", out var error)) Warn(error);
        };
        StyleMenu();
        Theme.Changed += OnThemeChanged;

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = !preview, Text = "YubiKey LC Manager" };
        _icon.DoubleClick += (_, _) => ShowStatus();
        // Erst nach dem Anlegen von _icon abonnieren, sonst NullReferenceException beim Start (0.11.1).
        _icon.BalloonTipClicked += (_, _) => { if (_balloonOpensPairing) { _balloonOpensPairing = false; ShowPairing(); } };
        _icon.BalloonTipClosed += (_, _) => _balloonOpensPairing = false;
        _pipe.Message += m => _ui.Post(_ => OnMessage(m), null);
        _pipe.ConnectedChanged += c => _ui.Post(_ => { _pipeConnected = c; Render(); }, null);
        Render();
        if (!preview) _pipe.Start();
    }

    private void StyleMenu()
    {
        BentoMenuRenderer.Style(_menu);
        void Glyph(ToolStripItem item, Bitmap image) { var old = item.Image; item.Image = image; old?.Dispose(); }
        Glyph(_showStatus, MenuGlyphs.Status);
        Glyph(_openWeb, MenuGlyphs.Web);
        Glyph(_pair, MenuGlyphs.Pair);
        Glyph(_unpair, MenuGlyphs.Unpair);
        Glyph(_settings, MenuGlyphs.Settings);
        Glyph(_logs, MenuGlyphs.Logs);
        Glyph(_exit, MenuGlyphs.Exit);
    }

    /// <summary>Darstellung gewechselt: Menü neu einfärben, offene Fenster an derselben Stelle neu aufbauen.</summary>
    private void OnThemeChanged()
    {
        StyleMenu();
        if (_settingsForm is { IsDisposed: false } settings)
        {
            var location = settings.Location;
            settings.Close();
            ShowSettings();
            _settingsForm!.StartPosition = FormStartPosition.Manual;
            _settingsForm.Location = location;
        }
        var statusVisible = _statusForm.Visible;
        _statusForm.Dispose();
        _statusForm = new StatusForm();
        if (statusVisible) ShowStatus();
        if (_pairForm is { IsDisposed: false } pair) { pair.Close(); ShowPairing(); }
        Render();
    }

    /// <summary>Text, Farbe und „lebt“ für den Verbindungsstatus (Kapsel, Menü, Tray-Punkt).</summary>
    internal static (string Text, Color Color, bool Live) Describe(BackendConnectionState s, bool pipe)
    {
        var p = Theme.P;
        if (!pipe) return ("Dienst nicht erreichbar", p.Error, false);
        return s switch
        {
            BackendConnectionState.Connected => ("Verbunden", p.Ok, true),
            BackendConnectionState.Connecting => ("Verbinde …", p.Warn, true),
            BackendConnectionState.Disconnected => ("Getrennt", p.Error, false),
            BackendConnectionState.Unauthorized => ("Kopplung ungültig – neu koppeln", p.Error, false),
            _ => ("Nicht gekoppelt", p.Muted, false),
        };
    }

    /// <summary>Statuspunkt im Tray-Symbol: immer in den kräftigen Farben, die auf heller und dunkler Taskleiste wirken.</summary>
    private static Color IconColor(BackendConnectionState s, bool pipe) => !pipe ? Palette.Dark.Error : s switch
    {
        BackendConnectionState.Connected => Palette.Dark.Ok,
        BackendConnectionState.Connecting => Palette.Dark.Warn,
        BackendConnectionState.NotPaired => Palette.Hex("#94a3b8"),
        _ => Palette.Dark.Error,
    };

    private bool _balloonOpensPairing;

    private void OnMessage(TrayMessage m)
    {
        switch (m.Type)
        {
            case TrayMessageType.Status when m.Status is not null:
                var previousKey = _status.Key?.SerialNumber;
                var wasUnauthorized = _status.Connection == BackendConnectionState.Unauthorized;
                _status = m.Status;
                Render();
                // Der Server kennt das Token nicht mehr (Agent entkoppelt, Server neu aufgesetzt): Ohne neue Kopplung
                // gehen weder Verbindung noch Updates. Einmal darauf hinweisen, ein Klick öffnet das Koppeln-Fenster.
                if (!wasUnauthorized && m.Status.Connection == BackendConnectionState.Unauthorized)
                {
                    _balloonOpensPairing = true;
                    _icon.ShowBalloonTip(10000, "Agent neu koppeln",
                        "Der Server kennt diesen Agent nicht mehr. Ohne neue Kopplung gibt es keine Verbindung und keine Updates. Hier klicken zum Koppeln.",
                        ToolTipIcon.Warning);
                    break;
                }
                if (_announceUpdate)
                {
                    _announceUpdate = false;
                    _icon.ShowBalloonTip(5000, "Agent aktualisiert", $"Der Agent läuft jetzt mit Version {m.Status.AgentVersion}.", ToolTipIcon.Info);
                }
                // While the agent is connected the web UI shows every step, so the tray stays quiet (no balloons, no
                // window popping up). An already open status window is still updated by Render(). Without a server
                // connection only the key balloon remains.
                if (Quiet) break;
                if (m.Status.Key is { } key && key.SerialNumber != previousKey && m.Status.Operation is null)
                {
                    var compatible = key.SupportsForcePinChange ? "" : " Hinweis: PIN-Wechsel kann nicht erzwungen werden (Firmware < 5.7).";
                    _icon.ShowBalloonTip(3000, "YubiKey erkannt", $"{key.Model}, Seriennummer {key.SerialNumber}, Firmware {key.FirmwareVersion}.{compatible}", ToolTipIcon.None);
                }
                break;
            case TrayMessageType.Notification:
                if (Quiet) break;
                _icon.ShowBalloonTip(6000, m.Title ?? "YubiKey LC Manager", string.IsNullOrEmpty(m.Text) ? " " : m.Text, m.IsError ? ToolTipIcon.Error : ToolTipIcon.Info);
                break;
            case TrayMessageType.PairResult:
                _pairResult?.TrySetResult((!m.IsError, m.Text ?? ""));
                break;
            case TrayMessageType.CommandResult:
                if (_settingsForm is { IsDisposed: false, Visible: true }) _settingsForm.ShowResult(m.Text ?? "", m.IsError);
                else _icon.ShowBalloonTip(5000, "YubiKey LC Manager", string.IsNullOrEmpty(m.Text) ? " " : m.Text, m.IsError ? ToolTipIcon.Error : ToolTipIcon.Info);
                break;
            case TrayMessageType.UpdateInstalling:
                // Der Dienst installiert ein Update und startet die Tray-App danach neu. Sofort beenden, damit YlmTray.exe ersetzt werden kann.
                if (_exiting) break;
                _exiting = true;
                _icon.ShowBalloonTip(3000, "Agent wird aktualisiert",
                    $"Version {m.Text ?? "neu"} wird installiert. Das Tray-Symbol ist in etwa einer Minute wieder da.", ToolTipIcon.Info);
                var timer = new System.Windows.Forms.Timer { Interval = 1500 };
                timer.Tick += (_, _) => { timer.Dispose(); ExitThread(); };
                timer.Start();
                break;
        }
    }

    /// <summary>Connected to the server: the web UI is the place for hints, the tray only shows its icon.</summary>
    private bool Quiet => !TraySettings.Notifications || (_pipeConnected && _status.Connection == BackendConnectionState.Connected);

    private void Render()
    {
        var (connection, color, _) = Describe(_status.Connection, _pipeConnected);
        _statusItem.Text = connection + (_status.InstanceName is { } i ? $" · {i}" : "");
        var oldDot = _statusItem.Image;
        _statusItem.Image = MenuGlyphs.Dot(color);
        oldDot?.Dispose();
        _keyItem.Text = _status.Key is { } k ? $"{k.Model} · SN {k.SerialNumber} · FW {k.FirmwareVersion}" : "Kein Key eingesteckt";
        var oldKey = _keyItem.Image;
        _keyItem.Image = MenuGlyphs.Key(_status.Key is null ? Theme.P.Muted : Theme.P.Green);
        oldKey?.Dispose();
        _openWeb.Enabled = _status.ServerUrl is not null;
        _pair.Enabled = _pipeConnected;
        _unpair.Enabled = _pipeConnected && _status.Connection != BackendConnectionState.NotPaired;
        var tip = $"YubiKey LC Manager: {connection}" + (_status.Operation is { } op ? $"\n{op.Title}" : "");
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        var old = _currentIcon;
        _currentIcon = TrayIcons.Create(IconColor(_status.Connection, _pipeConnected));
        _icon.Icon = _currentIcon;
        old?.Dispose();
        if (_statusForm.Visible) _statusForm.Show(_status, _pipeConnected);
        if (_settingsForm is { IsDisposed: false }) _settingsForm.Render(_status, _pipeConnected);
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false }) { _settingsForm.Activate(); return; }
        _settingsForm = new SettingsForm();
        _settingsForm.AutoUpdateChanged += async enabled =>
        {
            if (!Elevation.IsElevated())
            {
                // Die erhöhte Instanz ändert die Einstellung, der neue Stand kommt über den Status zurück.
                if (!Elevation.Run("auto-update", out var error, enabled ? "on" : "off")) _settingsForm?.ShowResult(error ?? "", true);
                _settingsForm?.Render(_status, _pipeConnected);
                return;
            }
            if (!await _pipe.SendAsync(new TrayMessage { Type = TrayMessageType.SetAutoUpdate, Enabled = enabled }))
                _settingsForm?.ShowResult("Der Agent-Dienst ist nicht erreichbar.", true);
        };
        _settingsForm.CheckRequested += async () =>
        {
            if (!await _pipe.SendAsync(new TrayMessage { Type = TrayMessageType.CheckForUpdate }))
                _settingsForm?.ShowResult("Der Agent-Dienst ist nicht erreichbar.", true);
        };
        _settingsForm.PairRequested += ShowPairing;
        _settingsForm.LogsRequested += OpenLogs;
        _settingsForm.Render(_status, _pipeConnected);
        _settingsForm.Show();
    }

    private void ShowStatus()
    {
        if (_statusForm.IsDisposed) _statusForm = new StatusForm();
        _statusForm.Show(_status, _pipeConnected);
        if (!_statusForm.Visible) _statusForm.Show();
        _statusForm.Activate();
    }

    private void Warn(string? text) => _icon.ShowBalloonTip(5000, "YubiKey LC Manager", string.IsNullOrEmpty(text) ? " " : text, ToolTipIcon.Warning);

    private void ShowPairing()
    {
        if (!Elevation.IsElevated())
        {
            // Koppeln nur mit Administratorrechten: Das Fenster öffnet die erhöhte Instanz nach der UAC-Abfrage.
            if (!Elevation.Run("pair", out var error)) Warn(error);
            return;
        }
        if (_pairForm is { IsDisposed: false }) { _pairForm.Activate(); return; }
        _pairForm = new PairForm(_status.ServerUrl);
        _pairForm.PairRequested += async (url, code) =>
        {
            _pairResult = new TaskCompletionSource<(bool, string)>();
            if (!await _pipe.SendAsync(new TrayMessage { Type = TrayMessageType.PairRequest, ServerUrl = url, PairingCode = code }))
                return (false, "Der Agent-Dienst ist nicht erreichbar.");
            var done = await Task.WhenAny(_pairResult.Task, Task.Delay(TimeSpan.FromSeconds(45)));
            return done == _pairResult.Task ? _pairResult.Task.Result : (false, "Zeitüberschreitung bei der Kopplung.");
        };
        _pairForm.Show();
    }

    private static void OpenLogs()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YubikeyLifecycleManager", "Agent", "logs");
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        else MessageBox.Show("Es gibt noch keine Logdateien.", "YubiKey LC Manager");
    }

    // ---------- Vorschaubilder (YlmTray.exe --screenshots) ----------

    internal ContextMenuStrip Menu => _menu;

    internal void Preview(TrayStatus status, bool pipeConnected)
    {
        _status = status;
        _pipeConnected = pipeConnected;
        Render();
    }

    protected override void ExitThreadCore()
    {
        Theme.Changed -= OnThemeChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _pipe.Dispose();
        _currentIcon?.Dispose();
        base.ExitThreadCore();
    }
}
