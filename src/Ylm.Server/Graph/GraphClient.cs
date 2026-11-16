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
                string? code = null, message = null;
                try
                {
                    using var err = JsonDocument.Parse(text);
                    code = err.RootElement.GetProperty("error").GetProperty("code").GetString();
                    message = err.RootElement.GetProperty("error").GetProperty("message").GetString();
                }
                catch (Exception) { /* not a Graph error body */ }
                throw new GraphException($"Microsoft Graph meldet {(int)res.StatusCode}: {message ?? res.ReasonPhrase}", (int)res.StatusCode, code);
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

    public static JsonObject PolicyPatchBody(Fido2Policy policy) => new()
    {
        ["@odata.type"] = "#microsoft.graph.fido2AuthenticationMethodConfiguration",
        ["state"] = policy.Enabled ? "enabled" : "disabled",
        ["keyRestrictions"] = new JsonObject
        {
            ["isEnforced"] = policy.KeyRestrictionsEnforced,
            ["enforcementType"] = policy.EnforcementType,
            ["aaGuids"] = new JsonArray(policy.AaGuids.Select(g => (JsonNode)g.ToString()).ToArray()),
        },
        ["includeTargets"] = new JsonArray(policy.IncludeTargets.Select(t => (JsonNode)new JsonObject
        {
            ["targetType"] = t.TargetType, ["id"] = t.Id, ["isRegistrationRequired"] = t.IsRegistrationRequired,
        }).ToArray()),
        ["excludeTargets"] = new JsonArray(policy.ExcludeTargets.Select(t => (JsonNode)new JsonObject
        {
            ["targetType"] = t.TargetType, ["id"] = t.Id,
        }).ToArray()),
    };

    public async Task UpdateFido2PolicyAsync(Fido2Policy policy, CancellationToken ct = default)
    {
        using var _ = await SendAsync(HttpMethod.Patch, V1 + Fido2PolicyPath, ct, PolicyPatchBody(policy));
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
        while (url is not null)
        {
            using var doc = await SendAsync(HttpMethod.Get, url, ct);
            foreach (var u in doc!.RootElement.GetProperty("value").EnumerateArray())
            {
                if (!u.TryGetProperty("methodsRegistered", out var methods)) continue;
                if (methods.EnumerateArray().Any(m => m.GetString() is "passKeyDeviceBound" or "fido2" or "passKeyDeviceBoundAuthenticator"))
                    yield return Str(u, "id")!;
            }
            url = Str(doc.RootElement, "@odata.nextLink");
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
