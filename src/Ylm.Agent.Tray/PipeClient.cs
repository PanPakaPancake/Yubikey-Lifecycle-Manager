using System.IO.Pipes;
using Ylm.Contracts;

namespace Ylm.Agent.Tray;

/// <summary>Connects to the agent service's named pipe and reconnects automatically.</summary>
internal sealed class PipeClient : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private StreamWriter? _writer;

    public event Action<TrayMessage>? Message;
    public event Action<bool>? ConnectedChanged;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", TrayPipe.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
                    // Identification: Der Dienst darf nachsehen, wer verbunden ist (Adminprüfung), aber nicht in unserem Namen handeln.
                    System.Security.Principal.TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5000, _cts.Token);
                using var reader = new StreamReader(pipe);
                _writer = new StreamWriter(pipe) { AutoFlush = true };
                ConnectedChanged?.Invoke(true);
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null) break;
                    if (TrayPipe.Deserialize(line) is { } msg) Message?.Invoke(msg);
                }
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
                // service not running or restarting
            }
            _writer = null;
            ConnectedChanged?.Invoke(false);
            try { await Task.Delay(3000, _cts.Token); } catch (OperationCanceledException) { }
        }
    }

    public async Task<bool> SendAsync(TrayMessage message)
    {
        var w = _writer;
        if (w is null) return false;
        try
        {
            await w.WriteLineAsync(TrayPipe.Serialize(message));
            return true;
        }
        catch (IOException) { return false; }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
