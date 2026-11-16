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

    public Task<int?> CountUsersAsync(string? groupId, CancellationToken ct = default) => Task.FromResult<int?>(Users.Count);

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
