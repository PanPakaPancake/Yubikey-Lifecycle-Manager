using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Ylm.Agent.Core;
using Ylm.Contracts;

namespace Ylm.Agent;

/// <summary>
/// Named pipe between the service and the per-user tray app (planning 2.3). The service pushes status
/// updates; the tray sends pairing requests. One line of JSON per message.
/// </summary>
public sealed class TrayPipeServer(AgentConnection connection, AgentUpdater updater, ILogger<TrayPipeServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Pipe could not be created");
                await Task.Delay(2000, stoppingToken);
                continue;
            }
            _ = Task.Run(() => HandleClientAsync(pipe, stoppingToken), stoppingToken);
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(TrayPipe.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }
        return new NamedPipeServerStream(TrayPipe.PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var pipeScope = pipe;
        using var reader = new StreamReader(pipe);
        await using var writer = new StreamWriter(pipe) { AutoFlush = true };
        var sendLock = new SemaphoreSlim(1, 1);

        async Task SendAsync(TrayMessage m)
        {
            await sendLock.WaitAsync(ct);
            try { await writer.WriteLineAsync(TrayPipe.Serialize(m)); }
            catch (IOException) { /* client gone */ }
            finally { sendLock.Release(); }
        }

        void OnStatus(TrayStatus s) => _ = SendAsync(new TrayMessage { Type = TrayMessageType.Status, Status = s });
        void OnNotification(string title, string text, bool error) => _ = SendAsync(new TrayMessage { Type = TrayMessageType.Notification, Title = title, Text = text, IsError = error });
        void OnTrayExit() => _ = SendAsync(new TrayMessage { Type = TrayMessageType.UpdateInstalling, Text = updater.State.AvailableVersion });

        connection.StatusChanged += OnStatus;
        connection.Notification += OnNotification;
        connection.TrayExitRequested += OnTrayExit;
        try
        {
            await SendAsync(new TrayMessage { Type = TrayMessageType.Status, Status = connection.Status });
            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                var msg = TrayPipe.Deserialize(line);
                switch (msg?.Type)
                {
                    case TrayMessageType.GetStatus:
                        await SendAsync(new TrayMessage { Type = TrayMessageType.Status, Status = connection.Status });
                        break;
                    case TrayMessageType.PairRequest when msg.ServerUrl is not null && msg.PairingCode is not null:
                        // Nur Administratoren: Der Dienst läuft als SYSTEM und installiert Updates vom gekoppelten Server.
                        // Könnte jeder Benutzer koppeln, ließe sich der Agent an einen fremden Server hängen, der ein
                        // präpariertes MSI als "Update" ausliefert (Rechteausweitung auf SYSTEM).
                        var (ok, text) = IsAdministrator(pipe)
                            ? await Task.Run(() => connection.PairAsync(msg.ServerUrl, msg.PairingCode, ct), ct)
                            : (false, AdminOnlyMessage);
                        await SendAsync(new TrayMessage { Type = TrayMessageType.PairResult, Text = text, IsError = !ok });
                        break;
                    case TrayMessageType.Unpair:
                        if (IsAdministrator(pipe)) await connection.UnpairAsync();
                        else await SendAsync(new TrayMessage { Type = TrayMessageType.CommandResult, Text = AdminOnlyMessage, IsError = true });
                        break;
                    case TrayMessageType.SetAutoUpdate when msg.Enabled is { } enabled:
                        var (changed, message) = IsAdministrator(pipe)
                            ? await updater.SetAutoUpdateAsync(enabled)
                            : (false, "Automatische Updates ändern verlangt Administratorrechte auf diesem PC (Bestätigung per UAC).");
                        await SendAsync(new TrayMessage { Type = TrayMessageType.CommandResult, Text = message, IsError = !changed });
                        break;
                    case TrayMessageType.CheckForUpdate:
                        // Läuft im Hintergrund weiter, damit die Pipe frei bleibt (Status kommt über StatusChanged).
                        _ = Task.Run(async () =>
                        {
                            var result = await updater.CheckWithTimeoutAsync();
                            await SendAsync(new TrayMessage { Type = TrayMessageType.CommandResult, Text = result, IsError = updater.State.LastResultIsError });
                        }, ct);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // tray disconnected
        }
        finally
        {
            connection.StatusChanged -= OnStatus;
            connection.Notification -= OnNotification;
            connection.TrayExitRequested -= OnTrayExit;
        }
    }

    internal const string AdminOnlyMessage =
        "Koppeln und Entkoppeln verlangen Administratorrechte auf diesem PC (Bestätigung per UAC). Bitte die Tray-App aktualisieren oder die IT bitten.";

    /// <summary>
    /// Läuft der Prozess am anderen Ende der Pipe mit erhöhten Administratorrechten? Ein Administrator ohne UAC-Bestätigung
    /// zählt bewusst nicht: Sonst könnte Schadcode im Benutzerkontext den Agent still an einen fremden Server koppeln,
    /// der ein präpariertes MSI als Update ausliefert (UAC-Umgehung bis SYSTEM). Die Tray-App startet sich für solche Aktionen
    /// daher erhöht neu (siehe Elevation in Ylm.Agent.Tray).
    /// Bewusst ohne <see cref="NamedPipeServerStream.RunAsClient"/>: Die Tray-App verbindet sich nur mit Identification,
    /// und Code, der danach auf dem Dienst-Thread lief, sah noch den Identifikations-Token des Benutzers. Registry, Zertifikatsspeicher,
    /// DPAPI und Logdateien schlugen dann fehl (0x80071770 beim Speichern, TLS-Fehler, "Requested registry access is not allowed"
    /// beim Koppeln). Stattdessen liest der Dienst das Token des Tray-Prozesses selbst.
    /// </summary>
    private bool IsAdministrator(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process.IsInvalid) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            if (!OpenProcessToken(process, TokenQuery | TokenDuplicate, out var token))
                throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            using (token)
            using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                return IsAdministrator(identity);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Benutzer der Tray-App nicht ermittelbar: {Message}", ex.Message);
            return false;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsAdministrator(WindowsIdentity identity)
    {
        // Nur ein erhöhtes Token enthält die Administratoren-Gruppe als aktive Gruppe; ohne UAC steht sie dort als "deny only".
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
