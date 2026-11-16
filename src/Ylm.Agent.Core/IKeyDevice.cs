using Ylm.Contracts;

namespace Ylm.Agent.Core;

/// <summary>Prompts shown to the admin during an operation (tray + WebGUI).</summary>
public interface IOperationPrompts
{
    void Prompt(string text);
}

/// <summary>The locally plugged-in YubiKey. Implemented with the Yubico .NET SDK, or simulated for development.</summary>
public interface IKeyDevice
{
    event EventHandler? KeyChanged;

    Task<KeyInfo?> ReadAsync(CancellationToken ct = default);

    /// <summary>FIDO2 reset. Requires removing and re-inserting the key and a touch within 10 seconds.</summary>
    Task<OperationResult> ResetAsync(int? expectedSerial, IOperationPrompts prompts, TimeSpan timeout, CancellationToken ct = default);

    Task<OperationResult> SetPinAsync(int? expectedSerial, string pin, CancellationToken ct = default);

    Task<MakeCredentialResult> MakeCredentialAsync(int? expectedSerial, string pin, WebAuthnCreationOptions options, string origin, IOperationPrompts prompts, CancellationToken ct = default);

    Task<OperationResult> ForcePinChangeAsync(int? expectedSerial, string pin, CancellationToken ct = default);
}

public static class DeviceErrors
{
    public const string NoKey = "no_key";
    public const string WrongKey = "wrong_key";
    public const string MultipleKeys = "multiple_keys";
    public const string PinAlreadySet = "pin_already_set";
    public const string PinRejected = "pin_rejected";
    public const string Timeout = "timeout";
    public const string NotSupported = "not_supported";
    public const string AccessDenied = "access_denied";
    public const string Failed = "failed";
}
