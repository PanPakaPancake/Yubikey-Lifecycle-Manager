using System.Security.Cryptography;
using System.Text;

namespace Ylm.Server.Security;

/// <summary>Random high-entropy tokens stored as SHA-256 hashes (API keys, agent tokens, pairing codes).</summary>
public static class TokenHashing
{
    public static string NewSecret(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Human friendly pairing code like "K7QM-2XPA" (no ambiguous characters).</summary>
    public static string NewPairingCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars, 0, 4) + "-" + new string(chars, 4, 4);
    }

    public static string NormalizePairingCode(string code) => code.Trim().ToUpperInvariant().Replace(" ", "").Replace("-", "");
}
