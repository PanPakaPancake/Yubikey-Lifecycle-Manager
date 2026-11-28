using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>
/// Koppeln, Entkoppeln und Auto-Update ändern verlangt der Dienst nur von Prozessen mit erhöhten Rechten.
/// Läuft die Tray-App ohne (der Normalfall, auch bei Administratoren), startet sie sich für genau diese Aktion
/// mit "Als Administrator ausführen" neu: Windows zeigt die UAC-Abfrage, die erhöhte Instanz erledigt die Aktion und beendet sich.
/// Die Tray-App selbst bleibt dauerhaft ohne erhöhte Rechte.
/// </summary>
internal static class Elevation
{
    public const string Arg = "--elevated";
    private const int Cancelled = 1223; // ERROR_CANCELLED: UAC-Abfrage abgelehnt

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Startet die Aktion erhöht. False, wenn die UAC-Abfrage abgelehnt wurde oder der Start scheiterte (Text in <paramref name="error"/>).</summary>
    public static bool Run(string action, out string? error, string? value = null)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = value is null ? $"{Arg} {action}" : $"{Arg} {action} {value}",
            };
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            error = "Abgebrochen: Für diese Aktion ist die Bestätigung als Administrator nötig.";
            return false;
        }
        catch (Exception ex)
        {
            error = "Konnte nicht mit Administratorrechten starten: " + ex.Message;
            return false;
        }
    }
}

/// <summary>Die erhöhte Instanz: verbindet sich mit dem Dienst, führt eine Aktion aus und beendet sich.</summary>
internal sealed class ElevatedActionContext : ApplicationContext
{
    private const string Title = "YubiKey LC Manager";
    private readonly PipeClient _pipe = new();
    private readonly string _action;
    private readonly string? _value;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TrayStatus> _status = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<TrayMessage>? _reply;

    public ElevatedActionContext(string action, string? value)
    {
        _action = action;
        _value = value;
        _pipe.ConnectedChanged += c => { if (c) _connected.TrySetResult(); };
        _pipe.Message += m =>
        {
            if (m.Type == TrayMessageType.Status && m.Status is { } s) _status.TrySetResult(s);
            else if (m.Type is TrayMessageType.PairResult or TrayMessageType.CommandResult) _reply?.TrySetResult(m);
        };
        // Erst in der Nachrichtenschleife starten, damit await auf den UI-Thread zurückkehrt.
        var start = new System.Windows.Forms.Timer { Interval = 1 };
        start.Tick += async (_, _) => { start.Dispose(); await RunAsync(); };
        start.Start();
    }

    private async Task RunAsync()
    {
        _pipe.Start();
        if (await Task.WhenAny(_connected.Task, Task.Delay(TimeSpan.FromSeconds(10))) != _connected.Task)
        {
            Fail("Der Agent-Dienst ist nicht erreichbar.");
            return;
        }
        await _pipe.SendAsync(new TrayMessage { Type = TrayMessageType.GetStatus });
        await Task.WhenAny(_status.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        var status = _status.Task.IsCompletedSuccessfully ? _status.Task.Result : null;

        switch (_action)
        {
            case "pair":
                var form = new PairForm(status?.ServerUrl);
                form.PairRequested += async (url, code) =>
                {
                    var reply = await AskAsync(new TrayMessage { Type = TrayMessageType.PairRequest, ServerUrl = url, PairingCode = code }, 45);
                    return reply is null ? (false, "Zeitüberschreitung bei der Kopplung.") : (!reply.IsError, reply.Text ?? "");
                };
                form.FormClosed += (_, _) => ExitThread();
                form.Show();
                form.Activate();
                return;
            case "unpair":
                // Der Dienst antwortet nur bei einem Fehler; Erfolg zeigt die Tray-App über den neuen Status.
                var unpair = await AskAsync(new TrayMessage { Type = TrayMessageType.Unpair }, 3);
                if (unpair is { IsError: true }) Fail(unpair.Text ?? "Die Kopplung konnte nicht gelöst werden.");
                else ExitThread();
                return;
            case "auto-update" when _value is "on" or "off":
                var changed = await AskAsync(new TrayMessage { Type = TrayMessageType.SetAutoUpdate, Enabled = _value == "on" }, 15);
                if (changed is null or { IsError: true }) Fail(changed?.Text ?? "Keine Antwort vom Agent-Dienst.");
                else ExitThread();
                return;
            default:
                ExitThread();
                return;
        }
    }

    private async Task<TrayMessage?> AskAsync(TrayMessage message, int timeoutSeconds)
    {
        var reply = _reply = new TaskCompletionSource<TrayMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!await _pipe.SendAsync(message)) return new TrayMessage { Type = TrayMessageType.CommandResult, IsError = true, Text = "Der Agent-Dienst ist nicht erreichbar." };
        return await Task.WhenAny(reply.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))) == reply.Task ? reply.Task.Result : null;
    }

    private void Fail(string text)
    {
        MessageBox.Show(text, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pipe.Dispose();
        base.Dispose(disposing);
    }
}
