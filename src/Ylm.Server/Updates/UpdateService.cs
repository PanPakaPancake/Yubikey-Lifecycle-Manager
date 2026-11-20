using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Updates;

/// <summary>Versionen der Installation: Server, mitgelieferter Agent und die neueste Version auf GitHub.</summary>
public sealed class UpdateService(IHttpClientFactory httpFactory, IMemoryCache cache, IOptions<YlmOptions> options, IWebHostEnvironment env, ILogger<UpdateService> logger)
{
    public const string AgentFileName = "YubikeyLifecycleManager-Agent.msi";

    public static string CurrentVersion { get; } = Normalize(
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    /// <summary>"v1.2.3+abc" → "1.2.3".</summary>
    public static string Normalize(string version) => version.Trim().TrimStart('v', 'V').Split('+')[0];

    public static bool IsNewer(string? candidate, string? current) =>
        candidate is not null && current is not null
        && Version.TryParse(Pad(Normalize(candidate).Split('-')[0]), out var a) && Version.TryParse(Pad(Normalize(current).Split('-')[0]), out var b) && a > b;

    private static string Pad(string v) => v.Count(c => c == '.') switch { 0 => v + ".0.0", 1 => v + ".0", _ => v };

    // ---------- mitgelieferter Agent ----------

    public sealed record AgentPackage(string Path, string Version, string Sha256, long Size);

    private string AgentDirectory => options.Value.AgentPackageDirectory ?? System.IO.Path.Combine(env.ContentRootPath, "agent");

    /// <summary>MSI im Image (Release-Build legt es mit version.txt nach /app/agent). Null, wenn keins vorhanden ist.</summary>
    public AgentPackage? GetAgentPackage() => cache.GetOrCreate("agent-package", e =>
    {
        e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
        var path = System.IO.Path.Combine(AgentDirectory, AgentFileName);
        if (!File.Exists(path)) return null;
        var versionFile = System.IO.Path.Combine(AgentDirectory, "version.txt");
        var version = File.Exists(versionFile) ? Normalize(File.ReadAllText(versionFile)) : CurrentVersion;
        using var stream = File.OpenRead(path);
        return new AgentPackage(path, version, Convert.ToHexStringLower(SHA256.HashData(stream)), new FileInfo(path).Length);
    });

    // ---------- neueste Version auf GitHub ----------

    public sealed record LatestRelease(string Version, string Url, DateTimeOffset? PublishedAt, string? Notes, string? AgentDownloadUrl);
    public sealed record ReleaseCheck(LatestRelease? Latest, string? Error, DateTimeOffset CheckedAt);

    public async Task<ReleaseCheck> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        if (!options.Value.UpdateCheckEnabled) return new ReleaseCheck(null, "Die Updateprüfung ist abgeschaltet (Ylm__UpdateCheckEnabled).", DateTimeOffset.UtcNow);
        if (!force && cache.TryGetValue("release-check", out ReleaseCheck? cached) && cached is not null) return cached;
        ReleaseCheck result;
        try
        {
            var http = httpFactory.CreateClient("updates");
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{options.Value.UpdateApiBaseUrl.TrimEnd('/')}/repos/{options.Value.UpdateRepository}/releases/latest");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("YubikeyLifecycleManager", CurrentVersion));
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var res = await http.SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
                result = new ReleaseCheck(null, "Noch keine veröffentlichte Version gefunden.", DateTimeOffset.UtcNow);
            else
            {
                res.EnsureSuccessStatusCode();
                var gh = await res.Content.ReadFromJsonAsync<GitHubRelease>(ct) ?? throw new JsonException();
                var msi = gh.Assets?.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;
                result = new ReleaseCheck(new LatestRelease(Normalize(gh.TagName), gh.HtmlUrl, gh.PublishedAt, gh.Body, msi), null, DateTimeOffset.UtcNow);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Updateprüfung fehlgeschlagen: {Message}", e.Message);
            result = new ReleaseCheck(null, "GitHub ist nicht erreichbar: " + e.Message, DateTimeOffset.UtcNow);
        }
        cache.Set("release-check", result, result.Error is null ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(15));
        return result;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] List<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

    // ---------- Updater-Container ----------

    public bool UpdaterConfigured => !string.IsNullOrWhiteSpace(options.Value.UpdaterUrl) && !string.IsNullOrWhiteSpace(options.Value.UpdaterToken);

    private HttpRequestMessage UpdaterRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, options.Value.UpdaterUrl!.TrimEnd('/') + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.UpdaterToken);
        return req;
    }

    public async Task<JsonElement?> UpdaterStatusAsync(CancellationToken ct = default)
    {
        if (!UpdaterConfigured) return null;
        try
        {
            using var res = await httpFactory.CreateClient("updates").SendAsync(UpdaterRequest(HttpMethod.Get, "/status"), ct);
            if (!res.IsSuccessStatusCode) return JsonSerializer.SerializeToElement(new { state = "error", message = $"Updater antwortet mit {(int)res.StatusCode}." });
            return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return JsonSerializer.SerializeToElement(new { state = "unreachable", message = "Der Updater-Container ist nicht erreichbar." });
        }
    }

    public Task StartUpdateAsync(string? version, CancellationToken ct = default) =>
        PostUpdaterAsync("/update", new { version }, ct);

    /// <summary>Sicherheitsupdates: alle Images derselben Version neu laden und geänderte Container neu erstellen.</summary>
    public Task StartRefreshAsync(CancellationToken ct = default) => PostUpdaterAsync("/refresh", new { }, ct);

    /// <summary>GET am Updater. Null ohne Updater, (404) wenn der Updater die Funktion noch nicht kennt.</summary>
    public async Task<(System.Net.HttpStatusCode Status, JsonElement? Body)> UpdaterGetAsync(string path, CancellationToken ct = default)
    {
        if (!UpdaterConfigured) return (System.Net.HttpStatusCode.ServiceUnavailable, null);
        try
        {
            using var res = await httpFactory.CreateClient("updates").SendAsync(UpdaterRequest(HttpMethod.Get, path), ct);
            return (res.StatusCode, res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<JsonElement>(ct) : null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return (System.Net.HttpStatusCode.BadGateway, null);
        }
    }

    public async Task PostUpdaterAsync(string path, object body, CancellationToken ct)
    {
        if (!UpdaterConfigured) throw ApiException.Conflict("updater_missing", "Der Updater-Container ist nicht eingerichtet. Siehe docs/BETRIEB.md, Abschnitt Updates.");
        using var req = UpdaterRequest(HttpMethod.Post, path);
        req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        try
        {
            using var res = await httpFactory.CreateClient("updates").SendAsync(req, ct);
            if (res.StatusCode == System.Net.HttpStatusCode.Conflict) throw ApiException.Conflict("update_running", "Es läuft bereits ein Update.");
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) throw ApiException.Conflict("updater_outdated", "Der Updater kennt diese Funktion noch nicht. Bitte das Installationsskript einmal erneut ausführen (lädt den neuen Updater).");
            if (!res.IsSuccessStatusCode)
            {
                var detail = await res.Content.ReadAsStringAsync(ct);
                throw ApiException.Conflict("updater_failed", $"Der Updater hat den Auftrag abgelehnt ({(int)res.StatusCode}): {detail}");
            }
        }
        catch (HttpRequestException e)
        {
            throw ApiException.Conflict("updater_unreachable", "Der Updater-Container ist nicht erreichbar: " + e.Message);
        }
    }
}
