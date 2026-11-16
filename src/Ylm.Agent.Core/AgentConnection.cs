using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ylm.Contracts;

namespace Ylm.Agent.Core;

/// <summary>
/// Outgoing connection from the agent to the backend (planning 2.2). The backend invokes key operations
/// on the agent via SignalR client results; the agent never listens on a local port.
/// </summary>
public sealed class AgentConnection : IAsyncDisposable, IOperationPrompts
{
    private readonly IKeyDevice _device;
    private readonly AgentSettingsStore _store;
    private readonly ILogger _logger;
    private readonly string _version;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? _configureHttp;
    private HubConnection? _hub;
    private AgentSettings _settings;
    private TrayStatus _status;

    public event Action<TrayStatus>? StatusChanged;
    public event Action<string, string, bool>? Notification;

    public AgentConnection(IKeyDevice device, AgentSettingsStore store, ILogger logger, string version, Func<HttpMessageHandler>? handlerFactory = null,
        Action<Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions>? configureHttp = null)
    {
        _configureHttp = configureHttp;
        _device = device;
        _store = store;
        _logger = logger;
        _version = version;
        _handlerFactory = handlerFactory;
        _settings = store.Load();
        _status = new TrayStatus
        {
            Connection = _settings.ProtectedToken is null ? BackendConnectionState.NotPaired : BackendConnectionState.Disconnected,
            ServerUrl = _settings.ServerUrl, PairedUser = _settings.PairedUser, InstanceName = _settings.InstanceName, AgentVersion = version,
        };
        _device.KeyChanged += async (_, _) => await OnKeyChangedAsync();
    }

    public TrayStatus Status => _status;

    private void Update(Func<TrayStatus, TrayStatus> change)
    {
        _status = change(_status);
        StatusChanged?.Invoke(_status);
    }

    public void Prompt(string text) => Update(s => s with { Operation = (s.Operation ?? new AgentProgress("Vorgang")) with { Prompt = text } });

    private async Task OnKeyChangedAsync()
    {
        try
        {
            await Task.Delay(300);
            var key = await _device.ReadAsync();
            Update(s => s with { Key = key });
            if (_hub?.State == HubConnectionState.Connected) await _hub.InvokeAsync(AgentProtocol.ReportKeyStateMethod, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Key state report failed");
        }
    }

    public async Task StartAsync(CancellationToken ct)
    {
        var key = await _device.ReadAsync(ct);
        Update(s => s with { Key = key });
        if (_settings.ProtectedToken is null || _settings.ServerUrl is null) return;
        await ConnectAsync(ct);
    }

    private HubConnection BuildHub(string serverUrl, string token)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(serverUrl.TrimEnd('/') + AgentProtocol.HubPath, o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                if (_handlerFactory is not null) o.HttpMessageHandlerFactory = _ => _handlerFactory();
                _configureHttp?.Invoke(o);
            })
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        hub.ServerTimeout = TimeSpan.FromSeconds(60);

        hub.On(nameof(IAgentClient.GetKeyInfo), async () => await _device.ReadAsync());
        hub.On<ResetRequest, OperationResult>(nameof(IAgentClient.ResetFido), async req =>
            await RunAsync("Werksreset", () => _device.ResetAsync(req.ExpectedSerialNumber, this, TimeSpan.FromSeconds(Math.Clamp(req.TimeoutSeconds, 20, 300)))));
        hub.On<SetPinRequest, OperationResult>(nameof(IAgentClient.SetPin), async req =>
            await RunAsync("PIN setzen", () => _device.SetPinAsync(req.ExpectedSerialNumber, req.NewPin)));
        hub.On<MakeCredentialRequest, MakeCredentialResult>(nameof(IAgentClient.MakeCredential), async req =>
        {
            WebAuthnCreationOptions options;
            Base64Url.TextFallbacks = [];
            try
            {
                options = WebAuthnCreationOptions.Parse(req.PublicKeyOptionsJson);
                if (Base64Url.TextFallbacks.Count > 0)
                    _logger.LogWarning("creationOptions: Felder nicht base64url, als Text verwendet: {Fields}", string.Join(", ", Base64Url.TextFallbacks));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "creationOptions could not be parsed. Structure: {Structure}", DescribeJson(req.PublicKeyOptionsJson));
                return new MakeCredentialResult { Success = false, Error = "Ungültige creationOptions: " + ex.Message, ErrorCode = "invalid_options" };
            }
            finally { Base64Url.TextFallbacks = null; }
            return await RunAsync("Passkey erzeugen", () => _device.MakeCredentialAsync(req.ExpectedSerialNumber, req.Pin, options, req.Origin, this));
        });
        hub.On<ForcePinChangeRequest, OperationResult>(nameof(IAgentClient.ForcePinChange), async req =>
            await RunAsync("PIN-Wechsel erzwingen", () => _device.ForcePinChangeAsync(req.ExpectedSerialNumber, req.Pin)));
        hub.On<AgentProgress>(nameof(IAgentClient.ShowProgress), p =>
        {
            _logger.LogInformation("Server: {Title}{Prompt}", p.Title, p.Prompt is null ? "" : " – " + p.Prompt);
            Update(s => s with { Operation = p.Done || p.Failed ? null : p });
            if (p.Done || p.Failed) Notification?.Invoke(p.Title, p.Prompt ?? "", p.Failed);
        });

        hub.Reconnecting += ex => { _logger.LogWarning("Verbindung unterbrochen, verbinde neu: {Error}", ex?.Message); Update(s => s with { Connection = BackendConnectionState.Connecting }); return Task.CompletedTask; };
        hub.Reconnected += async _ => { _logger.LogInformation("Wieder verbunden"); Update(s => s with { Connection = BackendConnectionState.Connected, LastError = null }); await HelloAsync(hub); };
        hub.Closed += ex =>
        {
            _logger.LogWarning("Verbindung geschlossen: {Error}", ex?.Message);
            Update(s => s with { Connection = IsUnauthorized(ex) ? BackendConnectionState.Unauthorized : BackendConnectionState.Disconnected, LastError = ex?.Message });
            return Task.CompletedTask;
        };
        return hub;
    }

    /// <summary>Field names, types and lengths of a JSON document (no values) for diagnostics.</summary>
    internal static string DescribeJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var parts = new List<string>();
            void Walk(System.Text.Json.JsonElement e, string path)
            {
                switch (e.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Object:
                        foreach (var p in e.EnumerateObject()) Walk(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        var i = 0;
                        foreach (var x in e.EnumerateArray()) Walk(x, $"{path}[{i++}]");
                        break;
                    case System.Text.Json.JsonValueKind.String:
                        var v = e.GetString() ?? "";
                        parts.Add($"{path}=string({v.Length}{(v.Length > 0 && !Base64Url.TryDecode(v, out _) ? ", kein base64url" : "")})");
                        break;
                    default:
                        parts.Add($"{path}={e.ValueKind}");
                        break;
                }
            }
            Walk(doc.RootElement, "");
            return string.Join("; ", parts);
        }
        catch (Exception ex) { return "kein JSON: " + ex.Message; }
    }

    private async Task<T> RunAsync<T>(string title, Func<Task<T>> op)
    {
        Update(s => s with { Operation = new AgentProgress(title) });
        _logger.LogInformation("Vorgang gestartet: {Title}", title);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var result = await op();
            var (ok, error) = result switch
            {
                OperationResult r => (r.Success, r.Error),
                MakeCredentialResult m => (m.Success, m.Error),
                _ => (true, null),
            };
            if (ok) _logger.LogInformation("Vorgang erfolgreich: {Title} ({Ms} ms)", title, (int)(DateTimeOffset.UtcNow - started).TotalMilliseconds);
            else _logger.LogWarning("Vorgang fehlgeschlagen: {Title}: {Error}", title, error);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Vorgang abgebrochen: {Title}", title);
            throw;
        }
        finally { Update(s => s with { Operation = s.Operation?.Title == title ? null : s.Operation }); }
    }

    private static bool IsUnauthorized(Exception? ex) => ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized };

    private async Task HelloAsync(HubConnection hub)
    {
        await hub.InvokeAsync(AgentProtocol.ReportHelloMethod, new AgentHello(Environment.MachineName, _version, AgentProtocol.ProtocolVersion, Environment.UserName));
        await hub.InvokeAsync(AgentProtocol.ReportKeyStateMethod, await _device.ReadAsync());
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_hub is not null) { await _hub.DisposeAsync(); _hub = null; }
        var token = _store.Token(_settings);
        if (token is null || _settings.ServerUrl is null) return;
        _hub = BuildHub(_settings.ServerUrl, token);
        Update(s => s with { Connection = BackendConnectionState.Connecting, ServerUrl = _settings.ServerUrl });
        // Initial connect with retry; afterwards automatic reconnect takes over.
        for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try
            {
                await _hub.StartAsync(ct);
                await HelloAsync(_hub);
                Update(s => s with { Connection = BackendConnectionState.Connected, LastError = null });
                _logger.LogInformation("Mit Server verbunden: {Url}", _settings.ServerUrl);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Verbindung zum Server fehlgeschlagen (Versuch {Attempt}): {Error}", attempt + 1, ex.Message);
                if (IsUnauthorized(ex))
                {
                    Update(s => s with { Connection = BackendConnectionState.Unauthorized, LastError = "Agent wurde abgemeldet oder widerrufen. Bitte neu koppeln." });
                    return;
                }
                Update(s => s with { Connection = BackendConnectionState.Disconnected, LastError = ex.Message });
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(attempt, 5))), ct);
            }
        }
    }

    /// <summary>Pairs the agent with a server using the code shown in the WebGUI.</summary>
    public async Task<(bool Ok, string Message)> PairAsync(string serverUrl, string code, CancellationToken ct)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
            return (false, "Bitte die Server-Adresse mit https:// angeben.");
        try
        {
            using var http = _handlerFactory is null ? new HttpClient() : new HttpClient(_handlerFactory());
            http.Timeout = TimeSpan.FromSeconds(30);
            var res = await http.PostAsJsonAsync(new Uri(uri, "/api/agent/pair"), new PairingRequest(code, Environment.MachineName, _version), ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadFromJsonAsync<ErrorBody>(ct);
                return (false, err?.Message ?? $"Kopplung fehlgeschlagen ({(int)res.StatusCode}).");
            }
            var pairing = (await res.Content.ReadFromJsonAsync<PairingResponse>(ct))!;
            _settings = _store.WithToken(new AgentSettings
            {
                ServerUrl = uri.GetLeftPart(UriPartial.Authority), AgentId = pairing.AgentId, PairedUser = pairing.UserDisplayName, InstanceName = pairing.InstanceName,
            }, pairing.AgentToken);
            _store.Save(_settings);
            Update(s => s with { ServerUrl = _settings.ServerUrl, PairedUser = pairing.UserDisplayName, InstanceName = pairing.InstanceName });
            _logger.LogInformation("Gekoppelt mit {Url} ({Instance}) als {User}", serverUrl, pairing.InstanceName, pairing.UserDisplayName);
            _ = ConnectAsync(CancellationToken.None);
            return (true, $"Gekoppelt mit {pairing.InstanceName} als {pairing.UserDisplayName}.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kopplung fehlgeschlagen");
            return (false, "Server nicht erreichbar: " + ex.Message);
        }
    }

    public async Task UnpairAsync()
    {
        if (_hub is not null) { await _hub.DisposeAsync(); _hub = null; }
        _settings = new AgentSettings();
        _store.Save(_settings);
        Update(s => s with { Connection = BackendConnectionState.NotPaired, ServerUrl = null, PairedUser = null, InstanceName = null, LastError = null });
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
    }

    private sealed record ErrorBody(string? Code, string? Message);

    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext ctx) => TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(ctx.PreviousRetryCount, 6))));
    }
}
