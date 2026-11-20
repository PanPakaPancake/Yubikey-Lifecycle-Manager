using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Secrets;

namespace Ylm.Server.Graph;

/// <summary>Builds Graph clients that authenticate with the installation's app certificate (client credentials).</summary>
public sealed class GraphClientFactory(
    IHttpClientFactory httpFactory,
    IServiceScopeFactory scopes,
    AppCertificateService certificates,
    IOptions<YlmOptions> options) : IGraphClientFactory
{
    private readonly ConcurrentDictionary<Guid, TokenCredential> _credentials = new();

    public async Task<IGraphClient> ForTenantAsync(Guid entraTenantId, CancellationToken ct = default)
    {
        var credential = await GetCredentialAsync(entraTenantId, ct);
        var scope = options.Value.GraphBaseUrl.TrimEnd('/') + "/.default";
        GraphTokenProvider tokens = async c => (await credential.GetTokenAsync(new TokenRequestContext([scope]), c)).Token;
        return new GraphClient(httpFactory.CreateClient("graph"), tokens, options.Value.GraphBaseUrl);
    }

    private async Task<TokenCredential> GetCredentialAsync(Guid tenantId, CancellationToken ct)
    {
        if (_credentials.TryGetValue(tenantId, out var cached)) return cached;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YlmDbContext>();
        var settings = await db.Installation.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.ClientId is null) throw new ApiException(409, "setup_incomplete", "Die App-Registrierung der Installation ist noch nicht eingerichtet.");
        var cert = await certificates.GetAsync(ct) ?? throw new ApiException(409, "setup_incomplete", "Das App-Zertifikat fehlt.");
        var credential = new ClientCertificateCredential(tenantId.ToString(), settings.ClientId.Value.ToString(), cert,
            new ClientCertificateCredentialOptions { AuthorityHost = new Uri(options.Value.LoginAuthority), SendCertificateChain = false });
        return _credentials.GetOrAdd(tenantId, credential);
    }

    public async Task TestCertificateAsync(Guid entraTenantId, System.Security.Cryptography.X509Certificates.X509Certificate2 certificate, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<YlmDbContext>();
        var settings = await db.Installation.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings?.ClientId is null) throw new ApiException(409, "setup_incomplete", "Die App-Registrierung der Installation ist noch nicht eingerichtet.");
        var credential = new ClientCertificateCredential(entraTenantId.ToString(), settings.ClientId.Value.ToString(), certificate,
            new ClientCertificateCredentialOptions { AuthorityHost = new Uri(options.Value.LoginAuthority), SendCertificateChain = false });
        await credential.GetTokenAsync(new TokenRequestContext([options.Value.GraphBaseUrl.TrimEnd('/') + "/.default"]), ct);
    }

    public void Invalidate(Guid entraTenantId) => _credentials.TryRemove(entraTenantId, out _);

    public void Reset() => _credentials.Clear();
}
