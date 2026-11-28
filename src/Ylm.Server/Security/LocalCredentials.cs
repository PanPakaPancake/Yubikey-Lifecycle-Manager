using System.Security.Cryptography;
using System.Text;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Security;

/// <summary>PBKDF2-SHA256 Passwort-Hashes im Format pbkdf2-sha256$Iterationen$Salt$Hash.</summary>
public static class PasswordHashing
{
    private const int Iterations = 600_000;
    public const int MinLength = 14;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (stored is null) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Returns an error text or null when the password is acceptable.</summary>
    public static string? Validate(string? password, string upn)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return L.F($"Das Passwort muss mindestens {MinLength} Zeichen lang sein.");
        if (password.Contains(upn.Split('@')[0], StringComparison.OrdinalIgnoreCase)) return L.T("Das Passwort darf den Anmeldenamen nicht enthalten.");
        if (password.Distinct().Count() < 6) return L.T("Das Passwort ist zu einfach.");
        return null;
    }
}

/// <summary>TOTP nach RFC 6238 (SHA-1, 6 Ziffern, 30 Sekunden), kompatibel mit gängigen Authenticator-Apps.</summary>
public static class Totp
{
    private const int Digits = 6;
    private const int StepSeconds = 30;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long CurrentStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var value = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6");
    }

    /// <summary>Checks the code with one step tolerance and returns the matched step (null = wrong code).</summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        code = code?.Replace(" ", "").Trim();
        if (code is not { Length: Digits } || !code.All(char.IsAsciiDigit)) return null;
        var current = CurrentStep(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (lastUsedStep is { } last && step <= last) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code))) return step;
        }
        return null;
    }

    public static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static string Uri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}?secret={Base32(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";
}
