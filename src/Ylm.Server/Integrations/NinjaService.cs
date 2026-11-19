using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Reporting;

namespace Ylm.Server.Integrations;

public sealed record NinjaMetric(string Id, string Label, string DefaultField, string Kind);

public sealed record NinjaOrganization(long Id, string Name);

public sealed record NinjaPushResult(int Tenants, int Succeeded, IReadOnlyList<string> Errors);

/// <summary>
/// NinjaOne (NinjaRMM) V2: jeder Tenant wird einer NinjaOne-Organisation zugeordnet; die Plattform schreibt die
/// Kennzahlen des Tenants regelmäßig in Organisationsfelder (Custom Fields). Nur ausgehend, Ninja schreibt nichts zurück.
/// </summary>
public sealed class NinjaService(YlmDbContext db, IHttpClientFactory http, IDataProtectionProvider dataProtection, DashboardService dashboards, AuditService audit, ILogger<NinjaService> logger)
{
    public const string SecretPurpose = "Ylm.Ninja.Secret";

    public static readonly IReadOnlyList<NinjaMetric> Metrics =
    [
        new("summary", "Zusammenfassung (Text)", "ylmSummary", "text"),
        new("keysActive", "Keys aktiv", "ylmKeysActive", "number"),
        new("keysInStock", "Keys im Lager", "ylmKeysInStock", "number"),
        new("keysLocked", "Keys gesperrt", "ylmKeysLocked", "number"),
        new("usersWithKey", "Personen mit Key", "ylmUsersWithKey", "number"),
        new("usersWithoutKey", "Personen der Zielgruppe ohne Key", "ylmUsersWithoutKey", "number"),
        new("coveragePercent", "Abdeckung in Prozent", "ylmCoveragePercent", "number"),
        new("vulnerableKeys", "Keys mit Sicherheitshinweis", "ylmVulnerableKeys", "number"),
        new("openTodos", "Offene ToDos", "ylmOpenTodos", "number"),
        new("lastSync", "Letzter Entra-Sync (Text)", "ylmLastSync", "text"),
    ];

    public static readonly IReadOnlyList<string> Regions =
        ["https://app.ninjarmm.com", "https://eu.ninjarmm.com", "https://ca.ninjarmm.com", "https://oc.ninjarmm.com", "https://us2.ninjarmm.com"];

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (string Key, string Token, DateTimeOffset ExpiresAt)? _token;

    public string Protect(string secret) => dataProtection.CreateProtector(SecretPurpose).Protect(secret);

    public static Dictionary<string, string> DefaultFieldMap() => Metrics.ToDictionary(m => m.Id, m => m.DefaultField);

    public async Task<NinjaSettings> SettingsAsync(CancellationToken ct = default)
    {
        var s = await db.Ninja.FirstOrDefaultAsync(ct);
        if (s is not null) return s;
        s = new NinjaSettings { FieldMap = DefaultFieldMap() };
        db.Ninja.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    public static void ForgetToken() => _token = null;

    private async Task<HttpClient> ClientAsync(NinjaSettings s, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(s.ClientId) || s.ClientSecretProtected is null)
            throw ApiException.Conflict("ninja_missing", "Bitte Client-ID und Client-Secret der NinjaOne-API eintragen.");
        var client = http.CreateClient(WebhookService.HttpClientName);
        var baseUrl = s.BaseUrl.TrimEnd('/');
        client.BaseAddress = new Uri(baseUrl + "/");
        var cacheKey = baseUrl + "|" + s.ClientId;
        if (_token is not { } t || t.Key != cacheKey || t.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            var secret = dataProtection.CreateProtector(SecretPurpose).Unprotect(s.ClientSecretProtected);
            using var res = await client.PostAsync("ws/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = s.ClientId!, ["client_secret"] = secret, ["scope"] = "monitoring management",
            }), ct);
            if (!res.IsSuccessStatusCode)
                throw ApiException.Conflict("ninja_auth", $"Anmeldung bei NinjaOne fehlgeschlagen (HTTP {(int)res.StatusCode}). Bitte Region, Client-ID und Secret prüfen; die API-App braucht den Grant „Client Credentials“ und die Scopes Monitoring und Management.");
            var json = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
            var token = json.GetProperty("access_token").GetString()!;
            var expires = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var sec) ? sec : 3600;
            _token = (cacheKey, token, DateTimeOffset.UtcNow.AddSeconds(expires));
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token!.Value.Token);
        return client;
    }

    public async Task<IReadOnlyList<NinjaOrganization>> OrganizationsAsync(CancellationToken ct = default)
    {
        var s = await SettingsAsync(ct);
        var client = await ClientAsync(s, ct);
        using var res = await client.GetAsync("v2/organizations", ct);
        if (!res.IsSuccessStatusCode) throw ApiException.Conflict("ninja_error", $"NinjaOne lieferte HTTP {(int)res.StatusCode} beim Abruf der Organisationen.");
        var list = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return list.EnumerateArray().Select(o => new NinjaOrganization(o.GetProperty("id").GetInt64(), o.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
            .OrderBy(o => o.Name).ToList();
    }

    public async Task<Dictionary<string, object?>> ValuesAsync(Tenant t, CancellationToken ct = default)
    {
        var d = await dashboards.BuildAsync([t.Id], ct: ct);
        var todos = await db.Todos.CountAsync(x => x.TenantId == t.Id && x.Status == TodoStatus.Open, ct);
        var active = d.ByStatus.GetValueOrDefault("Active");
        var locked = d.ByStatus.GetValueOrDefault("Locked");
        int? without = d.TargetUsers is { } target ? Math.Max(0, target - d.UsersWithKey) : null;
        var summary = $"{active} aktiv · {d.InStock} im Lager · {locked} gesperrt" + (d.CoveragePercent is { } c ? $" · Abdeckung {c:0.#} %" : "")
                      + (d.VulnerableKeys > 0 ? $" · {d.VulnerableKeys} mit Sicherheitshinweis" : "") + (todos > 0 ? $" · {todos} offene ToDos" : "");
        return new Dictionary<string, object?>
        {
            ["summary"] = summary,
            ["keysActive"] = active,
            ["keysInStock"] = d.InStock,
            ["keysLocked"] = locked,
            ["usersWithKey"] = d.UsersWithKey,
            ["usersWithoutKey"] = without,
            ["coveragePercent"] = d.CoveragePercent is { } p ? Math.Round(p, 1) : null,
            ["vulnerableKeys"] = d.VulnerableKeys,
            ["openTodos"] = todos,
            ["lastSync"] = t.LastSyncAt is { } ls ? ls.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "noch nie",
        };
    }

    /// <summary>Writes the metrics of all mapped tenants (or one tenant) into their NinjaOne organization.</summary>
    public async Task<NinjaPushResult> PushAsync(Guid? onlyTenant = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var s = await SettingsAsync(ct);
            var tenants = await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active && t.NinjaOrganizationId != null && (onlyTenant == null || t.Id == onlyTenant)).ToListAsync(ct);
            var errors = new List<string>();
            var ok = 0;
            HttpClient? client = null;
            try { client = await ClientAsync(s, ct); }
            catch (ApiException ex) { errors.Add(ex.Message); }
            if (client is not null)
            {
                foreach (var t in tenants)
                {
                    var values = await ValuesAsync(t, ct);
                    var body = new Dictionary<string, object?>();
                    foreach (var (metric, value) in values)
                        if (s.FieldMap.TryGetValue(metric, out var field) && !string.IsNullOrWhiteSpace(field)) body[field.Trim()] = value;
                    if (body.Count == 0) { errors.Add("Keine Felder zugeordnet."); break; }
                    try
                    {
                        using var res = await client.PatchAsJsonAsync($"v2/organization/{t.NinjaOrganizationId}/custom-fields", body, ct);
                        if (res.IsSuccessStatusCode) ok++;
                        else
                        {
                            var detail = await res.Content.ReadAsStringAsync(ct);
                            errors.Add($"{t.DisplayName}: HTTP {(int)res.StatusCode}{(detail.Length is > 0 and < 300 ? " " + detail : "")}");
                        }
                    }
                    catch (HttpRequestException ex) { errors.Add($"{t.DisplayName}: {ex.Message}"); }
                }
            }
            if (onlyTenant is null)
            {
                var wasOk = s.LastRunSucceeded != false;
                s.LastRunAt = DateTimeOffset.UtcNow;
                s.LastRunSucceeded = errors.Count == 0;
                s.LastRunMessage = errors.Count == 0 ? $"{ok} Tenant(s) übertragen." : string.Join(" ", errors.Take(5));
                if (errors.Count > 0 && wasOk) audit.Add(AuditService.System, AuditActions.NinjaPushFailed, details: new { errors = errors.Take(10) });
                await db.SaveChangesAsync(ct);
            }
            if (errors.Count > 0) logger.LogInformation("NinjaOne push: {Errors}", string.Join(" | ", errors));
            return new NinjaPushResult(tenants.Count, ok, errors);
        }
        finally
        {
            Gate.Release();
        }
    }

    public async Task RunDueAsync(CancellationToken ct = default)
    {
        var s = await db.Ninja.AsNoTracking().FirstOrDefaultAsync(ct);
        if (s is null || !s.Enabled) return;
        if (s.LastRunAt is { } last && last.AddMinutes(Math.Max(s.IntervalMinutes, 15)) > DateTimeOffset.UtcNow) return;
        await PushAsync(ct: ct);
    }
}
