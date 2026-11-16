using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ylm.Server.Secrets;

/// <summary>Creates and loads the certificate the installation's Entra app authenticates with.</summary>
public sealed class AppCertificateService(ISecretStore secrets)
{
    private const string SecretName = "app-certificate";
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
        using var rsa = RSA.Create(3072);
        var cn = new string(organizationName.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
        var request = new CertificateRequest($"CN=Yubikey Lifecycle Manager {cn}".Trim(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        var pfx = cert.Export(X509ContentType.Pkcs12);
        await secrets.SetAsync(SecretName, pfx, ct);
        _cached = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.EphemeralKeySet);
        return _cached;
    }

    /// <summary>For tests: inject an existing certificate.</summary>
    public async Task ImportAsync(X509Certificate2 cert, CancellationToken ct = default)
    {
        await secrets.SetAsync(SecretName, cert.Export(X509ContentType.Pkcs12), ct);
        _cached = null;
    }
}
