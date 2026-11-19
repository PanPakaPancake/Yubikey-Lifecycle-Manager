using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ylm.Agent.Core;
using Ylm.Contracts;

namespace Ylm.Agent;

/// <summary>
/// Auto-Update: fragt den Server regelmäßig, ob er ein neueres Agent-MSI mitbringt, lädt es herunter,
/// prüft die SHA-256-Prüfsumme und installiert es still per msiexec (der Dienst läuft mit Systemrechten).
///
/// Ablauf einer Installation:
/// 1. Die Tray-Apps werden gebeten, sich zu beenden (sonst ist YlmTray.exe gesperrt und Windows ersetzt sie erst nach einem Neustart).
///    Die Sitzungen, in denen eine Tray-App lief, landen in updates/pending.json.
/// 2. msiexec installiert die neue Version. Dabei beendet die Installation diesen Dienst und startet die neue Version.
/// 3. Die neue Version liest pending.json beim Start, meldet Erfolg oder Fehler und startet die Tray-Apps in den gemerkten Sitzungen neu.
///    Endet msiexec mit einem Fehler, während dieser Dienst noch läuft, passiert Schritt 3 sofort.
///
/// Ob automatisch installiert wird, entscheiden die Server-Vorgabe (Globale Einstellungen) und die lokale Einstellung
/// in der Tray-App. Agent:AutoUpdate=false in appsettings.json setzt nur den Standardwert der lokalen Einstellung.
/// </summary>
public sealed partial class AgentUpdater(
    AgentConnection connection, AgentSettingsStore store, IConfiguration config, ILogger<AgentUpdater> logger, string currentVersion, string dataDirectory)
    : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);
    private static readonly TimeSpan RetryAfterError = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _updateDir = Path.Combine(dataDirectory, "updates");
    private string StateFile => Path.Combine(dataDirectory, "update-state.json");
    private string PendingFile => Path.Combine(_updateDir, "pending.json");
    private AgentUpdateState _state = new();
    private CancellationToken _stopping;

    private sealed record Pending(string FromVersion, string TargetVersion, string Log, int[] TraySessions, DateTimeOffset StartedAt);

    /// <summary>Auf diesem System grundsätzlich möglich (Windows-Dienst, kein Simulator).</summary>
    private bool Supported => OperatingSystem.IsWindows() && !config.GetValue<bool>("Agent:Simulate");

    public AgentUpdateState State => _state;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _state = LoadState() with { Installing = false };
        Recompute();
        connection.UpdateReport = () => (Supported ? _state.AutoUpdate : null, _state.LastResultIsError ? _state.LastResult : null);
        if (!Supported) return;
        connection.UpdateRequested = _ => CheckWithTimeoutAsync();

        await FinishPendingAsync(stoppingToken);

        try { await Task.Delay(FirstCheck, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckAsync(manual: false, stoppingToken);
            var wait = _state.LastResultIsError ? RetryAfterError : Interval;
            try { await Task.Delay(wait, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// "Jetzt aktualisieren" aus Tray oder Weboberfläche. Wartet höchstens 20 Sekunden auf das Ergebnis; Download und Installation
    /// laufen danach im Hintergrund weiter (bei Erfolg startet der Dienst ohnehin neu).
    /// </summary>
    public async Task<string> CheckWithTimeoutAsync()
    {
        var check = CheckAsync(manual: true, _stopping);
        var done = await Task.WhenAny(check, Task.Delay(TimeSpan.FromSeconds(20)));
        return done == check ? await check : _state.LastResult ?? "Update läuft …";
    }

    // ---------- Einstellungen ----------

    private bool LocalDefault => config.GetValue("Agent:AutoUpdate", true);

    private void Recompute()
    {
        var settings = store.Load();
        var local = settings.AutoUpdate ?? LocalDefault;
        var policy = settings.ServerPolicy ?? AgentUpdatePolicy.AgentDecides;
        var effective = Supported && policy switch
        {
            AgentUpdatePolicy.Always => true,
            AgentUpdatePolicy.Never => false,
            _ => local,
        };
        Publish(s => s with { LocalAutoUpdate = local, Policy = policy, AutoUpdate = effective });
    }

    /// <summary>Lokale Einstellung "Automatische Updates" (aus der Tray-App, nur Administratoren).</summary>
    public async Task<(bool Ok, string Message)> SetAutoUpdateAsync(bool enabled)
    {
        if (!Supported) return (false, "Auto-Update gibt es nur im installierten Windows-Dienst.");
        store.Change(s => s with { AutoUpdate = enabled });
        Recompute();
        logger.LogInformation("Automatische Updates lokal {State}", enabled ? "eingeschaltet" : "ausgeschaltet");
        await connection.ResendHelloAsync();
        var note = _state.Policy switch
        {
            AgentUpdatePolicy.Always when !enabled => " Der Server schreibt aber automatische Updates vor.",
            AgentUpdatePolicy.Never when enabled => " Der Server hat automatische Updates aber abgeschaltet.",
            _ => "",
        };
        return (true, (enabled ? "Automatische Updates sind eingeschaltet." : "Automatische Updates sind ausgeschaltet.") + note);
    }

    private void Publish(Func<AgentUpdateState, AgentUpdateState> change)
    {
        _state = change(_state);
        connection.SetUpdateState(_state);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(_state with { Installing = false }, Json));
        }
        catch (Exception e) { logger.LogDebug(e, "Update-Status nicht gespeichert"); }
    }

    private AgentUpdateState LoadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<AgentUpdateState>(File.ReadAllText(StateFile), Json) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    private void Result(string text, bool error, string? available = null)
    {
        if (error) logger.LogWarning("Update: {Text}", text);
        else logger.LogInformation("Update: {Text}", text);
        Publish(s => s with { LastResult = text, LastResultIsError = error, LastCheckAt = DateTimeOffset.UtcNow, AvailableVersion = available, Installing = false });
    }

    // ---------- Prüfen und installieren ----------

    /// <summary>Fragt den Server. <paramref name="manual"/>: "Jetzt aktualisieren" (Tray oder Weboberfläche) installiert auch bei abgeschaltetem Auto-Update.</summary>
    public async Task<string> CheckAsync(bool manual, CancellationToken ct)
    {
        if (!Supported) return "Auto-Update gibt es nur im installierten Windows-Dienst.";
        if (!await _gate.WaitAsync(0, ct)) return "Es läuft bereits eine Updateprüfung.";
        var reportedError = _state.LastResultIsError;
        try
        {
            var settings = store.Load();
            var token = store.Token(settings);
            if (settings.ServerUrl is null || token is null)
            {
                Result("Nicht gekoppelt, keine Updateprüfung.", false);
                return _state.LastResult!;
            }
            if (connection.Status.Operation is { Done: false })
            {
                Result("Update verschoben: Der Agent arbeitet gerade.", false, _state.AvailableVersion);
                return _state.LastResult!;
            }

            using var http = new HttpClient { BaseAddress = new Uri(settings.ServerUrl), Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await http.GetAsync("/api/agent/update?info=1&current=" + Uri.EscapeDataString(currentVersion), ct);
            AgentUpdateInfo? info;
            if (res.StatusCode == System.Net.HttpStatusCode.NoContent) info = new AgentUpdateInfo(false, null, null, null, null, AgentUpdatePolicy.AgentDecides); // Server vor 0.5
            else
            {
                res.EnsureSuccessStatusCode();
                info = await res.Content.ReadFromJsonAsync<AgentUpdateInfo>(Json, ct);
            }
            if (info is null) throw new InvalidOperationException("Leere Antwort vom Server.");
            if (settings.ServerPolicy != info.Policy)
            {
                store.Change(s => s with { ServerPolicy = info.Policy });
                Recompute();
            }

            if (!info.Available || info.Version is null || info.Url is null || info.Sha256 is null)
            {
                Result($"Aktuell (Version {currentVersion}).", false);
                return _state.LastResult!;
            }
            if (!manual && !_state.AutoUpdate)
            {
                Result($"Version {info.Version} ist verfügbar. Automatische Updates sind aus{(info.Policy == AgentUpdatePolicy.Never ? " (Vorgabe des Servers)" : "")}.", false, info.Version);
                return _state.LastResult!;
            }

            logger.LogInformation("Neue Agent-Version {Version} verfügbar (installiert: {Current}). Lade herunter …", info.Version, currentVersion);
            Publish(s => s with { Installing = true, AvailableVersion = info.Version, LastResult = $"Lade Version {info.Version} …", LastResultIsError = false });
            var msi = await DownloadAsync(http, info, ct);
            return await InstallAsync(info.Version, msi, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            Result("Updateprüfung fehlgeschlagen: " + e.Message, true, _state.AvailableVersion);
            return _state.LastResult!;
        }
        finally
        {
            _gate.Release();
            if (reportedError != _state.LastResultIsError) _ = connection.ResendHelloAsync();
        }
    }

    private async Task<string> DownloadAsync(HttpClient http, AgentUpdateInfo info, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) EnsureProtectedDirectory(_updateDir);
        else Directory.CreateDirectory(_updateDir);
        // Alte Pakete aufräumen, nur das aktuelle bleibt liegen.
        foreach (var old in Directory.EnumerateFiles(_updateDir, "*.msi"))
            try { File.Delete(old); } catch (IOException) { }
        var msi = Path.Combine(_updateDir, $"YubikeyLifecycleManager-Agent-{info.Version}.msi");
        await using (var file = File.Create(msi))
        await using (var stream = await http.GetStreamAsync(info.Url, ct))
            await stream.CopyToAsync(file, ct);

        await using (var check = File.OpenRead(msi))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct));
            if (!string.Equals(hash, info.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(msi);
                throw new InvalidOperationException("Prüfsumme des heruntergeladenen MSI stimmt nicht.");
            }
        }
        return msi;
    }

    private async Task<string> InstallAsync(string version, string msi, CancellationToken ct)
    {
        var log = Path.Combine(_updateDir, $"install-{version}.log");
        var sessions = TraySessions();
        await File.WriteAllTextAsync(PendingFile, JsonSerializer.Serialize(new Pending(currentVersion, version, log, sessions, DateTimeOffset.UtcNow), Json), ct);
        Publish(s => s with { Installing = true, LastResult = $"Installiere Version {version} …", LastResultIsError = false });

        // Tray-Apps beenden, damit YlmTray.exe ersetzt werden kann.
        connection.RequestTrayExit();
        await WaitForTraysToExitAsync(TimeSpan.FromSeconds(10), ct);

        logger.LogInformation("Installiere Agent {Version} (Protokoll: {Log}) …", version, log);
        // Restart Manager aus: Die Tray-Apps sind schon beendet, und der Dienst wird über ServiceControl im MSI gestoppt.
        // msiexec läuft als eigener Prozess weiter, wenn die Installation diesen Dienst beendet und die neue Version startet.
        using var process = Process.Start(new ProcessStartInfo("msiexec.exe",
            $"/i \"{msi}\" /qn /norestart MSIRESTARTMANAGERCONTROL=Disable REBOOT=ReallySuppress /l*v \"{log}\"") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("msiexec konnte nicht gestartet werden.");
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { return $"Installiere Version {version} …"; } // Dienst wird für das Update beendet

        // Wir laufen noch: Die Installation ist fertig, ohne diesen Dienst zu ersetzen, also in der Regel fehlgeschlagen.
        var code = process.ExitCode;
        TryDelete(PendingFile);
        LaunchTrays(sessions, updated: false);
        if (code is 0 or 3010 or 1641)
        {
            Result($"Installation von {version} beendet (Code {code}), der Dienst wurde aber nicht neu gestartet. Bitte den PC neu starten.", true, version);
            return _state.LastResult!;
        }
        Result($"Installation von {version} fehlgeschlagen: {DescribeMsiError(code)} Protokoll: {log}", true, version);
        return _state.LastResult!;
    }

    /// <summary>Nach dem Start der neuen (oder nach einem Fehler weiterlaufenden alten) Version: Ergebnis melden, Tray neu starten.</summary>
    private async Task FinishPendingAsync(CancellationToken ct)
    {
        Pending? pending;
        try { pending = File.Exists(PendingFile) ? JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(PendingFile, ct), Json) : null; }
        catch (Exception) { pending = null; }
        TryDelete(PendingFile);
        if (pending is null) return;

        if (pending.TargetVersion == currentVersion)
            Result($"Aktualisiert von {pending.FromVersion} auf {currentVersion} am {DateTime.Now:dd.MM.yyyy HH:mm}.", false);
        else
        {
            var status = ReadMsiStatus(pending.Log);
            Result($"Update auf {pending.TargetVersion} fehlgeschlagen{(status is { } c ? ": " + DescribeMsiError(c) : ".")} Protokoll: {pending.Log}", true, pending.TargetVersion);
        }
        _ = connection.ResendHelloAsync();
        // Kurz warten, bis die Benutzersitzungen nach einem Dienststart bereit sind.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return; }
        LaunchTrays(pending.TraySessions, updated: pending.TargetVersion == currentVersion);
    }

    // ---------- Tray-Apps ----------

    private static int[] TraySessions()
    {
        try
        {
            return Process.GetProcessesByName("YlmTray").Select(p => { using (p) return p.SessionId; }).Where(id => id > 0).Distinct().ToArray();
        }
        catch (Exception) { return []; }
    }

    private async Task WaitForTraysToExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && Process.GetProcessesByName("YlmTray").Length > 0)
            await Task.Delay(500, ct);
        foreach (var p in Process.GetProcessesByName("YlmTray"))
        {
            using (p)
            {
                try
                {
                    logger.LogInformation("Tray-App in Sitzung {Session} reagiert nicht, wird für das Update beendet.", p.SessionId);
                    p.Kill();
                    p.WaitForExit(5000);
                }
                catch (Exception e) { logger.LogWarning("Tray-App konnte nicht beendet werden: {Message}", e.Message); }
            }
        }
    }

    private void LaunchTrays(IEnumerable<int> sessions, bool updated)
    {
        if (!OperatingSystem.IsWindows()) return;
        var exe = Path.Combine(AppContext.BaseDirectory, "YlmTray.exe");
        if (!File.Exists(exe)) return;
        var running = TraySessions();
        foreach (var session in sessions.Except(running))
        {
            try
            {
                TrayLauncher.StartInSession((uint)session, exe, updated ? "--updated" : "");
                logger.LogInformation("Tray-App in Sitzung {Session} neu gestartet.", session);
            }
            catch (Exception e) { logger.LogWarning("Tray-App in Sitzung {Session} nicht gestartet: {Message}", session, e.Message); }
        }
    }

    // ---------- MSI-Fehler ----------

    internal static string DescribeMsiError(int code) => code switch
    {
        1602 => "Abgebrochen (1602).",
        1603 => "Schwerer Fehler bei der Installation (1603).",
        1618 => "Eine andere Installation läuft gerade (1618). Der Agent versucht es später erneut.",
        1619 => "Das Paket ließ sich nicht öffnen (1619).",
        1625 => "Die Installation ist durch eine Richtlinie verboten (1625).",
        1638 => "Diese Version ist bereits installiert (1638).",
        _ => $"Fehlercode {code}.",
    };

    internal static int? ReadMsiStatus(string log)
    {
        try
        {
            if (!File.Exists(log)) return null;
            // msiexec schreibt das Protokoll in UTF-16; File.ReadAllText erkennt das am BOM.
            var match = MsiStatus().Matches(File.ReadAllText(log)).LastOrDefault();
            return match is null ? null : int.Parse(match.Groups[1].Value);
        }
        catch (Exception) { return null; }
    }

    [GeneratedRegex(@"(?:success or error status|Erfolg- oder Fehlerstatus): (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MsiStatus();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    /// <summary>
    /// Nur SYSTEM und Administratoren dürfen in den Update-Ordner schreiben. Sonst könnte ein Benutzer ohne Adminrechte
    /// das geprüfte MSI vor dem Start von msiexec austauschen. Ein fremd angelegter Ordner wird nicht verwendet.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void EnsureProtectedDirectory(string dir)
    {
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var security = new System.Security.AccessControl.DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { system, admins })
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        security.SetOwner(admins);
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
        {
            info.Create(security);
            return;
        }
        var owner = info.GetAccessControl().GetOwner(typeof(System.Security.Principal.SecurityIdentifier));
        if (owner != system && owner != admins)
            throw new InvalidOperationException($"Der Update-Ordner {dir} gehört nicht SYSTEM oder den Administratoren. Bitte löschen; er wird dann neu angelegt.");
        info.SetAccessControl(security);
    }
}
