using System.Text.Json;
using System.Text.Json.Serialization;
using Ylm.Contracts;

namespace Ylm.Agent.Core;

public sealed record AgentSettings
{
    public string? ServerUrl { get; init; }
    public Guid? AgentId { get; init; }
    /// <summary>Agent token, protected by <see cref="ISecretProtector"/>.</summary>
    public string? ProtectedToken { get; init; }
    public string? PairedUser { get; init; }
    public string? InstanceName { get; init; }
    /// <summary>Lokale Einstellung "Automatische Updates" (null = Standard: an).</summary>
    public bool? AutoUpdate { get; init; }
    /// <summary>Zuletzt vom Server gemeldete Vorgabe.</summary>
    public AgentUpdatePolicy? ServerPolicy { get; init; }
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

/// <param name="secureFile">Wird vor dem Ersetzen mit der neuen Datei aufgerufen (Windows: Zugriff nur für SYSTEM und Administratoren).</param>
public sealed class AgentSettingsStore(string path, ISecretProtector protector, Action<string>? secureFile = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly Lock _lock = new();

    public string FilePath => path;

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

    /// <summary>Liest, ändert und speichert unter einer Sperre (Kopplung und Update-Einstellungen schreiben dieselbe Datei).</summary>
    public AgentSettings Change(Func<AgentSettings, AgentSettings> change)
    {
        lock (_lock)
        {
            var next = change(Load());
            Save(next);
            return next;
        }
    }

    public void Save(AgentSettings settings)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            secureFile?.Invoke(tmp);
            File.Move(tmp, path, true);
        }
    }

    public string? Token(AgentSettings s) => s.ProtectedToken is null ? null : protector.Unprotect(s.ProtectedToken);

    public AgentSettings WithToken(AgentSettings s, string token) => s with { ProtectedToken = protector.Protect(token) };
}
