namespace Ylm.Contracts;

/// <summary>Information the agent reads from a plugged-in YubiKey.</summary>
public sealed record KeyInfo
{
    public int? SerialNumber { get; init; }
    /// <summary>Firmware as "major.minor.patch".</summary>
    public required string FirmwareVersion { get; init; }
    /// <summary>Form factor as reported by the SDK, e.g. UsbAKeychain.</summary>
    public string? FormFactor { get; init; }
    /// <summary>Human readable model, e.g. "YubiKey 5 NFC".</summary>
    public string? Model { get; init; }
    public Guid? AaGuid { get; init; }
    public bool HasFido2 { get; init; } = true;
    public bool IsFips { get; init; }
    /// <summary>True when a FIDO2 PIN is already set on the key.</summary>
    public bool PinIsSet { get; init; }
    /// <summary>True when the key supports authenticatorConfig/setMinPINLength with forceChangePin (CTAP 2.1, FW 5.7+).</summary>
    public bool SupportsForcePinChange { get; init; }
    public int? MinimumPinLength { get; init; }
    public int? RemainingDiscoverableCredentials { get; init; }
    public IReadOnlyList<string> CtapVersions { get; init; } = [];
}

public sealed record OperationResult(bool Success, string? Error = null, string? ErrorCode = null)
{
    public static OperationResult Ok() => new(true);
    public static OperationResult Fail(string code, string message) => new(false, message, code);
}

public sealed record ResetRequest
{
    /// <summary>Serial number of the key that must be reset (safety check).</summary>
    public int? ExpectedSerialNumber { get; init; }
    public int TimeoutSeconds { get; init; } = 120;
}

public sealed record SetPinRequest
{
    public int? ExpectedSerialNumber { get; init; }
    public required string NewPin { get; init; }
}

public sealed record ForcePinChangeRequest
{
    public int? ExpectedSerialNumber { get; init; }
    public required string Pin { get; init; }
}

public sealed record MakeCredentialRequest
{
    public int? ExpectedSerialNumber { get; init; }
    public required string Pin { get; init; }
    /// <summary>The publicKey part of the Graph creationOptions as JSON.</summary>
    public required string PublicKeyOptionsJson { get; init; }
    /// <summary>WebAuthn origin used for clientDataJSON, normally https://login.microsoft.com.</summary>
    public string Origin { get; init; } = "https://login.microsoft.com";
}

public sealed record MakeCredentialResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? ErrorCode { get; init; }
    /// <summary>base64url credential id.</summary>
    public string? CredentialId { get; init; }
    /// <summary>base64url clientDataJSON.</summary>
    public string? ClientDataJson { get; init; }
    /// <summary>base64url WebAuthn attestationObject (CBOR with fmt, attStmt, authData).</summary>
    public string? AttestationObject { get; init; }
    public Guid? AaGuid { get; init; }
}

public sealed record AgentProgress(string Title, string? Prompt = null, int? Step = null, int? Total = null, bool Done = false, bool Failed = false);

/// <summary>Methods the server invokes on a connected agent (SignalR client results).</summary>
public interface IAgentClient
{
    Task<KeyInfo?> GetKeyInfo(CancellationToken cancellationToken);
    Task<OperationResult> ResetFido(ResetRequest request, CancellationToken cancellationToken);
    Task<OperationResult> SetPin(SetPinRequest request, CancellationToken cancellationToken);
    Task<MakeCredentialResult> MakeCredential(MakeCredentialRequest request, CancellationToken cancellationToken);
    Task<OperationResult> ForcePinChange(ForcePinChangeRequest request, CancellationToken cancellationToken);
    Task ShowProgress(AgentProgress progress);
    /// <summary>Der Server bittet den Agent, sofort nach einem Update zu suchen und es zu installieren. Antwort: Ergebnistext.</summary>
    Task<string> CheckForUpdate(CancellationToken cancellationToken);
}

public static class AgentProtocol
{
    public const string HubPath = "/hubs/agent";
    public const string ProtocolVersion = "1";
    public const string ReportKeyStateMethod = "ReportKeyState";
    /// <summary>Agent → server: what the admin has to do at the key right now (unplug, plug in, touch).</summary>
    public const string ReportPromptMethod = "ReportPrompt";
    public const string ReportHelloMethod = "Hello";
}

/// <param name="AutoUpdate">Ob der Agent Updates selbst installiert (wirksamer Wert nach Server-Vorgabe). Null bei Agents vor 0.5.</param>
/// <param name="UpdateError">Fehlertext des letzten Updateversuchs, null wenn alles in Ordnung ist.</param>
public sealed record AgentHello(string MachineName, string AgentVersion, string ProtocolVersion, string? OsUser, bool? AutoUpdate = null, string? UpdateError = null);

/// <summary>Server-Vorgabe für automatische Agent-Updates (Globale Einstellungen).</summary>
public enum AgentUpdatePolicy
{
    /// <summary>Standard: Agents aktualisieren sich automatisch, am PC lässt sich das abschalten.</summary>
    AgentDecides = 0,
    /// <summary>Immer automatisch, der Schalter am PC ist gesperrt.</summary>
    Always = 1,
    /// <summary>Keine automatischen Updates; am PC geht "Jetzt aktualisieren" weiterhin.</summary>
    Never = 2,
}

/// <summary>Antwort von GET /api/agent/update?info=1.</summary>
public sealed record AgentUpdateInfo(bool Available, string? Version, string? Sha256, long? Size, string? Url, AgentUpdatePolicy Policy);

public sealed record PairingRequest(string Code, string MachineName, string AgentVersion);
public sealed record PairingResponse(Guid AgentId, string AgentToken, string UserDisplayName, string InstanceName);
