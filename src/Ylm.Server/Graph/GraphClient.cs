using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ylm.Core;

namespace Ylm.Server.Graph;

/// <summary>Microsoft Graph operations for one customer tenant, using the installation's app identity.</summary>
public interface IGraphClient
{
    Task<GraphOrganization> GetOrganizationAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetGrantedApplicationPermissionsAsync(CancellationToken ct = default);
    Task<Fido2Policy> GetFido2PolicyAsync(CancellationToken ct = default);
    Task UpdateFido2PolicyAsync(Fido2Policy policy, CancellationToken ct = default);
    Task<IReadOnlyList<GraphUser>> SearchUsersAsync(string query, int top = 20, CancellationToken ct = default);
    Task<GraphUser?> GetUserAsync(string idOrUpn, CancellationToken ct = default);
    Task<IReadOnlyList<GraphGroup>> SearchGroupsAsync(string query, int top = 20, CancellationToken ct = default);
    Task<GraphGroup?> GetGroupAsync(string id, CancellationToken ct = default);
    Task<int?> CountUsersAsync(string? groupId, CancellationToken ct = default);
    /// <summary>Ids of the enabled users in a group (transitive) or, for null/all_users, in the whole tenant.</summary>
    Task<IReadOnlySet<string>> ListUserIdsAsync(string? groupId, CancellationToken ct = default);
    /// <summary>Which of the given groups the user is a member of (transitive).</summary>
    Task<IReadOnlyList<string>> CheckMemberGroupsAsync(string userId, IReadOnlyCollection<string> groupIds, CancellationToken ct = default);
    IAsyncEnumerable<string> ListUserIdsWithPasskeysAsync(CancellationToken ct = default);
    Task<IReadOnlyList<GraphFido2Method>> ListFido2MethodsAsync(string userId, CancellationToken ct = default);
    Task DeleteFido2MethodAsync(string userId, string methodId, CancellationToken ct = default);
    Task<CreationOptions> GetCreationOptionsAsync(string userId, int challengeTimeoutMinutes = 5, CancellationToken ct = default);
    Task<GraphFido2Method> RegisterFido2MethodAsync(string userId, string displayName, NewFido2Credential credential, CancellationToken ct = default);
}

public interface IGraphClientFactory
{
    Task<IGraphClient> ForTenantAsync(Guid entraTenantId, CancellationToken ct = default);

    /// <summary>
    /// Discards the cached credential (and with it the cached app-only token) for a tenant. Needed after a
    /// consent change: the token's "roles" claim only reflects permissions granted when it was issued.
    /// </summary>
    void Invalidate(Guid entraTenantId);
}

public delegate Task<string> GraphTokenProvider(CancellationToken ct);

public sealed class GraphClient(HttpClient http, GraphTokenProvider tokens, string baseUrl) : IGraphClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string V1 => baseUrl.TrimEnd('/') + "/v1.0";
    private string Beta => baseUrl.TrimEnd('/') + "/beta";
    private const string Fido2PolicyPath = "/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/fido2";

    private async Task<HttpRequestMessage> RequestAsync(HttpMethod method, string url, CancellationToken ct, object? body = null, bool eventual = false)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens(ct));
        if (eventual) req.Headers.Add("ConsistencyLevel", "eventual");
        if (body is not null)
            req.Content = new StringContent(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return req;
    }

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string url, CancellationToken ct, object? body = null, bool eventual = false, bool allowNotFound = false)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = await RequestAsync(method, url, ct, body, eventual);
            using var res = await http.SendAsync(req, ct);
            if ((res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500) && attempt < 3)
            {
                var delay = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, ct);
                continue;
            }
            if (allowNotFound && res.StatusCode == HttpStatusCode.NotFound) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
            {
                string? code = null, message = null, requestId = null;
                try
                {
                    using var err = JsonDocument.Parse(text);
                    var e = err.RootElement.GetProperty("error");
                    code = e.GetProperty("code").GetString();
                    message = e.GetProperty("message").GetString();
                    if (e.TryGetProperty("innerError", out var inner)) requestId = Str(inner, "request-id");
                }
                catch (Exception) { /* not a Graph error body */ }
                requestId ??= res.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null;
                throw new GraphException($"Microsoft Graph meldet {(int)res.StatusCode}: {message ?? res.ReasonPhrase}" +
                    (requestId is null ? "" : $" (request-id {requestId})"), (int)res.StatusCode, code);
            }
            return string.IsNullOrWhiteSpace(text) ? null : JsonDocument.Parse(text);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static GraphUser ToUser(JsonElement u) => new(
        Str(u, "id")!, Str(u, "userPrincipalName") ?? "", Str(u, "displayName"), Str(u, "mail"), Str(u, "department"),
        !u.TryGetProperty("accountEnabled", out var en) || en.ValueKind != JsonValueKind.False);

    private static GraphFido2Method ToMethod(JsonElement m) => new(
        Str(m, "id")!, Str(m, "displayName"),
        DateTimeOffset.TryParse(Str(m, "createdDateTime"), out var c) ? c : null,
        Guid.TryParse(Str(m, "aaGuid"), out var g) ? g : null, Str(m, "model"));

    public async Task<GraphOrganization> GetOrganizationAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{V1}/organization?$select=id,displayName,verifiedDomains", ct);
        var org = doc!.RootElement.GetProperty("value")[0];
        string? domain = null;
        if (org.TryGetProperty("verifiedDomains", out var domains))
        {
            foreach (var d in domains.EnumerateArray())
            {
                if (d.TryGetProperty("isDefault", out var isDef) && isDef.ValueKind == JsonValueKind.True) domain = Str(d, "name");
            }
        }
        return new GraphOrganization(Str(org, "id")!, Str(org, "displayName") ?? "", domain);
    }

    public async Task<IReadOnlyList<string>> GetGrantedApplicationPermissionsAsync(CancellationToken ct = default)
    {
        // The "roles" claim of the app-only access token lists the consented application permissions.
        var token = await tokens(ct);
        var parts = token.Split('.');
        if (parts.Length < 2) return [];
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.TryGetProperty("roles", out var roles)
            ? roles.EnumerateArray().Select(r => r.GetString()!).ToList()
            : [];
    }

    public async Task<Fido2Policy> GetFido2PolicyAsync(CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, V1 + Fido2PolicyPath, ct);
        return ParsePolicy(doc!.RootElement);
    }

    public static Fido2Policy ParsePolicy(JsonElement p)
    {
        static List<PolicyTarget> Targets(JsonElement p, string name) =>
            p.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Select(t => new PolicyTarget(Str(t, "id") ?? "", Str(t, "targetType") ?? "group",
                    t.TryGetProperty("isRegistrationRequired", out var r) && r.ValueKind == JsonValueKind.True)).ToList()
                : [];

        var restrictions = p.TryGetProperty("keyRestrictions", out var kr) && kr.ValueKind == JsonValueKind.Object ? kr : default;
        var aaguids = restrictions.ValueKind == JsonValueKind.Object && restrictions.TryGetProperty("aaGuids", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => Guid.TryParse(x.GetString(), out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList()
            : [];
        return new Fido2Policy
        {
            Enabled = Str(p, "state") == "enabled",
            IsSelfServiceRegistrationAllowed = !p.TryGetProperty("isSelfServiceRegistrationAllowed", out var ss) || ss.ValueKind != JsonValueKind.False,
            IsAttestationEnforced = p.TryGetProperty("isAttestationEnforced", out var att) && att.ValueKind == JsonValueKind.True,
            KeyRestrictionsEnforced = restrictions.ValueKind == JsonValueKind.Object && restrictions.TryGetProperty("isEnforced", out var ie) && ie.ValueKind == JsonValueKind.True,
            EnforcementType = restrictions.ValueKind == JsonValueKind.Object ? Str(restrictions, "enforcementType") ?? "allow" : "allow",
            AaGuids = aaguids,
            IncludeTargets = Targets(p, "includeTargets"),
            ExcludeTargets = Targets(p, "excludeTargets"),
        };
    }

    /// <summary>
    /// Builds a PATCH body that only contains the properties that actually change, based on the raw policy as Graph
    /// returned it. Unchanged properties are left out and existing targets keep all their fields (including ones this
    /// platform does not know, e.g. passkey profiles). Sending the complete object made Graph fail with
    /// "500 Internal error: Nullable object must have a value." Returns null when nothing changes.
    /// </summary>
    public static JsonObject? PolicyPatch(JsonElement raw, Fido2Policy after)
    {
        var before = ParsePolicy(raw);
        var patch = new JsonObject { ["@odata.type"] = "#microsoft.graph.fido2AuthenticationMethodConfiguration" };

        if (before.Enabled != after.Enabled) patch["state"] = after.Enabled ? "enabled" : "disabled";

        if (before.KeyRestrictionsEnforced != after.KeyRestrictionsEnforced || before.EnforcementType != after.EnforcementType
            || !before.AaGuids.SequenceEqual(after.AaGuids))
        {
            var kr = raw.TryGetProperty("keyRestrictions", out var k) && k.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(k.GetRawText())!.AsObject()
                : new JsonObject();
            kr["isEnforced"] = after.KeyRestrictionsEnforced;
            kr["enforcementType"] = after.EnforcementType;
            kr["aaGuids"] = new JsonArray(after.AaGuids.Select(g => (JsonNode)g.ToString()).ToArray());
            patch["keyRestrictions"] = kr;
        }

        static bool SameTargets(IReadOnlyList<PolicyTarget> a, IReadOnlyList<PolicyTarget> b) =>
            a.Select(t => t.Id).OrderBy(x => x).SequenceEqual(b.Select(t => t.Id).OrderBy(x => x));

        JsonArray Targets(string name, IReadOnlyList<PolicyTarget> targets, bool include)
        {
            var existing = new Dictionary<string, JsonElement>();
            if (raw.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var t in arr.EnumerateArray())
                    if (Str(t, "id") is { } id) existing[id] = t;
            return new JsonArray(targets.Select(t =>
            {
                if (existing.TryGetValue(t.Id, out var e)) return JsonNode.Parse(e.GetRawText());
                var o = new JsonObject { ["targetType"] = t.TargetType, ["id"] = t.Id };
                if (include) o["isRegistrationRequired"] = t.IsRegistrationRequired;
                return (JsonNode)o;
            }).ToArray());
        }

        if (!SameTargets(before.IncludeTargets, after.IncludeTargets)) patch["includeTargets"] = Targets("includeTargets", after.IncludeTargets, true);
        if (!SameTargets(before.ExcludeTargets, after.ExcludeTargets)) patch["excludeTargets"] = Targets("excludeTargets", after.ExcludeTargets, false);

        return patch.Count > 1 ? patch : null;
    }

    public async Task UpdateFido2PolicyAsync(Fido2Policy policy, CancellationToken ct = default)
    {
        using var current = await SendAsync(HttpMethod.Get, V1 + Fido2PolicyPath, ct);
        var patch = PolicyPatch(current!.RootElement, policy);
        if (patch is null) return;
        using var _ = await SendAsync(HttpMethod.Patch, V1 + Fido2PolicyPath, ct, patch);
    }

    public async Task<IReadOnlyList<GraphUser>> SearchUsersAsync(string query, int top = 20, CancellationToken ct = default)
    {
        var q = query.Replace("\"", "").Replace("\\", "").Trim();
        if (q.Length == 0) return [];
        var search = Uri.EscapeDataString($"\"displayName:{q}\" OR \"userPrincipalName:{q}\" OR \"mail:{q}\"");
        using var doc = await SendAsync(HttpMethod.Get,
            $"{V1}/users?$search={search}&$top={top}&$select=id,userPrincipalName,displayName,mail,department,accountEnabled&$count=true", ct, eventual: true);
        return doc!.RootElement.GetProperty("value").EnumerateArray().Select(ToUser).ToList();
    }

    public async Task<GraphUser?> GetUserAsync(string idOrUpn, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get,
            $"{V1}/users/{Uri.EscapeDataString(idOrUpn)}?$select=id,userPrincipalName,displayName,mail,department,accountEnabled", ct, allowNotFound: true);
        return doc is null ? null : ToUser(doc.RootElement);
    }

    public async Task<IReadOnlyList<GraphGroup>> SearchGroupsAsync(string query, int top = 20, CancellationToken ct = default)
    {
        var q = query.Replace("\"", "").Replace("\\", "").Trim();
        if (q.Length == 0) return [];
        var search = Uri.EscapeDataString($"\"displayName:{q}\"");
        using var doc = await SendAsync(HttpMethod.Get, $"{V1}/groups?$search={search}&$top={top}&$select=id,displayName", ct, eventual: true);
        return doc!.RootElement.GetProperty("value").EnumerateArray().Select(g => new GraphGroup(Str(g, "id")!, Str(g, "displayName") ?? "")).ToList();
    }

    public async Task<GraphGroup?> GetGroupAsync(string id, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{V1}/groups/{Uri.EscapeDataString(id)}?$select=id,displayName", ct, allowNotFound: true);
        return doc is null ? null : new GraphGroup(Str(doc.RootElement, "id")!, Str(doc.RootElement, "displayName") ?? "");
    }

    public async Task<int?> CountUsersAsync(string? groupId, CancellationToken ct = default)
    {
        var url = string.IsNullOrEmpty(groupId) || groupId == PolicyTarget.AllUsers
            ? $"{V1}/users/$count?$filter=accountEnabled eq true"
            : $"{V1}/groups/{Uri.EscapeDataString(groupId)}/transitiveMembers/microsoft.graph.user/$count";
        using var req = await RequestAsync(HttpMethod.Get, url, ct, eventual: true);
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;
        var text = await res.Content.ReadAsStringAsync(ct);
        return int.TryParse(text.Trim().TrimStart('﻿'), out var n) ? n : null;
    }

    public async Task<IReadOnlySet<string>> ListUserIdsAsync(string? groupId, CancellationToken ct = default)
    {
        var all = string.IsNullOrEmpty(groupId) || groupId == PolicyTarget.AllUsers;
        string? url = all
            ? $"{V1}/users?$select=id,accountEnabled&$top=999"
            : $"{V1}/groups/{Uri.EscapeDataString(groupId!)}/transitiveMembers/microsoft.graph.user?$select=id,accountEnabled&$top=999";
        var ids = new HashSet<string>();
        while (url is not null)
        {
            using var doc = await SendAsync(HttpMethod.Get, url, ct, allowNotFound: !all);
            if (doc is null) break; // group deleted in Entra
            foreach (var u in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (u.TryGetProperty("accountEnabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                if (Str(u, "id") is { } id) ids.Add(id);
            }
            url = Str(doc.RootElement, "@odata.nextLink");
        }
        return ids;
    }

    public async Task<IReadOnlyList<string>> CheckMemberGroupsAsync(string userId, IReadOnlyCollection<string> groupIds, CancellationToken ct = default)
    {
        if (groupIds.Count == 0) return [];
        var result = new List<string>();
        // Graph accepts at most 20 group ids per call.
        foreach (var chunk in groupIds.Chunk(20))
        {
            using var doc = await SendAsync(HttpMethod.Post, $"{V1}/users/{Uri.EscapeDataString(userId)}/checkMemberGroups", ct, new { groupIds = chunk });
            result.AddRange(doc!.RootElement.GetProperty("value").EnumerateArray().Select(v => v.GetString()!).Where(v => v is not null));
        }
        return result;
    }

    public async IAsyncEnumerable<string> ListUserIdsWithPasskeysAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        string? url = $"{V1}/reports/authenticationMethods/userRegistrationDetails?$select=id,methodsRegistered&$top=999";
        JsonDocument? first;
        try
        {
            first = await SendAsync(HttpMethod.Get, url, ct);
        }
        catch (GraphException ex) when (ex.StatusCode == 403 && IsMissingPremiumLicense(ex))
        {
            first = null;
        }

        if (first is null)
        {
            // The registration report needs Entra ID P1/P2. Without it, check every user's FIDO2 methods directly.
            await foreach (var id in ListUserIdsWithFido2MethodsSlowAsync(ct)) yield return id;
            yield break;
        }

        var doc = first;
        while (true)
        {
            using (doc)
            {
                foreach (var u in doc.RootElement.GetProperty("value").EnumerateArray())
                {
                    if (!u.TryGetProperty("methodsRegistered", out var methods)) continue;
                    if (methods.EnumerateArray().Any(m => m.GetString() is "passKeyDeviceBound" or "fido2" or "passKeyDeviceBoundAuthenticator"))
                        yield return Str(u, "id")!;
                }
                url = Str(doc.RootElement, "@odata.nextLink");
            }
            if (url is null) yield break;
            doc = (await SendAsync(HttpMethod.Get, url, ct))!;
        }
    }

    /// <summary>Graph answers 403 "Tenant is not a B2C tenant and doesn't have premium license" for reports without P1.</summary>
    public static bool IsMissingPremiumLicense(GraphException ex) =>
        ex.Message.Contains("premium license", StringComparison.OrdinalIgnoreCase) || ex.GraphCode == "Authentication_RequestFromNonPremiumTenantOrB2CTenant";

    private async IAsyncEnumerable<string> ListUserIdsWithFido2MethodsSlowAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        string? url = $"{V1}/users?$select=id&$top=999";
        while (url is not null)
        {
            var ids = new List<string>();
            using (var doc = await SendAsync(HttpMethod.Get, url, ct))
            {
                ids.AddRange(doc!.RootElement.GetProperty("value").EnumerateArray().Select(u => Str(u, "id")!));
                url = Str(doc.RootElement, "@odata.nextLink");
            }
            foreach (var id in ids)
            {
                using var methods = await SendAsync(HttpMethod.Get, $"{V1}/users/{Uri.EscapeDataString(id)}/authentication/fido2Methods", ct);
                if (methods!.RootElement.GetProperty("value").GetArrayLength() > 0) yield return id;
            }
        }
    }

    public async Task<IReadOnlyList<GraphFido2Method>> ListFido2MethodsAsync(string userId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"{V1}/users/{Uri.EscapeDataString(userId)}/authentication/fido2Methods", ct);
        return doc!.RootElement.GetProperty("value").EnumerateArray().Select(ToMethod).ToList();
    }

    public async Task DeleteFido2MethodAsync(string userId, string methodId, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Delete,
            $"{V1}/users/{Uri.EscapeDataString(userId)}/authentication/fido2Methods/{Uri.EscapeDataString(methodId)}", ct, allowNotFound: true);
    }

    public async Task<CreationOptions> GetCreationOptionsAsync(string userId, int challengeTimeoutMinutes = 5, CancellationToken ct = default)
    {
        // Provisioning on behalf is currently only available on the beta endpoint.
        using var doc = await SendAsync(HttpMethod.Get,
            $"{Beta}/users/{Uri.EscapeDataString(userId)}/authentication/fido2Methods/creationOptions(challengeTimeoutInMinutes={challengeTimeoutMinutes})", ct);
        var root = doc!.RootElement;
        var publicKey = root.GetProperty("publicKey").Clone();
        return new CreationOptions(publicKey, DateTimeOffset.TryParse(Str(root, "challengeTimeoutDateTime"), out var t) ? t : null);
    }

    public async Task<GraphFido2Method> RegisterFido2MethodAsync(string userId, string displayName, NewFido2Credential credential, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["displayName"] = displayName,
            ["publicKeyCredential"] = new JsonObject
            {
                ["id"] = credential.CredentialId,
                ["response"] = new JsonObject
                {
                    ["clientDataJSON"] = credential.ClientDataJson,
                    ["attestationObject"] = credential.AttestationObject,
                },
            },
        };
        using var doc = await SendAsync(HttpMethod.Post, $"{Beta}/users/{Uri.EscapeDataString(userId)}/authentication/fido2Methods", ct, body);
        return ToMethod(doc!.RootElement);
    }
}
