using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ylm.Server.Secrets;

/// <summary>Creates and loads the certificate the installation's Entra app authenticates with.</summary>
public sealed class AppCertificateService(ISecretStore secrets)
{
    private const string SecretName = "app-certificate";
    private const string NextSecretName = "app-certificate-next";
    private X509Certificate2? _cached;

    public async Task<X509Certificate2?> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null) return _cached;
        var pfx = await secrets.GetAsync(SecretName, ct);
        if (pfx is null) return null;
        _cached = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
        return _cached;
    }

    public async Task<X509Certificate2> CreateAsync(string organizationName, CancellationToken ct = default)
    {
        var pfx = NewPfx(organizationName);
        await secrets.SetAsync(SecretName, pfx, ct);
        _cached = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
        return _cached;
    }

    /// <summary>
    /// Nachfolger für die Erneuerung: liegt getrennt, bis Entra ihn annimmt. Erst dann ersetzt
    /// <see cref="PromoteNextAsync"/> das aktuelle Zertifikat.
    /// </summary>
    public async Task<X509Certificate2> CreateNextAsync(string organizationName, CancellationToken ct = default)
    {
        var pfx = NewPfx(organizationName);
        await secrets.SetAsync(NextSecretName, pfx, ct);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    public async Task<X509Certificate2?> GetNextAsync(CancellationToken ct = default)
    {
        var pfx = await secrets.GetAsync(NextSecretName, ct);
        return pfx is null ? null : X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
    }

    public async Task<X509Certificate2?> PromoteNextAsync(CancellationToken ct = default)
    {
        var pfx = await secrets.GetAsync(NextSecretName, ct);
        if (pfx is null) return null;
        await secrets.SetAsync(SecretName, pfx, ct);
        await secrets.DeleteAsync(NextSecretName, ct);
        _cached = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
        return _cached;
    }

    public Task DiscardNextAsync(CancellationToken ct = default) => secrets.DeleteAsync(NextSecretName, ct);

    private static byte[] NewPfx(string organizationName)
    {
        using var rsa = RSA.Create(3072);
        var cn = new string(organizationName.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        var request = new CertificateRequest($"CN=Yubikey Lifecycle Manager {cn}".Trim(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return cert.Export(X509ContentType.Pkcs12);
    }

    /// <summary>For tests: inject an existing certificate.</summary>
    public async Task ImportAsync(X509Certificate2 cert, CancellationToken ct = default)
    {
        await secrets.SetAsync(SecretName, cert.Export(X509ContentType.Pkcs12), ct);
        _cached = null;
    }
}
