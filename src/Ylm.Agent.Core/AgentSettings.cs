using System.Text.Json;

namespace Ylm.Agent.Core;

public sealed record AgentSettings
{
    public string? ServerUrl { get; init; }
    public Guid? AgentId { get; init; }
    /// <summary>Agent token, protected by <see cref="ISecretProtector"/>.</summary>
    public string? ProtectedToken { get; init; }
    public string? PairedUser { get; init; }
    public string? InstanceName { get; init; }
}

/// <summary>Protects the agent token at rest (DPAPI LocalMachine on Windows).</summary>
public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

/// <summary>Fallback for development on non-Windows systems: no encryption.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public string Protect(string value) => value;
    public string Unprotect(string value) => value;
}

public sealed class AgentSettingsStore(string path, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public AgentSettings Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<AgentSettings>(File.ReadAllText(path), Json) ?? new AgentSettings() : new AgentSettings();
        }
        catch (Exception)
        {
            return new AgentSettings();
        }
    }

    public void Save(AgentSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, path, true);
    }

    public string? Token(AgentSettings s) => s.ProtectedToken is null ? null : protector.Unprotect(s.ProtectedToken);

    public AgentSettings WithToken(AgentSettings s, string token) => s with { ProtectedToken = protector.Protect(token) };
}
