using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using Ylm.Contracts;

namespace Ylm.Agent.Core;

/// <summary>
/// Software stand-in for a YubiKey 5 NFC (FW 5.7) for development and automated tests without hardware.
/// Produces structurally valid WebAuthn data with "none" attestation. Never use against a real tenant.
/// </summary>
public sealed class SimulatedKeyDevice : IKeyDevice
{
    public static readonly Guid SimulatedAaguid = Guid.Parse("a25342c0-3cdc-4414-8e46-f4807fca511c");

    private string? _pin;
    private bool _forceChange;
    private readonly List<(byte[] RpIdHash, byte[] CredId)> _credentials = [];

    public int SerialNumber { get; set; } = 31_337_001;
    public string Firmware { get; set; } = "5.7.1";
    public bool Inserted { get; private set; } = true;
    public bool ForcePinChangeActive => _forceChange;
    public string? CurrentPin => _pin;
    public int CredentialCount => _credentials.Count;

    public event EventHandler? KeyChanged;

    public void Insert() { Inserted = true; KeyChanged?.Invoke(this, EventArgs.Empty); }
    public void Remove() { Inserted = false; KeyChanged?.Invoke(this, EventArgs.Empty); }

    /// <summary>Simulates unplugging this key and plugging in another one in factory state.</summary>
    public void ReplaceWith(int serialNumber, string? firmware = null)
    {
        SerialNumber = serialNumber;
        if (firmware is not null) Firmware = firmware;
        _pin = null;
        _forceChange = false;
        _credentials.Clear();
        Inserted = true;
        KeyChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool SupportsForce => Ylm.Core.FirmwareVersion.TryParse(Firmware, out var v) && v >= Ylm.Core.FirmwareVersion.ForcePinChangeMinimum;

    public Task<KeyInfo?> ReadAsync(CancellationToken ct = default) => Task.FromResult(Inserted ? Info() : null);

    private KeyInfo Info() => new()
    {
        SerialNumber = SerialNumber,
        FirmwareVersion = Firmware,
        FormFactor = "UsbAKeychain",
        Model = "YubiKey 5 NFC (simuliert)",
        AaGuid = SimulatedAaguid,
        PinIsSet = _pin is not null,
        SupportsForcePinChange = SupportsForce,
        MinimumPinLength = 4,
        RemainingDiscoverableCredentials = 100 - _credentials.Count,
        CtapVersions = ["FIDO_2_0", "FIDO_2_1"],
    };

    private OperationResult? Check(int? expectedSerial)
    {
        if (!Inserted) return OperationResult.Fail(DeviceErrors.NoKey, "Kein YubiKey eingesteckt.");
        if (expectedSerial is { } s && s != SerialNumber) return OperationResult.Fail(DeviceErrors.WrongKey, "Falscher Key.");
        return null;
    }

    public async Task<OperationResult> ResetAsync(int? expectedSerial, IOperationPrompts prompts, TimeSpan timeout, CancellationToken ct = default)
    {
        if (Check(expectedSerial) is { } e) return e;
        prompts.Prompt("Simulation: Key wird ab- und wieder angesteckt.");
        await Task.Delay(50, ct);
        _pin = null;
        _forceChange = false;
        _credentials.Clear();
        return OperationResult.Ok();
    }

    public Task<OperationResult> SetPinAsync(int? expectedSerial, string pin, CancellationToken ct = default)
    {
        if (Check(expectedSerial) is { } e) return Task.FromResult(e);
        if (_pin is not null) return Task.FromResult(OperationResult.Fail(DeviceErrors.PinAlreadySet, "PIN bereits gesetzt."));
        if (pin.Length < 4) return Task.FromResult(OperationResult.Fail(DeviceErrors.PinRejected, "PIN zu kurz."));
        _pin = pin;
        return Task.FromResult(OperationResult.Ok());
    }

    public Task<MakeCredentialResult> MakeCredentialAsync(int? expectedSerial, string pin, WebAuthnCreationOptions options, string origin, IOperationPrompts prompts, CancellationToken ct = default)
    {
        if (Check(expectedSerial) is { } e) return Task.FromResult(new MakeCredentialResult { Success = false, Error = e.Error, ErrorCode = e.ErrorCode });
        if (_pin != pin) return Task.FromResult(new MakeCredentialResult { Success = false, Error = "Falsche PIN.", ErrorCode = DeviceErrors.PinRejected });
        prompts.Prompt("Simulation: Key wird berührt.");
        var rpIdHash = SHA256.HashData(Encoding.UTF8.GetBytes(options.RpId));
        if (options.ExcludeCredentials.Any(x => _credentials.Any(c => c.CredId.AsSpan().SequenceEqual(x.Id))))
            return Task.FromResult(new MakeCredentialResult { Success = false, Error = "Credential bereits vorhanden.", ErrorCode = DeviceErrors.Failed });

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(false);
        var credId = RandomNumberGenerator.GetBytes(32);
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);   // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);  // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);  // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(p.Q.X);
        cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y);
        cose.WriteEndMap();

        var authData = new List<byte>();
        authData.AddRange(rpIdHash);
        authData.Add(0x45); // UP | UV | AT
        authData.AddRange([0, 0, 0, 0]);
        authData.AddRange(SimulatedAaguid.ToByteArray(bigEndian: true));
        authData.Add((byte)(credId.Length >> 8));
        authData.Add((byte)credId.Length);
        authData.AddRange(credId);
        authData.AddRange(cose.Encode());

        var attStmt = new CborWriter();
        attStmt.WriteStartMap(0);
        attStmt.WriteEndMap();

        var clientData = WebAuthnEncoding.ClientDataJson(options.Challenge, origin);
        _credentials.Add((rpIdHash, credId));
        return Task.FromResult(new MakeCredentialResult
        {
            Success = true,
            CredentialId = Base64Url.Encode(credId),
            ClientDataJson = Base64Url.Encode(clientData),
            AttestationObject = Base64Url.Encode(WebAuthnEncoding.AttestationObject("none", attStmt.Encode(), authData.ToArray())),
            AaGuid = SimulatedAaguid,
        });
    }

    public Task<OperationResult> ForcePinChangeAsync(int? expectedSerial, string pin, CancellationToken ct = default)
    {
        if (Check(expectedSerial) is { } e) return Task.FromResult(e);
        if (!SupportsForce) return Task.FromResult(OperationResult.Fail(DeviceErrors.NotSupported, "Nicht unterstützt."));
        if (_pin != pin) return Task.FromResult(OperationResult.Fail(DeviceErrors.PinRejected, "Falsche PIN."));
        _forceChange = true;
        return Task.FromResult(OperationResult.Ok());
    }
}
