using System.Collections.Concurrent;
using System.Formats.Cbor;
using System.Text.Json;
using Ylm.Agent.Core;
using Ylm.Core;
using Ylm.Server.Graph;
using Ylm.Server.Setup;

namespace Ylm.Server.Tests;

/// <summary>In-memory stand-in for one Entra tenant's Graph API.</summary>
public sealed class FakeGraph : IGraphClient
{
    public Guid TenantId { get; } = Guid.NewGuid();
    public List<GraphUser> Users { get; } = [];
    public ConcurrentDictionary<string, List<GraphFido2Method>> Methods { get; } = new();
    public Fido2Policy Policy { get; set; } = new() { Enabled = true, IncludeTargets = [new PolicyTarget(PolicyTarget.AllUsers)] };
    public List<string> GrantedPermissions { get; set; } = [.. SetupService.RequiredApplicationPermissions];
    public List<(string UserId, NewFido2Credential Credential)> Registered { get; } = [];
    public string? LastChallenge { get; private set; }
    public int PolicyUpdates { get; private set; }

    public Task<GraphOrganization> GetOrganizationAsync(CancellationToken ct = default) =>
        Task.FromResult(new GraphOrganization(TenantId.ToString(), "Contoso GmbH", "contoso.com"));

    /// <summary>Simulates the app-only token cache: the roles claim is fixed when the token is first issued.</summary>
    public bool SimulateTokenCache { get; set; }
    public List<string>? TokenRoles { get; set; }

    public Task<IReadOnlyList<string>> GetGrantedApplicationPermissionsAsync(CancellationToken ct = default)
    {
        if (!SimulateTokenCache) return Task.FromResult<IReadOnlyList<string>>(GrantedPermissions);
        TokenRoles ??= [.. GrantedPermissions];
        return Task.FromResult<IReadOnlyList<string>>(TokenRoles);
    }

    public Task<Fido2Policy> GetFido2PolicyAsync(CancellationToken ct = default) => Task.FromResult(Policy);

    public Task UpdateFido2PolicyAsync(Fido2Policy policy, CancellationToken ct = default)
    {
        Policy = policy;
        PolicyUpdates++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphUser>> SearchUsersAsync(string query, int top = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphUser>>(Users.Where(u => u.UserPrincipalName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                                                   || (u.DisplayName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).Take(top).ToList());

    public Task<GraphUser?> GetUserAsync(string idOrUpn, CancellationToken ct = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == idOrUpn || u.UserPrincipalName.Equals(idOrUpn, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<GraphGroup>> SearchGroupsAsync(string query, int top = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphGroup>>([new GraphGroup("grp-yubikey", "YubiKey-Nutzer")]);

    public Task<GraphGroup?> GetGroupAsync(string id, CancellationToken ct = default) => Task.FromResult<GraphGroup?>(new GraphGroup(id, "Gruppe"));

    /// <summary>Group id → member user ids (already transitive).</summary>
    public Dictionary<string, List<string>> GroupMembers { get; } = [];

    public Task<int?> CountUsersAsync(string? groupId, CancellationToken ct = default) =>
        Task.FromResult<int?>(groupId is null || groupId == PolicyTarget.AllUsers ? Users.Count(u => u.AccountEnabled) : GroupMembers.GetValueOrDefault(groupId)?.Count ?? 0);

    public Task<IReadOnlySet<string>> ListUserIdsAsync(string? groupId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(groupId is null || groupId == PolicyTarget.AllUsers
            ? Users.Where(u => u.AccountEnabled).Select(u => u.Id).ToHashSet()
            : (GroupMembers.GetValueOrDefault(groupId) ?? []).ToHashSet());

    public Task<IReadOnlyList<GraphUser>?> ListGroupUsersAsync(string groupId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphUser>?>(GroupMembers.TryGetValue(groupId, out var ids) ? Users.Where(u => ids.Contains(u.Id)).ToList() : null);

    public Task<IReadOnlyList<string>> CheckMemberGroupsAsync(string userId, IReadOnlyCollection<string> groupIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(groupIds.Where(g => GroupMembers.GetValueOrDefault(g)?.Contains(userId) == true).ToList());

    public async IAsyncEnumerable<string> ListUserIdsWithPasskeysAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (user, methods) in Methods)
        {
            if (methods.Count > 0) yield return user;
        }
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphFido2Method>> ListFido2MethodsAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphFido2Method>>(Methods.GetValueOrDefault(userId)?.ToList() ?? []);

    public Task DeleteFido2MethodAsync(string userId, string methodId, CancellationToken ct = default)
    {
        Methods.GetValueOrDefault(userId)?.RemoveAll(m => m.Id == methodId);
        return Task.CompletedTask;
    }

    public Task<CreationOptions> GetCreationOptionsAsync(string userId, int challengeTimeoutMinutes = 5, CancellationToken ct = default)
    {
        var user = Users.First(u => u.Id == userId);
        LastChallenge = Base64Url.Encode(Guid.NewGuid().ToByteArray());
        var json = JsonSerializer.Serialize(new
        {
            challenge = LastChallenge,
            timeout = 300000,
            attestation = "direct",
            rp = new { id = "login.microsoft.com", name = "Microsoft" },
            user = new { id = Base64Url.Encode(System.Text.Encoding.UTF8.GetBytes(user.Id)), name = user.UserPrincipalName, displayName = user.DisplayName },
            pubKeyCredParams = new[] { new { type = "public-key", alg = -7 }, new { type = "public-key", alg = -257 } },
            excludeCredentials = Array.Empty<object>(),
            authenticatorSelection = new { authenticatorAttachment = "cross-platform", requireResidentKey = true, userVerification = "required" },
            extensions = new { hmacCreateSecret = true },
        });
        using var doc = JsonDocument.Parse(json);
        return Task.FromResult(new CreationOptions(doc.RootElement.Clone(), DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    public Task<GraphFido2Method> RegisterFido2MethodAsync(string userId, string displayName, NewFido2Credential credential, CancellationToken ct = default)
    {
        // Validate like Entra would: challenge, origin, rpIdHash.
        using var client = JsonDocument.Parse(Base64Url.Decode(credential.ClientDataJson));
        if (client.RootElement.GetProperty("challenge").GetString() != LastChallenge) throw new GraphException("challenge mismatch", 400);
        if (client.RootElement.GetProperty("origin").GetString() != "https://login.microsoft.com") throw new GraphException("origin mismatch", 400);
        var reader = new CborReader(Base64Url.Decode(credential.AttestationObject));
        reader.ReadStartMap();
        byte[]? authData = null;
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            if (key == "authData") authData = reader.ReadByteString(); else reader.SkipValue();
        }
        var expectedRp = System.Security.Cryptography.SHA256.HashData("login.microsoft.com"u8.ToArray());
        if (authData is null || !authData.AsSpan(0, 32).SequenceEqual(expectedRp)) throw new GraphException("rpId mismatch", 400);

        var aaguid = WebAuthnEncoding.AaguidFromAuthData(authData);
        var method = new GraphFido2Method("m-" + Guid.NewGuid().ToString("N")[..12], displayName, DateTimeOffset.UtcNow, aaguid, "YubiKey 5 Series with NFC");
        Methods.GetOrAdd(userId, _ => []).Add(method);
        Registered.Add((userId, credential));
        return Task.FromResult(method);
    }

    // ---- Passkey-Rollout ----
    /// <summary>Weitere Methoden pro Benutzer (zusätzlich zu den FIDO2-Methoden aus <see cref="Methods"/>).</summary>
    public ConcurrentDictionary<string, List<GraphAuthMethod>> OtherMethods { get; } = new();
    public bool ReportAvailable { get; set; } = true;
    public GraphTapPolicy TapPolicy { get; set; } = new(true, 60, 60, 480, false, false, 1);
    public List<(string UserId, string Pass)> Taps { get; } = [];

    private List<GraphAuthMethod> AllMethods(string userId) =>
    [
        new GraphAuthMethod("pw", "password", "password", null, null, null),
        .. (Methods.GetValueOrDefault(userId) ?? []).Select(m => new GraphAuthMethod(m.Id, "fido2", "passKeyDeviceBound", m.DisplayName, m.Model, m.CreatedDateTime)),
        .. OtherMethods.GetValueOrDefault(userId) ?? [],
    ];

    public Task<GraphRegistrationResult> ListUserRegistrationDetailsAsync(CancellationToken ct = default) =>
        Task.FromResult(new GraphRegistrationResult(Users.Select(u => new GraphUserRegistration(u.Id, u.UserPrincipalName, u.DisplayName, "member", false,
            AllMethods(u.Id).Select(m => m.ReportName).Distinct().ToList(), null, DateTimeOffset.UtcNow)).ToList(), ReportAvailable));

    public Task<IReadOnlyList<GraphDirectoryUser>> ListDirectoryUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphDirectoryUser>>(Users.Select(u => new GraphDirectoryUser(u.Id, u.UserPrincipalName, u.DisplayName, u.Mail, u.Department, u.AccountEnabled, "Member", null)).ToList());

    public Task<IReadOnlyList<GraphAuthMethod>> ListAuthenticationMethodsAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphAuthMethod>>(AllMethods(userId));

    public Task DeleteAuthenticationMethodAsync(string userId, string kind, string methodId, CancellationToken ct = default)
    {
        if (!AuthMethodCatalog.IsRemovable(kind)) throw new ArgumentException(kind);
        OtherMethods.GetValueOrDefault(userId)?.RemoveAll(m => m.Id == methodId);
        return Task.CompletedTask;
    }

    public Task<GraphTapPolicy> GetTapPolicyAsync(CancellationToken ct = default) => Task.FromResult(TapPolicy);

    public Task EnableTapPolicyAsync(CancellationToken ct = default)
    {
        TapPolicy = TapPolicy with { Enabled = true, TargetCount = Math.Max(1, TapPolicy.TargetCount) };
        return Task.CompletedTask;
    }

    public Task<GraphTemporaryAccessPass> CreateTemporaryAccessPassAsync(string userId, DateTimeOffset? startDateTime, int? lifetimeInMinutes, bool? isUsableOnce, CancellationToken ct = default)
    {
        if (!TapPolicy.Enabled) throw new GraphException("Microsoft Graph meldet 400: Temporary Access Pass policy is not enabled", 400, "badRequest");
        if (OtherMethods.GetValueOrDefault(userId)?.Any(m => m.Kind == "temporaryAccessPass") == true)
            throw new GraphException("Microsoft Graph meldet 400: User already has a Temporary Access Pass", 400, "badRequest");
        var id = "tap-" + Guid.NewGuid().ToString("N")[..8];
        var pass = "Tap" + Guid.NewGuid().ToString("N")[..8];
        OtherMethods.GetOrAdd(userId, _ => []).Add(new GraphAuthMethod(id, "temporaryAccessPass", "temporaryAccessPass", null, "gültig", DateTimeOffset.UtcNow));
        Taps.Add((userId, pass));
        return Task.FromResult(new GraphTemporaryAccessPass(id, pass, startDateTime ?? DateTimeOffset.UtcNow, lifetimeInMinutes ?? TapPolicy.DefaultLifetimeInMinutes, isUsableOnce ?? false));
    }
}

public sealed class FakeGraphFactory : IGraphClientFactory
{
    public ConcurrentDictionary<Guid, FakeGraph> Tenants { get; } = new();

    public Task<IGraphClient> ForTenantAsync(Guid entraTenantId, CancellationToken ct = default) =>
        Tenants.TryGetValue(entraTenantId, out var g) ? Task.FromResult<IGraphClient>(g) : throw new GraphException("unknown tenant", 401);

    public void Invalidate(Guid entraTenantId)
    {
        if (Tenants.TryGetValue(entraTenantId, out var g)) g.TokenRoles = null;
    }
}
