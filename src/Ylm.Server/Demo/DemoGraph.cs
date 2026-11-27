using System.Collections.Concurrent;
using System.Text.Json;
using Ylm.Core;
using Ylm.Server.Graph;
using Ylm.Server.Setup;

namespace Ylm.Server.Demo;

/// <summary>
/// Demo-Modus: erfundener Entra-Tenant im Speicher statt Microsoft Graph. Es geht keine Anfrage nach außen.
/// Inhalt (Personen, Methoden, Richtlinie) legt <see cref="DemoData"/> fest, Änderungen (Methoden entfernen, TAP,
/// „Jetzt beheben“) gelten bis zum Neustart des Servers.
/// </summary>
public sealed class DemoGraph(Guid tenantId, string displayName, string domain) : IGraphClient
{
    private readonly Lock _lock = new();
    public Guid TenantId { get; } = tenantId;
    public string DisplayName { get; } = displayName;
    public string Domain { get; } = domain;
    public List<GraphUser> Users { get; } = [];
    public HashSet<string> Admins { get; } = [];
    public Dictionary<string, List<string>> Groups { get; } = [];
    public Dictionary<string, string> GroupNames { get; } = [];
    public ConcurrentDictionary<string, List<GraphFido2Method>> Fido2 { get; } = new();
    public ConcurrentDictionary<string, List<GraphAuthMethod>> Other { get; } = new();
    public Dictionary<string, DateTimeOffset> LastSignIn { get; } = [];
    public Fido2Policy Policy { get; set; } = new() { Enabled = true, IncludeTargets = [new PolicyTarget(PolicyTarget.AllUsers)] };
    public GraphTapPolicy TapPolicy { get; set; } = new(true, 60, 60, 480, false, false, 1);

    public Task<GraphOrganization> GetOrganizationAsync(CancellationToken ct = default) =>
        Task.FromResult(new GraphOrganization(TenantId.ToString(), DisplayName, Domain));

    public Task<byte[]?> GetUserPhotoAsync(string userId, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

    public Task<IReadOnlyList<string>> GetGrantedApplicationPermissionsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([.. SetupService.RequiredApplicationPermissions]);

    public Task<Fido2Policy> GetFido2PolicyAsync(CancellationToken ct = default) => Task.FromResult(Policy);

    public Task UpdateFido2PolicyAsync(Fido2Policy policy, CancellationToken ct = default)
    {
        Policy = policy;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphUser>> SearchUsersAsync(string query, int top = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphUser>>(Users.Where(u => u.UserPrincipalName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                                                   || (u.DisplayName ?? "").Contains(query, StringComparison.OrdinalIgnoreCase)).Take(top).ToList());

    public Task<GraphUser?> GetUserAsync(string idOrUpn, CancellationToken ct = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Id == idOrUpn || u.UserPrincipalName.Equals(idOrUpn, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<GraphGroup>> SearchGroupsAsync(string query, int top = 20, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphGroup>>(GroupNames.Where(g => g.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(g => new GraphGroup(g.Key, g.Value)).Take(top).ToList());

    public Task<GraphGroup?> GetGroupAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(GroupNames.TryGetValue(id, out var name) ? new GraphGroup(id, name) : null);

    private IEnumerable<GraphUser> Members(string? groupId) =>
        groupId is null || groupId == PolicyTarget.AllUsers
            ? Users.Where(u => u.AccountEnabled)
            : Users.Where(u => u.AccountEnabled && Groups.GetValueOrDefault(groupId)?.Contains(u.Id) == true);

    public Task<int?> CountUsersAsync(string? groupId, CancellationToken ct = default) => Task.FromResult<int?>(Members(groupId).Count());

    public Task<IReadOnlySet<string>> ListUserIdsAsync(string? groupId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<string>>(Members(groupId).Select(u => u.Id).ToHashSet());

    public Task<IReadOnlyList<GraphUser>?> ListGroupUsersAsync(string groupId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphUser>?>(Groups.ContainsKey(groupId) ? Members(groupId).ToList() : null);

    public Task<IReadOnlyList<string>> CheckMemberGroupsAsync(string userId, IReadOnlyCollection<string> groupIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(groupIds.Where(g => Groups.GetValueOrDefault(g)?.Contains(userId) == true).ToList());

    public async IAsyncEnumerable<string> ListUserIdsWithPasskeysAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        List<string> ids;
        lock (_lock) ids = Fido2.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList();
        foreach (var id in ids) yield return id;
        await Task.CompletedTask;
    }

    public Task<IReadOnlyList<GraphFido2Method>> ListFido2MethodsAsync(string userId, CancellationToken ct = default)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<GraphFido2Method>>(Fido2.GetValueOrDefault(userId)?.ToList() ?? []);
    }

    public Task DeleteFido2MethodAsync(string userId, string methodId, CancellationToken ct = default)
    {
        lock (_lock) Fido2.GetValueOrDefault(userId)?.RemoveAll(m => m.Id == methodId);
        return Task.CompletedTask;
    }

    public Task<CreationOptions> GetCreationOptionsAsync(string userId, int challengeTimeoutMinutes = 5, CancellationToken ct = default)
    {
        var user = Users.FirstOrDefault(u => u.Id == userId) ?? throw new GraphException("Benutzer nicht gefunden", 404);
        var json = JsonSerializer.Serialize(new
        {
            challenge = B64(Guid.NewGuid().ToByteArray()),
            timeout = 300000,
            attestation = "direct",
            rp = new { id = "login.microsoft.com", name = "Microsoft" },
            user = new { id = B64(System.Text.Encoding.UTF8.GetBytes(user.Id)), name = user.UserPrincipalName, displayName = user.DisplayName },
            pubKeyCredParams = new[] { new { type = "public-key", alg = -7 }, new { type = "public-key", alg = -257 } },
            excludeCredentials = Array.Empty<object>(),
            authenticatorSelection = new { authenticatorAttachment = "cross-platform", requireResidentKey = true, userVerification = "required" },
        });
        using var doc = JsonDocument.Parse(json);
        return Task.FromResult(new CreationOptions(doc.RootElement.Clone(), DateTimeOffset.UtcNow.AddMinutes(challengeTimeoutMinutes)));
    }

    public Task<GraphFido2Method> RegisterFido2MethodAsync(string userId, string displayName, NewFido2Credential credential, CancellationToken ct = default)
    {
        var method = new GraphFido2Method("demo-" + Guid.NewGuid().ToString("N")[..12], displayName, DateTimeOffset.UtcNow, DemoData.Aaguid57Nfc, "YubiKey 5 Series with NFC");
        lock (_lock) Fido2.GetOrAdd(userId, _ => []).Add(method);
        return Task.FromResult(method);
    }

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private List<GraphAuthMethod> AllMethods(string userId)
    {
        lock (_lock)
            return
            [
                new GraphAuthMethod("pw-" + userId, "password", "password", null, null, null),
                .. (Fido2.GetValueOrDefault(userId) ?? []).Select(m => new GraphAuthMethod(m.Id, "fido2", AuthMethodCatalog.Fido2ReportName(m.AaGuid, m.Model), m.DisplayName, m.Model, m.CreatedDateTime)),
                .. Other.GetValueOrDefault(userId) ?? [],
            ];
    }

    public Task<GraphRegistrationResult> ListUserRegistrationDetailsAsync(CancellationToken ct = default) =>
        Task.FromResult(new GraphRegistrationResult(Users.Select(u => new GraphUserRegistration(u.Id, u.UserPrincipalName, u.DisplayName, "member", Admins.Contains(u.Id),
            AllMethods(u.Id).Select(m => m.ReportName).Distinct().ToList(), null, DateTimeOffset.UtcNow)).ToList(), true));

    public Task<IReadOnlyList<GraphDirectoryUser>> ListDirectoryUsersAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphDirectoryUser>>(Users.Select(u => new GraphDirectoryUser(u.Id, u.UserPrincipalName, u.DisplayName, u.Mail, u.Department,
            u.AccountEnabled, "Member", LastSignIn.TryGetValue(u.Id, out var s) ? s : null)).ToList());

    public Task<IReadOnlyList<GraphAuthMethod>> ListAuthenticationMethodsAsync(string userId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GraphAuthMethod>>(AllMethods(userId));

    public Task DeleteAuthenticationMethodAsync(string userId, string kind, string methodId, CancellationToken ct = default)
    {
        if (AuthMethodCatalog.DeleteSegment(kind) is null) throw new ArgumentException(kind);
        lock (_lock)
        {
            Other.GetValueOrDefault(userId)?.RemoveAll(m => m.Id == methodId);
            if (kind == "fido2") Fido2.GetValueOrDefault(userId)?.RemoveAll(m => m.Id == methodId);
        }
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
        var id = "tap-" + Guid.NewGuid().ToString("N")[..8];
        var pass = "Demo" + Random.Shared.Next(100000, 999999);
        lock (_lock)
        {
            if (Other.GetValueOrDefault(userId)?.Any(m => m.Kind == "temporaryAccessPass") == true)
                throw new GraphException("Microsoft Graph meldet 400: User already has a Temporary Access Pass", 400, "badRequest");
            Other.GetOrAdd(userId, _ => []).Add(new GraphAuthMethod(id, "temporaryAccessPass", "temporaryAccessPass", null, "gültig", DateTimeOffset.UtcNow));
        }
        return Task.FromResult(new GraphTemporaryAccessPass(id, pass, startDateTime ?? DateTimeOffset.UtcNow, lifetimeInMinutes ?? TapPolicy.DefaultLifetimeInMinutes, isUsableOnce ?? false));
    }

    public Task<bool?> IsAdminAsync(string userId, CancellationToken ct = default) => Task.FromResult<bool?>(Admins.Contains(userId));

    public Task<string?> GetApplicationObjectIdAsync(Guid appId, CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task AddApplicationCertificateAsync(string applicationObjectId, byte[] certificateDer, string displayName, string proof, CancellationToken ct = default) =>
        throw new GraphException("Im Demo-Modus nicht verfügbar.", 400);
}

/// <summary>Liefert für jeden Tenant der Demo dessen <see cref="DemoGraph"/>. Unbekannte Tenants bekommen einen kleinen erfundenen Tenant.</summary>
public sealed class DemoGraphFactory : IGraphClientFactory
{
    private readonly ConcurrentDictionary<Guid, DemoGraph> _tenants = new();

    public DemoGraphFactory()
    {
        foreach (var g in DemoData.BuildTenants()) _tenants[g.TenantId] = g;
    }

    public IReadOnlyCollection<DemoGraph> Tenants => [.. _tenants.Values.OrderBy(t => t.DisplayName)];

    public DemoGraph Get(Guid entraTenantId) => _tenants.GetOrAdd(entraTenantId, id => DemoData.BuildSmallTenant(id));

    public Task<IGraphClient> ForTenantAsync(Guid entraTenantId, CancellationToken ct = default) => Task.FromResult<IGraphClient>(Get(entraTenantId));

    public void Invalidate(Guid entraTenantId) { }

    public Task TestCertificateAsync(Guid entraTenantId, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, CancellationToken ct = default) => Task.CompletedTask;

    public void Reset() { }
}
