using System.Security.Cryptography;
using System.Text;

namespace Ylm.Core;

/// <summary>
/// Hash chain over audit entries: each entry stores SHA-256(previousHash | canonical content).
/// Changing or deleting an entry in the database breaks the chain, which the verification detects.
/// </summary>
public static class AuditChain
{
    public const string Genesis = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(string previousHash, long sequence, DateTimeOffset timestamp, string? actor,
        string action, Guid? tenantId, Guid? keyId, string? target, string? details)
    {
        var canonical = string.Join('\u001f', previousHash, sequence.ToString(),
            Normalize(timestamp).ToString("O"), actor ?? "", action, tenantId?.ToString() ?? "",
            keyId?.ToString() ?? "", target ?? "", details ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>PostgreSQL stores microseconds; truncate so hashes survive a database round trip.</summary>
    public static DateTimeOffset Normalize(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);
    }
}
