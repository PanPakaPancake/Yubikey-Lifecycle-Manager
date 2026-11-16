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
    private TaskCompletionSource<(bool, string)>? _pairResult;
    private Icon? _currentIcon;

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var version = typeof(TrayContext).Assembly.GetName().Version?.ToString(3);
        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(_keyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Status anzeigen", null, (_, _) => ShowStatus());
        menu.Items.Add(_openWeb);
        menu.Items.Add(_pair);
        menu.Items.Add(_unpair);
        menu.Items.Add("Logs öffnen", null, (_, _) => OpenLogs());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Version {version}") { Enabled = false });
        menu.Items.Add("Tray beenden", null, (_, _) => ExitThread());

        _openWeb.Click += (_, _) => { if (_status.ServerUrl is { } url) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); };
        _pair.Click += (_, _) => ShowPairing();
        _unpair.Click += async (_, _) =>
        {
            if (MessageBox.Show("Die Kopplung dieses PCs mit dem Server lösen?", "Yubikey Lifecycle Manager", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                await _pipe.SendAsync(new TrayMessage { Type = TrayMessageType.Unpair });
        };

        _icon = new NotifyIcon { ContextMenuStrip = menu, Visible = true, Text = "Yubikey Lifecycle Manager" };
        _icon.DoubleClick += (_, _) => ShowStatus();
        _pipe.Message += m => _ui.Post(_ => OnMessage(m), null);
        _pipe.ConnectedChanged += c => _ui.Post(_ => { _pipeConnected = c; Render(); }, null);
        Render();
        _pipe.Start();
    }

    internal static string Describe(BackendConnectionState s) => s switch
    {
        BackendConnectionState.Connected => "Verbunden",
        BackendConnectionState.Connecting => "Verbinde …",
        BackendConnectionState.Disconnected => "Getrennt",
        BackendConnectionState.Unauthorized => "Nicht angemeldet",
        _ => "Nicht gekoppelt",
    };

    internal static Color ColorFor(BackendConnectionState s, bool pipe) => !pipe ? Color.Firebrick : s switch
    {
        BackendConnectionState.Connected => Color.SeaGreen,
        BackendConnectionState.Connecting => Color.Goldenrod,
        BackendConnectionState.NotPaired => Color.Gray,
        _ => Color.Firebrick,
    };

    private void OnMessage(TrayMessage m)
    {
        switch (m.Type)
        {
            case TrayMessageType.Status when m.Status is not null:
                var previousKey = _status.Key?.SerialNumber;
                _status = m.Status;
                Render();
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
                _icon.ShowBalloonTip(6000, m.Title ?? "Yubikey Lifecycle Manager", string.IsNullOrEmpty(m.Text) ? " " : m.Text, m.IsError ? ToolTipIcon.Error : ToolTipIcon.Info);
                break;
            case TrayMessageType.PairResult:
                _pairResult?.TrySetResult((!m.IsError, m.Text ?? ""));
                break;
        }
    }

    /// <summary>Connected to the server: the web UI is the place for hints, the tray only shows its icon.</summary>
    private bool Quiet => _pipeConnected && _status.Connection == BackendConnectionState.Connected;

    private void Render()
    {
        var connection = _pipeConnected ? Describe(_status.Connection) : "Agent-Dienst nicht erreichbar";
        _statusItem.Text = connection + (_status.InstanceName is { } i ? $" – {i}" : "");
        _keyItem.Text = _status.Key is { } k ? $"Key: {k.Model} · {k.SerialNumber} · FW {k.FirmwareVersion}" : "Kein Key eingesteckt";
        _openWeb.Enabled = _status.ServerUrl is not null;
        _pair.Enabled = _pipeConnected;
        _unpair.Enabled = _pipeConnected && _status.Connection != BackendConnectionState.NotPaired;
        var tip = $"YLM Agent: {connection}" + (_status.Operation is { } op ? $"\n{op.Title}" : "");
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        var old = _currentIcon;
        _currentIcon = TrayIcons.Create(ColorFor(_status.Connection, _pipeConnected));
        _icon.Icon = _currentIcon;
        old?.Dispose();
        if (_statusForm.Visible) _statusForm.Show(_status, _pipeConnected);
    }

    private void ShowStatus()
    {
        if (_statusForm.IsDisposed) _statusForm = new StatusForm();
        _statusForm.Show(_status, _pipeConnected);
        if (!_statusForm.Visible) _statusForm.Show();
        _statusForm.Activate();
    }

    private void ShowPairing()
    {
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
        else MessageBox.Show("Es gibt noch keine Logdateien.", "Yubikey Lifecycle Manager");
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _pipe.Dispose();
        _currentIcon?.Dispose();
        base.ExitThreadCore();
    }
}
