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
                        var (ok, text) = await connection.PairAsync(msg.ServerUrl, msg.PairingCode, ct);
                        await SendAsync(new TrayMessage { Type = TrayMessageType.PairResult, Text = text, IsError = !ok });
                        break;
                    case TrayMessageType.Unpair:
                        await connection.UnpairAsync();
                        break;
                    case TrayMessageType.SetAutoUpdate when msg.Enabled is { } enabled:
                        var (changed, message) = IsAdministrator(pipe)
                            ? await updater.SetAutoUpdateAsync(enabled)
                            : (false, "Nur Administratoren dieses PCs können automatische Updates ändern.");
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

    /// <summary>
    /// Gehört der Benutzer am anderen Ende der Pipe zu den lokalen Administratoren? Zählt auch ohne erhöhte Rechte (UAC):
    /// Dann steht die Administratoren-Gruppe im Token als "deny only". Die Tray-App öffnet die Pipe dafür mit Identification.
    /// </summary>
    private bool IsAdministrator(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var admin = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                admin = IsAdministrator(identity);
            });
        }
        catch (Exception ex) { logger.LogWarning("Benutzer der Tray-App nicht ermittelbar: {Message}", ex.Message); }
        return admin;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool IsAdministrator(WindowsIdentity identity)
    {
        const string admins = "S-1-5-32-544";
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)
               || identity.Claims.Any(c => c.Type == System.Security.Claims.ClaimTypes.DenyOnlySid && c.Value == admins);
    }
}
