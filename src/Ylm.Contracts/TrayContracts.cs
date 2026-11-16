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
}

public enum TrayMessageType { Status, Notification, PairRequest, PairResult, Unpair, GetStatus }

public sealed record TrayMessage
{
    public TrayMessageType Type { get; init; }
    public TrayStatus? Status { get; init; }
    public string? Title { get; init; }
    public string? Text { get; init; }
    public bool IsError { get; init; }
    public string? ServerUrl { get; init; }
    public string? PairingCode { get; init; }
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
