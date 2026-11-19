using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ylm.Contracts;

public enum BackendConnectionState { NotPaired, Connecting, Connected, Disconnected, Unauthorized }

/// <summary>Status the Windows service pushes to the tray app over the named pipe.</summary>
public sealed record TrayStatus
{
    public BackendConnectionState Connection { get; init; }
    public string? ServerUrl { get; init; }
    public string? InstanceName { get; init; }
    public string? PairedUser { get; init; }
    public KeyInfo? Key { get; init; }
    public AgentProgress? Operation { get; init; }
    public string AgentVersion { get; init; } = "";
    public string? LastError { get; init; }
    public AgentUpdateState? Update { get; init; }
}

/// <summary>Stand des Auto-Updates, den die Tray-App in den Einstellungen anzeigt.</summary>
public sealed record AgentUpdateState
{
    /// <summary>Wirksam: installiert der Agent Updates selbst?</summary>
    public bool AutoUpdate { get; init; } = true;
    /// <summary>Lokale Einstellung dieses PCs (gilt, solange der Server nichts vorgibt).</summary>
    public bool LocalAutoUpdate { get; init; } = true;
    public AgentUpdatePolicy Policy { get; init; }
    public DateTimeOffset? LastCheckAt { get; init; }
    /// <summary>Neuere Version auf dem Server, falls vorhanden.</summary>
    public string? AvailableVersion { get; init; }
    public string? LastResult { get; init; }
    public bool LastResultIsError { get; init; }
    public bool Installing { get; init; }
}

public enum TrayMessageType
{
    Status, Notification, PairRequest, PairResult, Unpair, GetStatus,
    /// <summary>Tray → Dienst: automatische Updates an/aus (<see cref="TrayMessage.Enabled"/>), nur für Administratoren.</summary>
    SetAutoUpdate,
    /// <summary>Tray → Dienst: jetzt nach Updates suchen und ein gefundenes installieren.</summary>
    CheckForUpdate,
    /// <summary>Dienst → Tray: Antwort auf SetAutoUpdate/CheckForUpdate (<see cref="TrayMessage.Text"/>, <see cref="TrayMessage.IsError"/>).</summary>
    CommandResult,
    /// <summary>Dienst → Tray: Update wird installiert, bitte beenden (der Dienst startet die Tray-App danach neu).</summary>
    UpdateInstalling,
}

public sealed record TrayMessage
{
    public TrayMessageType Type { get; init; }
    public TrayStatus? Status { get; init; }
    public string? Title { get; init; }
    public string? Text { get; init; }
    public bool IsError { get; init; }
    public string? ServerUrl { get; init; }
    public string? PairingCode { get; init; }
    public bool? Enabled { get; init; }
}

public static class TrayPipe
{
    public const string PipeName = "YubikeyLifecycleManager.Agent";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(TrayMessage message) => JsonSerializer.Serialize(message, Json);
    public static TrayMessage? Deserialize(string line) => JsonSerializer.Deserialize<TrayMessage>(line, Json);
}
