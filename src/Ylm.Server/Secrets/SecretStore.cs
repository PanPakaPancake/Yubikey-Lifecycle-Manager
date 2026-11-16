using Microsoft.AspNetCore.DataProtection;

namespace Ylm.Server.Secrets;

/// <summary>
/// Storage for secrets such as the app certificate. Exchangeable (planning 2.4); V1 ships a
/// file-based store encrypted with ASP.NET Data Protection. Vault/Azure Key Vault can implement this interface.
/// </summary>
public interface ISecretStore
{
    Task<byte[]?> GetAsync(string name, CancellationToken ct = default);
    Task SetAsync(string name, byte[] value, CancellationToken ct = default);
    Task DeleteAsync(string name, CancellationToken ct = default);
}

public sealed class FileSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly IDataProtector _protector;

    public FileSecretStore(string directory, IDataProtectionProvider provider)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        _protector = provider.CreateProtector("Ylm.SecretStore.v1");
    }

    private string PathFor(string name)
    {
        if (name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new ArgumentException("Invalid secret name", nameof(name));
        return Path.Combine(_directory, name + ".secret");
    }

    public async Task<byte[]?> GetAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        return _protector.Unprotect(await File.ReadAllBytesAsync(path, ct));
    }

    public async Task SetAsync(string name, byte[] value, CancellationToken ct = default)
    {
        var path = PathFor(name);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, _protector.Protect(value), ct);
        File.Move(tmp, path, overwrite: true);
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
