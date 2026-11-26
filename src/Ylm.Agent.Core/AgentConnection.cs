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
    /// <summary>Ein Update wird gleich installiert: die Tray-Apps sollen sich beenden, damit ihre Datei ersetzt werden kann.</summary>
    public event Action? TrayExitRequested;

    /// <summary>Liefert den Update-Stand für die Anmeldung am Server (Hello).</summary>
    public Func<(bool? AutoUpdate, string? Error)>? UpdateReport { get; set; }
    /// <summary>Wird aufgerufen, wenn der Server "Jetzt aktualisieren" auslöst.</summary>
    public Func<CancellationToken, Task<string>>? UpdateRequested { get; set; }

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

    public void SetUpdateState(AgentUpdateState state) => Update(s => s with { Update = state });

    public void RequestTrayExit() => TrayExitRequested?.Invoke();

    /// <summary>Meldet den aktuellen Stand (z. B. geänderte Update-Einstellung) erneut an den Server.</summary>
    public async Task ResendHelloAsync()
    {
        try
        {
            if (_hub is { State: HubConnectionState.Connected } hub) await HelloAsync(hub);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Hello failed"); }
    }

    public void Prompt(string text)
    {
        _logger.LogInformation("Hinweis: {Prompt}", text);
        Update(s => s with { Operation = (s.Operation ?? new AgentProgress("Vorgang")) with { Prompt = text } });
        if (_hub?.State == HubConnectionState.Connected) _ = _hub.InvokeAsync(AgentProtocol.ReportPromptMethod, text);
    }

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
        hub.On(nameof(IAgentClient.CheckForUpdate), async () =>
            UpdateRequested is { } check ? await check(CancellationToken.None) : "Auto-Update ist auf diesem Agent nicht verfügbar.");
        hub.On<string>(nameof(IAgentClient.ServerAddress), url =>
        {
            _ = Task.Run(() => MoveToAsync(url));
            return Task.CompletedTask;
        });
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
        var update = UpdateReport?.Invoke();
        await hub.InvokeAsync(AgentProtocol.ReportHelloMethod,
            new AgentHello(Environment.MachineName, _version, AgentProtocol.ProtocolVersion, Environment.UserName, update?.AutoUpdate, update?.Error));
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

    private int _moving;

    /// <summary>
    /// Der Server hat eine neue Adresse (Adresse und Zertifikat in der Oberfläche geändert). Der Agent wechselt erst,
    /// wenn die neue Adresse erreichbar ist (HTTPS mit gültigem Zertifikat); sonst bleibt er bei der alten.
    /// </summary>
    public async Task MoveToAsync(string publicUrl)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var target) || (target.Scheme != "https" && !target.IsLoopback)) return;
        var next = target.GetLeftPart(UriPartial.Authority);
        if (_settings.ServerUrl is null || string.Equals(next, _settings.ServerUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return;
        if (Interlocked.Exchange(ref _moving, 1) == 1) return;
        try
        {
            using var http = _handlerFactory is null ? new HttpClient() : new HttpClient(_handlerFactory());
            http.Timeout = TimeSpan.FromSeconds(15);
            using var res = await http.GetAsync(new Uri(new Uri(next), "/healthz"));
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Neue Server-Adresse {Url} antwortet mit {Status}, bleibe bei {Old}", next, (int)res.StatusCode, _settings.ServerUrl);
                return;
            }
            var old = _settings.ServerUrl;
            _settings = _store.Change(s => s with { ServerUrl = next });
            _logger.LogInformation("Server ist umgezogen: {Old} -> {New}", old, next);
            Notification?.Invoke("Server-Adresse geändert", $"Der Agent verbindet sich jetzt mit {next}.", false);
            await ConnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Neue Server-Adresse {Url} nicht erreichbar, bleibe bei {Old}: {Error}", next, _settings.ServerUrl, ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _moving, 0);
        }
    }

    /// <summary>Pairs the agent with a server using the code shown in the WebGUI.</summary>
    public async Task<(bool Ok, string Message)> PairAsync(string serverUrl, string code, CancellationToken ct)
    {
        if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
            return (false, "Bitte die Server-Adresse mit https:// angeben.");
        PairingResponse pairing;
        try
        {
            using var http = _handlerFactory is null ? new HttpClient() : new HttpClient(_handlerFactory());
            http.Timeout = TimeSpan.FromSeconds(30);
            var res = await http.PostAsJsonAsync(new Uri(uri, "/api/agent/pair"), new PairingRequest(code, Environment.MachineName, _version), ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await ReadErrorAsync(res, ct);
                return (false, err ?? $"Kopplung fehlgeschlagen ({(int)res.StatusCode}).");
            }
            pairing = (await res.Content.ReadFromJsonAsync<PairingResponse>(ct))!;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kopplung fehlgeschlagen (Anfrage an {Url})", serverUrl);
            return (false, (ConnectionHint(ex, uri) ?? "Server nicht erreichbar.") + " " + Describe(ex));
        }

        // Ab hier hat der Server den Code verbraucht. Scheitert das Speichern auf diesem PC, soll das auch so dastehen
        // (vorher hieß es „Server nicht erreichbar“, obwohl der Server die Kopplung angenommen hatte).
        try
        {
            _settings = _store.Change(old => _store.WithToken(old with
            {
                ServerUrl = uri.GetLeftPart(UriPartial.Authority), AgentId = pairing.AgentId, PairedUser = pairing.UserDisplayName, InstanceName = pairing.InstanceName,
            }, pairing.AgentToken));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kopplung vom Server angenommen, Speichern auf diesem PC fehlgeschlagen ({File})", _store.FilePath);
            return (false, "Der Server hat die Kopplung angenommen, sie ließ sich auf diesem PC aber nicht speichern: " + Describe(ex)
                           + ". Bitte in der Weboberfläche den neuen Eintrag entkoppeln und mit einem neuen Code erneut koppeln.");
        }
        Update(s => s with { ServerUrl = _settings.ServerUrl, PairedUser = pairing.UserDisplayName, InstanceName = pairing.InstanceName });
        _logger.LogInformation("Gekoppelt mit {Url} ({Instance}) als {User}", serverUrl, pairing.InstanceName, pairing.UserDisplayName);
        _ = ConnectAsync(CancellationToken.None);
        return (true, $"Gekoppelt mit {pairing.InstanceName} als {pairing.UserDisplayName}.");
    }

    /// <summary>Fehlermeldung des Servers; null, wenn die Antwort kein JSON ist (z. B. Proxy-Seite).</summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try { return (await res.Content.ReadFromJsonAsync<ErrorBody>(ct))?.Message; }
        catch (Exception) { return null; }
    }

    /// <summary>Meldung mit Ausnahmetyp und Code, damit auch „Unknown error (0x…)“ zuzuordnen ist.</summary>
    /// <summary>
    /// Verständlicher Hinweis für die häufigen Fehler beim Koppeln, z. B. https:// auf dem HTTP-Port 8080
    /// (vorher nur „Cannot determine the frame size or a corrupted frame was received“).
    /// </summary>
    internal static string? ConnectionHint(Exception ex, Uri uri)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException auth)
            {
                var certificate = auth.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                                  || auth.Message.Contains("Zertifikat", StringComparison.OrdinalIgnoreCase);
                if (certificate)
                    return "Das HTTPS-Zertifikat des Servers ist auf diesem PC nicht vertrauenswürdig. Bei der internen Zertifizierungsstelle " +
                           "das Stammzertifikat (ylm-root-ca.crt) als vertrauenswürdige Stammzertifizierungsstelle installieren.";
                return uri.Port == 8080
                    ? "Unter dieser Adresse antwortet kein HTTPS: Port 8080 ist der interne HTTP-Port. Bitte die Adresse aus dem Browser verwenden, " +
                      "meist ohne Port (z. B. https://localhost)."
                    : "Unter dieser Adresse antwortet kein HTTPS. Bitte die Adresse aus dem Browser verwenden.";
            }
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused })
                return $"Unter {uri.GetLeftPart(UriPartial.Authority)} läuft kein Server (Verbindung abgelehnt). Bitte die Adresse aus dem Browser verwenden.";
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData })
                return $"Der Servername {uri.Host} ist unbekannt. Bitte die Adresse prüfen.";
        }
        return null;
    }

    internal static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException is not null && inner is HttpRequestException or AggregateException) inner = inner.InnerException;
        return $"({inner.Message} [{inner.GetType().Name}, 0x{inner.HResult:X8}])";
    }

    public async Task UnpairAsync()
    {
        if (_hub is not null) { await _hub.DisposeAsync(); _hub = null; }
        // Die lokale Update-Einstellung gehört zum PC, nicht zur Kopplung.
        _settings = _store.Change(old => new AgentSettings { AutoUpdate = old.AutoUpdate });
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
