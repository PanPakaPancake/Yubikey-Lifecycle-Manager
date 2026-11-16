using System.Text;
using Microsoft.Extensions.Logging;
using Yubico.YubiKey;
using Yubico.YubiKey.Fido2;
using Yubico.YubiKey.Fido2.Commands;
using Yubico.YubiKey.Fido2.Cose;
using Ylm.Contracts;
using Ylm.Core;

namespace Ylm.Agent.Core;

/// <summary>
/// Access to a real YubiKey via the Yubico .NET SDK. On Windows, FIDO2 over HID requires administrator
/// rights, which is why the agent runs as a Windows service (planning 2.3).
/// </summary>
public sealed class YubiKeyHardware : IKeyDevice, IDisposable
{
    private readonly ILogger<YubiKeyHardware> _logger;
    private readonly YubiKeyDeviceListener _listener;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TaskCompletionSource? _removed;
    private TaskCompletionSource<IYubiKeyDevice>? _arrived;
    private readonly Timer _poll;
    private string? _lastSnapshot;

    public event EventHandler? KeyChanged;

    public YubiKeyHardware(ILogger<YubiKeyHardware> logger)
    {
        _logger = logger;
        _listener = YubiKeyDeviceListener.Instance;
        _listener.Arrived += (_, e) =>
        {
            _logger.LogInformation("YubiKey arrived: {Serial}", e.Device.SerialNumber);
            _arrived?.TrySetResult(e.Device);
            KeyChanged?.Invoke(this, EventArgs.Empty);
        };
        _listener.Removed += (_, e) =>
        {
            _logger.LogInformation("YubiKey removed: {Serial}", e.Device.SerialNumber);
            _removed?.TrySetResult();
            KeyChanged?.Invoke(this, EventArgs.Empty);
        };
        // Fallback in case device notifications do not reach the service (observed: key inserted, nothing reported).
        _poll = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private void Poll()
    {
        if (!_lock.Wait(0)) return; // an operation is running
        try
        {
            var all = YubiKeyDevice.FindAll().ToList();
            var snapshot = Diagnose(all);
            if (snapshot == _lastSnapshot) return;
            _lastSnapshot = snapshot;
            _logger.LogInformation("YubiKey-Geräte gefunden: {Count} ({Details}); als Keys erkannt: {Keys}",
                all.Count, snapshot.Length == 0 ? "keine" : snapshot, Dedupe(all).Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Polling for YubiKeys failed");
            return;
        }
        finally { _lock.Release(); }
        KeyChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// FIDO-capable keys, one entry per physical key. The SDK can report one key several times (one entry per USB
    /// interface) when it cannot merge them, e.g. in a Windows service; such duplicates share the serial number.
    /// </summary>
    private static IReadOnlyList<IYubiKeyDevice> Devices() => Dedupe(YubiKeyDevice.FindAll());

    private static List<IYubiKeyDevice> Dedupe(IEnumerable<IYubiKeyDevice> all)
    {
        var fido = all.Where(d => d.AvailableUsbCapabilities.HasFlag(YubiKeyCapabilities.Fido2) || d.AvailableTransports.HasFlag(Transport.HidFido)).ToList();
        var withSerial = fido.Where(d => d.SerialNumber is not null)
            .GroupBy(d => d.SerialNumber)
            .Select(g => g.OrderByDescending(d => d.AvailableTransports.HasFlag(Transport.HidFido)).First())
            .ToList();
        // Keys without a readable serial (e.g. Security Key series) only count if no key with a serial is present.
        return withSerial.Count > 0 ? withSerial : fido;
    }

    private static string Diagnose(IEnumerable<IYubiKeyDevice> all) =>
        string.Join("; ", all.Select(d =>
            $"SN={d.SerialNumber?.ToString() ?? "-"} FW={d.FirmwareVersion.Major}.{d.FirmwareVersion.Minor}.{d.FirmwareVersion.Patch} " +
            $"Form={d.FormFactor} Transports={d.AvailableTransports} USB={d.AvailableUsbCapabilities} USB-aktiv={d.EnabledUsbCapabilities}"));

    private static List<IYubiKeyDevice> FidoHid() => YubiKeyDevice.FindByTransport(Transport.HidFido).ToList();

    private static IYubiKeyConnection ConnectWithRetry(IYubiKeyDevice device, TimeSpan within)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try { return device.Connect(YubiKeyApplication.Fido2); }
            catch (Exception) when (sw.Elapsed < within) { Thread.Sleep(100); }
        }
    }

    private static (IYubiKeyDevice? Device, OperationResult? Error) Single(int? expectedSerial)
    {
        var devices = Devices();
        if (devices.Count == 0) return (null, OperationResult.Fail(DeviceErrors.NoKey, "Kein YubiKey eingesteckt."));
        if (devices.Count > 1) return (null, OperationResult.Fail(DeviceErrors.MultipleKeys, "Es sind mehrere YubiKeys eingesteckt. Bitte nur einen Key verwenden."));
        var d = devices[0];
        if (expectedSerial is { } s && d.SerialNumber != s)
            return (null, OperationResult.Fail(DeviceErrors.WrongKey, $"Eingesteckt ist Key {d.SerialNumber}, erwartet wird {s}."));
        return (d, null);
    }

    public async Task<KeyInfo?> ReadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = YubiKeyDevice.FindAll().ToList();
            var devices = Dedupe(all);
            if (devices.Count == 1) return Describe(devices[0]);
            if (devices.Count > 1) _logger.LogWarning("Mehr als ein YubiKey erkannt: {Details}", Diagnose(all));
            return null;
        }
        finally { _lock.Release(); }
    }

    internal KeyInfo Describe(IYubiKeyDevice d)
    {
        var fw = $"{d.FirmwareVersion.Major}.{d.FirmwareVersion.Minor}.{d.FirmwareVersion.Patch}";
        var hasNfc = d.AvailableNfcCapabilities != YubiKeyCapabilities.None;
        var model = KeyModelNames.Describe(d.FormFactor.ToString(), hasNfc, d.IsSkySeries, d.IsFipsSeries, d.FirmwareVersion.Major);
        var info = new KeyInfo
        {
            SerialNumber = d.SerialNumber,
            FirmwareVersion = fw,
            FormFactor = d.FormFactor.ToString(),
            Model = model,
            IsFips = d.IsFipsSeries,
            HasFido2 = d.EnabledUsbCapabilities.HasFlag(YubiKeyCapabilities.Fido2),
        };
        if (!info.HasFido2) return info;
        try
        {
            using var fido = new Fido2Session(d);
            var a = fido.AuthenticatorInfo;
            return info with
            {
                AaGuid = new Guid(a.Aaguid.Span, bigEndian: true),
                PinIsSet = a.GetOptionValue(AuthenticatorOptions.clientPin) == OptionValue.True,
                SupportsForcePinChange = a.GetOptionValue(AuthenticatorOptions.setMinPINLength) == OptionValue.True,
                MinimumPinLength = a.MinimumPinLength,
                RemainingDiscoverableCredentials = a.RemainingDiscoverableCredentials,
                CtapVersions = a.Versions.ToList(),
            };
        }
        catch (Exception ex)
        {
            // Typical cause: the process lacks administrator rights for FIDO over HID on Windows.
            _logger.LogWarning(ex, "FIDO2 information could not be read");
            return info;
        }
    }

    public async Task<OperationResult> ResetAsync(int? expectedSerial, IOperationPrompts prompts, TimeSpan timeout, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var (device, error) = Single(expectedSerial);
            if (error is not null) return error;
            var serial = device!.SerialNumber;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            // Only the FIDO HID interface is watched: enumerating all interfaces (and the device listener) can take
            // several seconds on Windows, and the reset must reach the key within 10 seconds of power-up.
            prompts.Prompt("Key abziehen");
            while (FidoHid().Count > 0) await Task.Delay(150, cts.Token);
            prompts.Prompt("Key wieder einstecken");
            IYubiKeyDevice? again = null;
            while (again is null)
            {
                var found = FidoHid();
                if (found.Count > 1) return OperationResult.Fail(DeviceErrors.MultipleKeys, "Es sind mehrere YubiKeys eingesteckt. Bitte nur einen Key verwenden.");
                if (found.Count == 1) again = found[0];
                else await Task.Delay(100, cts.Token);
            }
            var sincePlugged = System.Diagnostics.Stopwatch.StartNew();
            if (serial is { } expected && again.SerialNumber is { } actual && actual != expected)
                return OperationResult.Fail(DeviceErrors.WrongKey, $"Eingesteckt ist Key {actual}, erwartet wird {expected}.");
            _logger.LogInformation("Key wieder eingesteckt, sende Werksreset");
            prompts.Prompt("Key jetzt berühren");
            return await Task.Run(() =>
            {
                using var connection = ConnectWithRetry(again!, TimeSpan.FromSeconds(2));
                var response = connection.SendCommand(new ResetCommand());
                _logger.LogInformation("Werksreset: {Status} nach {Ms} ms seit dem Einstecken", response.Status, sincePlugged.ElapsedMilliseconds);
                return response.Status == Yubico.YubiKey.ResponseStatus.Success
                    ? OperationResult.Ok()
                    : OperationResult.Fail(DeviceErrors.Failed, $"Reset abgelehnt ({response.StatusMessage}). Der Reset muss innerhalb von 10 Sekunden nach dem Anstecken bestätigt werden.");
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail(DeviceErrors.Timeout, "Zeitüberschreitung beim Werksreset.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reset failed");
            return OperationResult.Fail(DeviceErrors.Failed, ex.Message);
        }
        finally
        {
            _removed = null;
            _arrived = null;
            _lock.Release();
        }
    }

    private static Func<KeyEntryData, bool> Collector(string pin, IOperationPrompts? prompts) => data =>
    {
        switch (data.Request)
        {
            case KeyEntryRequest.VerifyFido2Pin:
                if (data.IsRetry) return false; // never loop on a wrong PIN
                data.SubmitValue(Encoding.UTF8.GetBytes(pin));
                return true;
            case KeyEntryRequest.SetFido2Pin:
                data.SubmitValue(Encoding.UTF8.GetBytes(pin));
                return true;
            case KeyEntryRequest.TouchRequest:
                prompts?.Prompt("Bitte den Key berühren.");
                return true;
            case KeyEntryRequest.Release:
                return true;
            default:
                return false;
        }
    };

    public async Task<OperationResult> SetPinAsync(int? expectedSerial, string pin, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var (device, error) = Single(expectedSerial);
            if (error is not null) return error;
            return await Task.Run(() =>
            {
                using var fido = new Fido2Session(device!);
                if (fido.AuthenticatorInfo.GetOptionValue(AuthenticatorOptions.clientPin) == OptionValue.True)
                    return OperationResult.Fail(DeviceErrors.PinAlreadySet, "Auf dem Key ist bereits eine PIN gesetzt.");
                return fido.TrySetPin(Encoding.UTF8.GetBytes(pin))
                    ? OperationResult.Ok()
                    : OperationResult.Fail(DeviceErrors.PinRejected, "Der Key hat die PIN abgelehnt (z. B. zu kurz oder zu einfach).");
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SetPin failed");
            return OperationResult.Fail(DeviceErrors.Failed, ex.Message);
        }
        finally { _lock.Release(); }
    }

    public async Task<MakeCredentialResult> MakeCredentialAsync(int? expectedSerial, string pin, WebAuthnCreationOptions options, string origin, IOperationPrompts prompts, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var (device, error) = Single(expectedSerial);
            if (error is not null) return new MakeCredentialResult { Success = false, Error = error.Error, ErrorCode = error.ErrorCode };
            return await Task.Run(() =>
            {
                using var fido = new Fido2Session(device!);
                fido.KeyCollector = Collector(pin, prompts);
                var info = fido.AuthenticatorInfo;
                var clientData = WebAuthnEncoding.ClientDataJson(options.Challenge, origin);
                var rp = new RelyingParty(options.RpId) { Name = options.RpName };
                var user = new UserEntity(options.User.Id) { Name = options.User.Name, DisplayName = options.User.DisplayName };
                var p = new MakeCredentialParameters(rp, user) { ClientDataHash = WebAuthnEncoding.Hash(clientData) };
                foreach (var alg in options.Algorithms)
                {
                    if (Enum.IsDefined(typeof(CoseAlgorithmIdentifier), alg)) p.AddAlgorithm("public-key", (CoseAlgorithmIdentifier)alg);
                }
                foreach (var ex in options.ExcludeCredentials) p.ExcludeCredential(new CredentialId { Id = ex.Id, Type = ex.Type });
                if (options.ResidentKey) p.AddOption(AuthenticatorOptions.rk, true);
                if (options.HmacSecret && info.IsExtensionSupported("hmac-secret")) p.AddHmacSecretExtension(info);
                if (options.CredProtect is { } level && info.IsExtensionSupported("credProtect"))
                    p.AddCredProtectExtension((CredProtectPolicy)level, options.EnforceCredProtect, info);

                fido.VerifyPin(PinUvAuthTokenPermissions.MakeCredential, options.RpId);
                prompts.Prompt("Bitte den Key berühren, sobald er blinkt.");
                var data = fido.MakeCredential(p);
                var authData = data.AuthenticatorData.EncodedAuthenticatorData;
                var attestation = WebAuthnEncoding.AttestationObject(data.Format, data.EncodedAttestationStatement, authData);
                var credId = data.AuthenticatorData.CredentialId?.Id ?? throw new InvalidOperationException("Key returned no credential id");
                return new MakeCredentialResult
                {
                    Success = true,
                    CredentialId = Base64Url.Encode(credId.Span),
                    ClientDataJson = Base64Url.Encode(clientData),
                    AttestationObject = Base64Url.Encode(attestation),
                    AaGuid = data.AuthenticatorData.Aaguid is { } g ? new Guid(g.Span, bigEndian: true) : null,
                };
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MakeCredential failed");
            return new MakeCredentialResult { Success = false, Error = ex.Message, ErrorCode = DeviceErrors.Failed };
        }
        finally { _lock.Release(); }
    }

    public async Task<OperationResult> ForcePinChangeAsync(int? expectedSerial, string pin, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var (device, error) = Single(expectedSerial);
            if (error is not null) return error;
            return await Task.Run(() =>
            {
                using var fido = new Fido2Session(device!);
                if (fido.AuthenticatorInfo.GetOptionValue(AuthenticatorOptions.setMinPINLength) != OptionValue.True)
                    return OperationResult.Fail(DeviceErrors.NotSupported, "Der Key unterstützt kein erzwungenes Ändern der PIN (ab Firmware 5.7).");
                fido.KeyCollector = Collector(pin, null);
                fido.VerifyPin(PinUvAuthTokenPermissions.AuthenticatorConfiguration);
                return fido.TrySetPinConfig(null, null, true)
                    ? OperationResult.Ok()
                    : OperationResult.Fail(DeviceErrors.Failed, "Der Key hat die Einstellung abgelehnt.");
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ForcePinChange failed");
            return OperationResult.Fail(DeviceErrors.Failed, ex.Message);
        }
        finally { _lock.Release(); }
    }

    public void Dispose()
    {
        _poll.Dispose();
        _lock.Dispose();
    }
}
