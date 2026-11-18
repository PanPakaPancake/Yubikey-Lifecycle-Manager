using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ylm.Agent.Core;

public sealed record WebAuthnUser(byte[] Id, string Name, string DisplayName);
public sealed record WebAuthnCredentialDescriptor(byte[] Id, string Type);

/// <summary>The parts of PublicKeyCredentialCreationOptions (from Graph creationOptions) the agent needs.</summary>
public sealed record WebAuthnCreationOptions
{
    public required byte[] Challenge { get; init; }
    public required string RpId { get; init; }
    public string? RpName { get; init; }
    public required WebAuthnUser User { get; init; }
    public IReadOnlyList<int> Algorithms { get; init; } = [-7];
    public IReadOnlyList<WebAuthnCredentialDescriptor> ExcludeCredentials { get; init; } = [];
    public bool ResidentKey { get; init; } = true;
    public bool HmacSecret { get; init; }
    /// <summary>credProtect level 1-3 or null.</summary>
    public int? CredProtect { get; init; }
    public bool EnforceCredProtect { get; init; }

    public static WebAuthnCreationOptions Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("publicKey", out var pk)) r = pk;

        var rp = r.GetProperty("rp");
        var user = r.GetProperty("user");
        var algs = r.TryGetProperty("pubKeyCredParams", out var p)
            ? p.EnumerateArray().Where(x => !x.TryGetProperty("type", out var t) || t.GetString() == "public-key").Select(x => x.GetProperty("alg").GetInt32()).ToList()
            : [-7];
        var exclude = r.TryGetProperty("excludeCredentials", out var ex) && ex.ValueKind == JsonValueKind.Array
            ? ex.EnumerateArray().Select(x => new WebAuthnCredentialDescriptor(Base64Url.DecodeOrUtf8(x.GetProperty("id").GetString()!, "excludeCredentials.id"), x.TryGetProperty("type", out var t) ? t.GetString() ?? "public-key" : "public-key")).ToList()
            : [];
        var residentKey = true;
        if (r.TryGetProperty("authenticatorSelection", out var sel))
        {
            if (sel.TryGetProperty("residentKey", out var rk) && rk.ValueKind == JsonValueKind.String) residentKey = rk.GetString() != "discouraged";
            else if (sel.TryGetProperty("requireResidentKey", out var rrk) && rrk.ValueKind is JsonValueKind.False) residentKey = false;
        }
        bool hmac = false, enforce = false;
        int? credProtect = null;
        if (r.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object)
        {
            hmac = ext.TryGetProperty("hmacCreateSecret", out var h) && h.ValueKind == JsonValueKind.True;
            enforce = ext.TryGetProperty("enforceCredentialProtectionPolicy", out var e) && e.ValueKind == JsonValueKind.True;
            if (ext.TryGetProperty("credentialProtectionPolicy", out var cp) && cp.ValueKind == JsonValueKind.String)
            {
                credProtect = cp.GetString() switch
                {
                    "userVerificationOptional" => 1,
                    "userVerificationOptionalWithCredentialIDList" => 2,
                    "userVerificationRequired" => 3,
                    _ => null,
                };
            }
        }
        return new WebAuthnCreationOptions
        {
            Challenge = Base64Url.DecodeOrUtf8(r.GetProperty("challenge").GetString()!, "challenge"),
            RpId = rp.GetProperty("id").GetString()!,
            RpName = rp.TryGetProperty("name", out var n) ? n.GetString() : null,
            User = new WebAuthnUser(
                Base64Url.DecodeOrUtf8(user.GetProperty("id").GetString()!, "user.id"),
                user.TryGetProperty("name", out var un) ? un.GetString() ?? "" : "",
                user.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : ""),
            Algorithms = algs.Count == 0 ? [-7] : algs,
            ExcludeCredentials = exclude,
            ResidentKey = residentKey,
            HmacSecret = hmac,
            CredProtect = credProtect,
            EnforceCredProtect = enforce,
        };
    }
}

public static class WebAuthnEncoding
{
    /// <summary>clientDataJSON for a registration ceremony (WebAuthn §5.8.1).</summary>
    public static byte[] ClientDataJson(byte[] challenge, string origin)
    {
        var json = "{\"type\":\"webauthn.create\",\"challenge\":\"" + Base64Url.Encode(challenge) +
                   "\",\"origin\":" + JsonSerializer.Serialize(origin) + ",\"crossOrigin\":false}";
        return Encoding.UTF8.GetBytes(json);
    }

    public static byte[] Hash(byte[] data) => SHA256.HashData(data);

    /// <summary>WebAuthn attestationObject: CBOR map {"fmt", "attStmt", "authData"}.</summary>
    public static byte[] AttestationObject(string format, ReadOnlyMemory<byte> encodedAttStmt, ReadOnlyMemory<byte> authData)
    {
        var w = new CborWriter(CborConformanceMode.Ctap2Canonical);
        w.WriteStartMap(3);
        // CTAP2 canonical order: shorter keys first → "fmt", "attStmt", "authData"
        w.WriteTextString("fmt");
        w.WriteTextString(format);
        w.WriteTextString("attStmt");
        w.WriteEncodedValue(encodedAttStmt.Span);
        w.WriteTextString("authData");
        w.WriteByteString(authData.Span);
        w.WriteEndMap();
        return w.Encode();
    }

    /// <summary>Reads the AAGUID from authenticator data (bytes 37..53 when attested credential data is present).</summary>
    public static Guid? AaguidFromAuthData(ReadOnlySpan<byte> authData) =>
        authData.Length >= 55 && (authData[32] & 0x40) != 0 ? new Guid(authData.Slice(37, 16), bigEndian: true) : null;
}

public static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url, tolerating standard base64 and padding.</summary>
    public static byte[] Decode(string value)
    {
        var s = value.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        return Convert.FromBase64String(s);
    }

    public static bool TryDecode(string value, out byte[] bytes)
    {
        var s = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd('=').Replace('-', '+').Replace('_', '/');
        bytes = [];
        if (s.Length % 4 == 1) return false;
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        var buffer = new byte[s.Length * 3 / 4];
        if (!Convert.TryFromBase64String(s, buffer, out var written)) return false;
        bytes = buffer[..written];
        return true;
    }

    /// <summary>Records which fields were not base64url and taken as text (for the agent log).</summary>
    [ThreadStatic] public static List<string>? TextFallbacks;

    /// <summary>
    /// WebAuthn JSON encodes binary fields as base64url. Microsoft Graph's creationOptions has been seen with values
    /// that are not valid base64url (e.g. a token string); such values are used as their UTF-8 bytes, which is what a
    /// browser does when the string is passed as the BufferSource.
    /// </summary>
    public static byte[] DecodeOrUtf8(string value, string field)
    {
        if (TryDecode(value, out var bytes)) return bytes;
        TextFallbacks?.Add($"{field} (Länge {value.Length})");
        return Encoding.UTF8.GetBytes(value);
    }
}
