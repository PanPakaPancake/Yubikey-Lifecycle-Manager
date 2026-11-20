using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Setup;

public sealed record DeviceCodeStart(string DeviceCode, string UserCode, string VerificationUri, int ExpiresInSeconds, int IntervalSeconds);

public enum DeviceCodeStatus { Pending, SlowDown, SignedIn, Declined, Expired, Failed }

public sealed record DeviceCodePoll(DeviceCodeStatus Status, string? AccessToken = null, DateTimeOffset? ExpiresAt = null, string? Error = null);

public sealed record SignedInAdmin(string Upn, string DisplayName, Guid TenantId, string OrganizationName, string? DefaultDomain);

public sealed record AppProvisioningRequest(
    Guid? ExistingClientId,
    string DisplayName,
    IReadOnlyList<string> RedirectUris,
    byte[] CertificateDer,
    string CertificateName,
    IReadOnlyList<string> ApplicationPermissions,
    IReadOnlyList<string> DelegatedScopes);

public sealed record AppProvisioningResult(Guid ClientId, IReadOnlyList<string> Steps, string? ApplicationObjectId = null);

/// <summary>
/// Creates the installation's own app registration with a delegated admin token, before the installation has an app identity.
/// The sign-in uses Microsoft's public client "Microsoft Graph Command Line Tools" (the same app Connect-MgGraph uses):
/// interactively with authorization code + PKCE and the redirect URI http://localhost (registered for that client), or as a
/// fallback with the device code flow. Before setup there is no app of our own on which the platform's address is registered.
/// </summary>
public interface IEntraBootstrap
{
    /// <summary>Microsoft sign-in page for the interactive flow (authorization code with PKCE).</summary>
    string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge);
    /// <summary>Redeems the code from the redirect for the admin's delegated token.</summary>
    Task<DeviceCodePoll> RedeemCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct = default);
    Task<DeviceCodeStart> StartDeviceCodeAsync(CancellationToken ct = default);
    Task<DeviceCodePoll> PollDeviceCodeAsync(string deviceCode, CancellationToken ct = default);
    Task<SignedInAdmin> GetSignedInAdminAsync(string accessToken, CancellationToken ct = default);
    Task<AppProvisioningResult> ProvisionAppAsync(string accessToken, AppProvisioningRequest request, CancellationToken ct = default);
    /// <summary>Ergänzt Redirect-URIs der eigenen App-Registrierung (vorhandene bleiben). Gibt alle URIs danach zurück.</summary>
    Task<IReadOnlyList<string>> AddRedirectUrisAsync(string accessToken, Guid clientId, IReadOnlyList<string> redirectUris, CancellationToken ct = default);
}

/// <summary>Holds the pending device code and the admin token of the setup sign-in (memory only, never persisted).</summary>
public sealed class SetupSignInStore
{
    public sealed record PendingDeviceCode(string DeviceCode, string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, int IntervalSeconds, DateTimeOffset NextPollAt);
    public sealed record SignIn(string AccessToken, DateTimeOffset ExpiresAt, SignedInAdmin Admin);
    public sealed record PendingInteractive(string State, string CodeVerifier, string RedirectUri, DateTimeOffset ExpiresAt);

    private readonly Lock _lock = new();
    private PendingDeviceCode? _pending;
    private PendingInteractive? _interactive;
    private SignIn? _signIn;

    public PendingInteractive? Interactive
    {
        get { lock (_lock) return _interactive is { } p && p.ExpiresAt > DateTimeOffset.UtcNow ? p : null; }
        set { lock (_lock) _interactive = value; }
    }

    /// <summary>Takes the pending interactive sign-in if the state matches (single use).</summary>
    public PendingInteractive? TakeInteractive(string state)
    {
        lock (_lock)
        {
            if (_interactive is not { } p || p.ExpiresAt <= DateTimeOffset.UtcNow
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(p.State), System.Text.Encoding.UTF8.GetBytes(state ?? "")))
                return null;
            _interactive = null;
            return p;
        }
    }

    public PendingDeviceCode? Pending
    {
        get { lock (_lock) return _pending is { } p && p.ExpiresAt > DateTimeOffset.UtcNow ? p : null; }
        set { lock (_lock) _pending = value; }
    }

    /// <summary>The admin sign-in, as long as its token is valid for at least two more minutes.</summary>
    public SignIn? Current
    {
        get { lock (_lock) return _signIn is { } s && s.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2) ? s : null; }
        set { lock (_lock) _signIn = value; }
    }

    /// <summary>The last signed-in admin, also after the token expired (for display and defaults).</summary>
    public SignedInAdmin? LastAdmin { get { lock (_lock) return _signIn?.Admin; } }

    public void Clear()
    {
        lock (_lock)
        {
            _pending = null;
            _interactive = null;
            _signIn = null;
        }
    }
}

public sealed class EntraBootstrap(IHttpClientFactory httpFactory, IOptions<YlmOptions> options, ILogger<EntraBootstrap> logger) : IEntraBootstrap
{
    /// <summary>Microsoft Graph Command Line Tools, a Microsoft first-party public client available in every tenant.</summary>
    public const string BootstrapClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";
    public const string GraphAppId = "00000003-0000-0000-c000-000000000000";

    public static readonly string[] BootstrapScopes =
    [
        "Application.ReadWrite.All",
        "AppRoleAssignment.ReadWrite.All",
        "DelegatedPermissionGrant.ReadWrite.All",
        "User.Read",
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Authority => options.Value.LoginAuthority.TrimEnd('/') + "/organizations/oauth2/v2.0";
    private string GraphV1 => options.Value.GraphBaseUrl.TrimEnd('/') + "/v1.0";

    private HttpClient Http() => httpFactory.CreateClient("graph");

    private string ScopeParameter()
    {
        var graph = options.Value.GraphBaseUrl.TrimEnd('/');
        return string.Join(' ', BootstrapScopes.Select(s => $"{graph}/{s}"));
    }

    public string BuildAuthorizeUrl(string redirectUri, string state, string codeChallenge) =>
        Authority + "/authorize?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = BootstrapClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = ScopeParameter(),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        }.Select(kv => kv.Key + "=" + Uri.EscapeDataString(kv.Value)));

    public async Task<DeviceCodePoll> RedeemCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct = default)
    {
        using var res = await Http().PostAsync(Authority + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = BootstrapClientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = ScopeParameter(),
        }), ct);
        var doc = await ReadJsonAsync(res, ct);
        if (!res.IsSuccessStatusCode) return new DeviceCodePoll(DeviceCodeStatus.Failed, Error: ErrorText(doc));
        var r = doc!.RootElement;
        var expiresIn = r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        return new DeviceCodePoll(DeviceCodeStatus.SignedIn, r.GetProperty("access_token").GetString(), DateTimeOffset.UtcNow.AddSeconds(expiresIn));
    }

    public async Task<DeviceCodeStart> StartDeviceCodeAsync(CancellationToken ct = default)
    {
        var graph = options.Value.GraphBaseUrl.TrimEnd('/');
        var scope = string.Join(' ', BootstrapScopes.Select(s => $"{graph}/{s}"));
        using var res = await Http().PostAsync(Authority + "/devicecode", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = BootstrapClientId,
            ["scope"] = scope,
        }), ct);
        var doc = await ReadJsonAsync(res, ct);
        if (!res.IsSuccessStatusCode)
            throw ApiException.BadRequest("signin_failed", "Microsoft-Anmeldung konnte nicht gestartet werden: " + ErrorText(doc));
        var r = doc!.RootElement;
        return new DeviceCodeStart(
            r.GetProperty("device_code").GetString()!,
            r.GetProperty("user_code").GetString()!,
            r.TryGetProperty("verification_uri", out var uri) ? uri.GetString()! : "https://microsoft.com/devicelogin",
            r.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 900,
            r.TryGetProperty("interval", out var iv) ? iv.GetInt32() : 5);
    }

    public async Task<DeviceCodePoll> PollDeviceCodeAsync(string deviceCode, CancellationToken ct = default)
    {
        using var res = await Http().PostAsync(Authority + "/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["client_id"] = BootstrapClientId,
            ["device_code"] = deviceCode,
        }), ct);
        var doc = await ReadJsonAsync(res, ct);
        if (res.IsSuccessStatusCode)
        {
            var r = doc!.RootElement;
            var expiresIn = r.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            return new DeviceCodePoll(DeviceCodeStatus.SignedIn, r.GetProperty("access_token").GetString(), DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }
        string? error = null;
        if (doc is not null && doc.RootElement.TryGetProperty("error", out var err)) error = err.GetString();
        return error switch
        {
            "authorization_pending" => new DeviceCodePoll(DeviceCodeStatus.Pending),
            "slow_down" => new DeviceCodePoll(DeviceCodeStatus.SlowDown),
            "authorization_declined" => new DeviceCodePoll(DeviceCodeStatus.Declined, Error: "Die Anmeldung wurde abgelehnt."),
            "expired_token" or "code_expired" => new DeviceCodePoll(DeviceCodeStatus.Expired, Error: "Der Code ist abgelaufen. Bitte neu starten."),
            _ => new DeviceCodePoll(DeviceCodeStatus.Failed, Error: ErrorText(doc)),
        };
    }

    public async Task<SignedInAdmin> GetSignedInAdminAsync(string accessToken, CancellationToken ct = default)
    {
        using var me = await GraphAsync(accessToken, HttpMethod.Get, $"{GraphV1}/me?$select=userPrincipalName,displayName", ct);
        using var org = await GraphAsync(accessToken, HttpMethod.Get, $"{GraphV1}/organization?$select=id,displayName,verifiedDomains", ct);
        var o = org!.RootElement.GetProperty("value")[0];
        string? domain = null;
        if (o.TryGetProperty("verifiedDomains", out var domains))
        {
            foreach (var d in domains.EnumerateArray())
            {
                if (d.TryGetProperty("isDefault", out var isDef) && isDef.ValueKind == JsonValueKind.True) domain = d.GetProperty("name").GetString();
            }
        }
        return new SignedInAdmin(
            me!.RootElement.GetProperty("userPrincipalName").GetString() ?? "",
            me.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            Guid.Parse(o.GetProperty("id").GetString()!),
            o.TryGetProperty("displayName", out var odn) ? odn.GetString() ?? "" : "",
            domain);
    }

    public async Task<AppProvisioningResult> ProvisionAppAsync(string token, AppProvisioningRequest request, CancellationToken ct = default)
    {
        var steps = new List<string>();

        // Microsoft Graph's service principal holds the ids of all permissions.
        using var graphSpDoc = await GraphAsync(token, HttpMethod.Get,
            $"{GraphV1}/servicePrincipals(appId='{GraphAppId}')?$select=id,appRoles,oauth2PermissionScopes", ct);
        var graphSp = graphSpDoc!.RootElement;
        var graphSpId = graphSp.GetProperty("id").GetString()!;
        var roleIds = request.ApplicationPermissions.Select(p => FindPermissionId(graphSp, "appRoles", p)).ToList();
        var scopeIds = request.DelegatedScopes.Select(p => FindPermissionId(graphSp, "oauth2PermissionScopes", p)).ToList();

        var access = new JsonArray();
        foreach (var id in roleIds) access.Add(new JsonObject { ["id"] = id, ["type"] = "Role" });
        foreach (var id in scopeIds) access.Add(new JsonObject { ["id"] = id, ["type"] = "Scope" });
        JsonObject AppBody() => new()
        {
            ["web"] = new JsonObject { ["redirectUris"] = new JsonArray(request.RedirectUris.Select(u => (JsonNode)u).ToArray()) },
            ["requiredResourceAccess"] = new JsonArray(new JsonObject { ["resourceAppId"] = GraphAppId, ["resourceAccess"] = access.DeepClone() }),
            ["keyCredentials"] = new JsonArray(new JsonObject
            {
                ["type"] = "AsymmetricX509Cert",
                ["usage"] = "Verify",
                ["key"] = Convert.ToBase64String(request.CertificateDer),
                ["displayName"] = request.CertificateName,
            }),
        };

        // 1. App registration: reuse the one from an earlier attempt, otherwise create it.
        string? appId = null, objectId = null;
        if (request.ExistingClientId is { } existing)
        {
            using var found = await GraphAsync(token, HttpMethod.Get, $"{GraphV1}/applications(appId='{existing}')?$select=id,appId", ct, allowNotFound: true);
            if (found is not null)
            {
                objectId = found.RootElement.GetProperty("id").GetString();
                await GraphAsync(token, HttpMethod.Patch, $"{GraphV1}/applications/{objectId}", ct, AppBody());
                appId = existing.ToString();
                steps.Add("Vorhandene App-Registrierung aktualisiert");
            }
        }
        if (appId is null)
        {
            var body = AppBody();
            body["displayName"] = request.DisplayName;
            body["signInAudience"] = "AzureADMultipleOrgs";
            using var created = await GraphAsync(token, HttpMethod.Post, $"{GraphV1}/applications", ct, body);
            appId = created!.RootElement.GetProperty("appId").GetString()!;
            objectId = created.RootElement.GetProperty("id").GetString();
            steps.Add("App-Registrierung angelegt");
        }
        steps.Add("Zertifikat hochgeladen");

        // 2. Service principal in the home tenant. Right after creating the app, Entra may need a few seconds.
        using var spDoc = await GraphAsync(token, HttpMethod.Get, $"{GraphV1}/servicePrincipals(appId='{appId}')?$select=id", ct, allowNotFound: true)
                          ?? await WithReplicationRetryAsync(() => GraphAsync(token, HttpMethod.Post, $"{GraphV1}/servicePrincipals", ct, new JsonObject { ["appId"] = appId }), ct);
        var spId = spDoc!.RootElement.GetProperty("id").GetString()!;
        steps.Add("Dienstprinzipal angelegt");

        // 3. Admin consent for the application permissions (app role assignments).
        using var assignedDoc = await GraphAsync(token, HttpMethod.Get, $"{GraphV1}/servicePrincipals/{spId}/appRoleAssignments?$select=appRoleId,resourceId", ct);
        var assigned = assignedDoc!.RootElement.GetProperty("value").EnumerateArray()
            .Where(a => a.GetProperty("resourceId").GetString() == graphSpId)
            .Select(a => a.GetProperty("appRoleId").GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var roleId in roleIds.Where(r => !assigned.Contains(r)))
        {
            using var _ = await WithReplicationRetryAsync(() => GraphAsync(token, HttpMethod.Post, $"{GraphV1}/servicePrincipals/{spId}/appRoleAssignments", ct,
                new JsonObject { ["principalId"] = spId, ["resourceId"] = graphSpId, ["appRoleId"] = roleId }), ct);
        }
        steps.Add($"Admin-Consent für {roleIds.Count} Anwendungsberechtigungen erteilt");

        // 4. Admin consent for the delegated sign-in scopes, for all users.
        var scope = string.Join(' ', request.DelegatedScopes);
        using var grants = await GraphAsync(token, HttpMethod.Get,
            $"{GraphV1}/oauth2PermissionGrants?$filter={Uri.EscapeDataString($"clientId eq '{spId}' and consentType eq 'AllPrincipals' and resourceId eq '{graphSpId}'")}", ct);
        var grant = grants!.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        if (grant.ValueKind == JsonValueKind.Object)
        {
            using var _ = await GraphAsync(token, HttpMethod.Patch, $"{GraphV1}/oauth2PermissionGrants/{grant.GetProperty("id").GetString()}", ct, new JsonObject { ["scope"] = scope });
        }
        else
        {
            using var _ = await WithReplicationRetryAsync(() => GraphAsync(token, HttpMethod.Post, $"{GraphV1}/oauth2PermissionGrants", ct,
                new JsonObject { ["clientId"] = spId, ["consentType"] = "AllPrincipals", ["resourceId"] = graphSpId, ["scope"] = scope }), ct);
        }
        steps.Add("Admin-Consent für die Anmeldung erteilt");

        logger.LogInformation("App-Registrierung {AppId} per Setup-Assistent eingerichtet", appId);
        return new AppProvisioningResult(Guid.Parse(appId), steps, objectId);
    }

    private static string FindPermissionId(JsonElement graphSp, string collection, string value)
    {
        foreach (var p in graphSp.GetProperty(collection).EnumerateArray())
        {
            if (p.GetProperty("value").GetString() == value) return p.GetProperty("id").GetString()!;
        }
        throw ApiException.BadRequest("permission_unknown", $"Berechtigung {value} wurde bei Microsoft Graph nicht gefunden.");
    }

    /// <summary>Entra replicates new objects with a delay; references to them fail with 400/404 for a few seconds.</summary>
    private static async Task<JsonDocument?> WithReplicationRetryAsync(Func<Task<JsonDocument?>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await action(); }
            catch (GraphException ex) when (attempt < 8 && ex.StatusCode is 400 or 404)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    public async Task<IReadOnlyList<string>> AddRedirectUrisAsync(string token, Guid clientId, IReadOnlyList<string> redirectUris, CancellationToken ct = default)
    {
        using var found = await GraphAsync(token, HttpMethod.Get, $"{GraphV1}/applications(appId='{clientId}')?$select=id,web", ct, allowNotFound: true)
                          ?? throw ApiException.Conflict("app_not_found", "Die App-Registrierung der Plattform wurde in diesem Tenant nicht gefunden. Bitte mit einem Admin des Tenants anmelden, in dem die Plattform registriert ist.");
        var objectId = found.RootElement.GetProperty("id").GetString()!;
        // web komplett zurückschreiben, damit Abmelde-URL und andere Einstellungen erhalten bleiben.
        var web = found.RootElement.TryGetProperty("web", out var w) && w.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(w.GetRawText())!.AsObject() : new JsonObject();
        var existing = web["redirectUris"] is JsonArray arr ? arr.Select(n => n!.GetValue<string>()).ToList() : [];
        var all = existing.Concat(redirectUris).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (all.Count != existing.Count)
        {
            web["redirectUris"] = new JsonArray(all.Select(u => (JsonNode)u).ToArray());
            web.Remove("redirectUriSettings");
            await GraphAsync(token, HttpMethod.Patch, $"{GraphV1}/applications/{objectId}", ct, new JsonObject { ["web"] = web });
            logger.LogInformation("Redirect-URIs der App {ClientId} ergänzt: {Uris}", clientId, string.Join(", ", redirectUris));
        }
        return all;
    }

    private async Task<JsonDocument?> GraphAsync(string token, HttpMethod method, string url, CancellationToken ct, JsonNode? body = null, bool allowNotFound = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null) req.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
            using var res = await Http().SendAsync(req, ct);
            if ((res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500) && attempt < 3)
            {
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                continue;
            }
            if (allowNotFound && res.StatusCode == HttpStatusCode.NotFound) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                string? code = null, message = null;
                try
                {
                    using var err = JsonDocument.Parse(text);
                    code = err.RootElement.GetProperty("error").GetProperty("code").GetString();
                    message = err.RootElement.GetProperty("error").GetProperty("message").GetString();
                }
                catch (Exception) { /* not a Graph error body */ }
                var hint = res.StatusCode == HttpStatusCode.Forbidden
                    ? " Das angemeldete Konto braucht die Rolle Globaler Administrator oder Privileged Role Administrator."
                    : "";
                throw new GraphException($"Microsoft Graph meldet {(int)res.StatusCode}: {message ?? res.ReasonPhrase}.{hint}", (int)res.StatusCode, code);
            }
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
        }
    }

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        try { return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    private static string ErrorText(JsonDocument? doc)
    {
        if (doc is null) return "unbekannter Fehler";
        var r = doc.RootElement;
        var description = r.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        var error = r.TryGetProperty("error", out var e) ? e.GetString() : null;
        // The first line of error_description carries the AADSTS code and the message; the rest is trace data.
        return description?.Split('\n', 2)[0].Trim() ?? error ?? "unbekannter Fehler";
    }
}
