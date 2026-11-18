using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ylm.Agent.Core;

namespace Ylm.Agent;

/// <summary>
/// Auto-Update: fragt den Server regelmäßig, ob er ein neueres Agent-MSI mitbringt, lädt es herunter,
/// prüft die SHA-256-Prüfsumme und installiert es still per msiexec (der Dienst läuft mit Systemrechten).
/// Abschaltbar mit Agent:AutoUpdate=false in appsettings.json.
/// </summary>
public sealed class AgentUpdater(AgentConnection connection, AgentSettingsStore store, IConfiguration config, ILogger<AgentUpdater> logger, string currentVersion) : BackgroundService
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(4);

    private sealed record UpdateInfo(string Version, string Sha256, long Size, string Url);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows() || !config.GetValue("Agent:AutoUpdate", true) || config.GetValue<bool>("Agent:Simulate")) return;
        try { await Task.Delay(FirstCheck, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await CheckAsync(stoppingToken); }
            catch (Exception e) when (e is not OperationCanceledException) { logger.LogWarning("Updateprüfung fehlgeschlagen: {Message}", e.Message); }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var settings = store.Load();
        var token = store.Token(settings);
        if (settings.ServerUrl is null || token is null) return;
        if (connection.Status.Operation is { Done: false })
        {
            logger.LogInformation("Update verschoben: Der Agent arbeitet gerade.");
            return;
        }

        using var http = new HttpClient { BaseAddress = new Uri(settings.ServerUrl), Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.GetAsync("/api/agent/update?current=" + Uri.EscapeDataString(currentVersion), ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NoContent) return;
        res.EnsureSuccessStatusCode();
        var info = await res.Content.ReadFromJsonAsync<UpdateInfo>(ct);
        if (info is null) return;
        logger.LogInformation("Neue Agent-Version {Version} verfügbar (installiert: {Current}). Lade herunter …", info.Version, currentVersion);

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "YubikeyLifecycleManager", "Agent", "updates");
        if (OperatingSystem.IsWindows()) EnsureProtectedDirectory(dir);
        var msi = Path.Combine(dir, $"YubikeyLifecycleManager-Agent-{info.Version}.msi");
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

        // msiexec läuft als eigener Prozess weiter, wenn die Installation diesen Dienst beendet und neu startet.
        var log = Path.Combine(dir, $"install-{info.Version}.log");
        logger.LogInformation("Installiere Agent {Version} …", info.Version);
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msi}\" /qn /norestart /l*v \"{log}\"") { UseShellExecute = false, CreateNoWindow = true });
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
