using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ylm.Core;
using Ylm.Server.Data;
using Ylm.Server.Security;

namespace Ylm.Server.Tests;

public sealed class LocalUsersTests : IAsyncLifetime
{
    private readonly TestApp _app = new();
    private HttpClient _owner = null!;

    public async Task InitializeAsync()
    {
        _owner = await _app.LoginAsync("owner@home.example", PlatformRole.Owner);
        await _app.WithDbAsync(async db =>
        {
            var s = db.Installation.FirstOrDefault() ?? db.Installation.Add(new InstallationSettings()).Entity;
            s.OrganizationName = "Contoso IT";
            s.ClientId = Guid.NewGuid();
            await db.SaveChangesAsync();
        });
    }

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {text}");
        return string.IsNullOrEmpty(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    private async Task<(Guid Id, string Token)> CreateUserAsync(string upn, string kind, string role = "Owner")
    {
        var user = await Json(await _owner.PostAsJsonAsync("/api/users", new { upn, displayName = upn, role, allTenants = true, loginKind = kind }));
        var id = user.GetProperty("id").GetGuid();
        var link = await Json(await _owner.PostAsync($"/api/users/{id}/setup-link", null));
        var url = link.GetProperty("url").GetString()!;
        Assert.StartsWith("https://ylm.test/account-setup#", url);
        return (id, url.Split('#')[1]);
    }

    // ---------- Hilfsfunktionen ----------

    [Fact]
    public void Totp_matches_rfc6238_test_vector()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Assert.Equal("287082", Totp.Code(secret, 59 / 30));
        Assert.Equal("005924", Totp.Code(secret, 1234567890 / 30));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(secret));
    }

    [Fact]
    public void Password_hash_roundtrip()
    {
        var hash = PasswordHashing.Hash("Korrekt-Pferd-Batterie-42");
        Assert.True(PasswordHashing.Verify("Korrekt-Pferd-Batterie-42", hash));
        Assert.False(PasswordHashing.Verify("korrekt-pferd-batterie-42", hash));
        Assert.NotNull(PasswordHashing.Validate("kurz", "a@b"));
    }

    // ---------- Notfallkonto ----------

    [Fact]
    public async Task Emergency_account_setup_login_replay_and_lockout()
    {
        var (_, token) = await CreateUserAsync("notfall@contoso.example", "Emergency");
        var anon = _app.Client();
        var info = await Json(await anon.PostAsJsonAsync("/auth/account-setup", new { token }));
        Assert.Equal("Emergency", info.GetProperty("loginKind").GetString());

        var totp = await Json(await anon.PostAsJsonAsync("/auth/account-setup/totp", new { token }));
        Assert.StartsWith("otpauth://totp/Contoso%20IT%20YLM:", totp.GetProperty("uri").GetString());
        var secret = Base32Decode(totp.GetProperty("secret").GetString()!);
        var step = Totp.CurrentStep(DateTimeOffset.UtcNow);

        var weak = await anon.PostAsJsonAsync("/auth/account-setup/emergency", new { token, password = "zu-kurz", code = Totp.Code(secret, step) });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        const string password = "Rettungs-Schluessel-2026!";
        await Json(await anon.PostAsJsonAsync("/auth/account-setup/emergency", new { token, password, code = Totp.Code(secret, step - 1) }));
        var me = await Json(await anon.GetAsync("/api/me"));
        Assert.Equal("notfall@contoso.example", me.GetProperty("user").GetProperty("upn").GetString());

        // Link ist verbraucht.
        Assert.Equal(HttpStatusCode.Gone, (await _app.Client().PostAsJsonAsync("/auth/account-setup", new { token })).StatusCode);

        // Anmeldung: derselbe Code darf nicht noch einmal funktionieren.
        var replay = await _app.Client().PostAsJsonAsync("/auth/emergency", new { upn = "notfall@contoso.example", password, code = Totp.Code(secret, step - 1) });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        var fresh = _app.Client();
        await Json(await fresh.PostAsJsonAsync("/auth/emergency", new { upn = "NOTFALL@contoso.example", password, code = Totp.Code(secret, step) }));
        await Json(await fresh.GetAsync("/api/users"));

        // Fünf Fehlversuche sperren das Konto, auch das richtige Passwort hilft dann nicht.
        for (var i = 0; i < LocalAuthService.MaxFailedLogins; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await _app.Client().PostAsJsonAsync("/auth/emergency", new { upn = "notfall@contoso.example", password = "falsch-falsch-falsch", code = "000000" })).StatusCode);
        var locked = await _app.Client().PostAsJsonAsync("/auth/emergency", new { upn = "notfall@contoso.example", password, code = Totp.Code(secret, step + 1) });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Assert.Contains("locked", await locked.Content.ReadAsStringAsync());

        // Neuer Link setzt Passwort und TOTP zurück.
        var (id, _) = await CreateUserAsync("notfall2@contoso.example", "Emergency");
        var users = await Json(await _owner.GetAsync("/api/users"));
        var row = users.EnumerateArray().First(u => u.GetProperty("id").GetGuid() == id);
        Assert.True(row.GetProperty("setupPending").GetBoolean());
        Assert.False(row.GetProperty("emergencyReady").GetBoolean());
    }

    // ---------- Passkey ----------

    [Fact]
    public async Task Local_user_registers_passkey_and_signs_in_with_it()
    {
        var (id, token) = await CreateUserAsync("lokal@contoso.example", "Local", "Operator");
        var authenticator = new SoftwareAuthenticator("ylm.test", "https://ylm.test");
        var setup = _app.Client();

        var wrong = await setup.PostAsJsonAsync("/auth/account-setup/totp", new { token });
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);

        var reg = await Json(await setup.PostAsJsonAsync("/auth/account-setup/passkey/options", new { token }));
        Assert.Equal("ylm.test", reg.GetProperty("options").GetProperty("rp").GetProperty("id").GetString());
        var attestation = authenticator.Create(reg.GetProperty("options"));
        await Json(await setup.PostAsJsonAsync("/auth/account-setup/passkey", new { token, requestId = reg.GetProperty("requestId").GetString(), response = attestation, name = "YubiKey 5C" }));
        var me = await Json(await setup.GetAsync("/api/me"));
        Assert.Equal(id, me.GetProperty("user").GetProperty("id").GetGuid());

        // Neue Sitzung: Anmeldung ohne Benutzername nur mit dem Passkey.
        var login = _app.Client();
        var opts = await Json(await login.PostAsync("/auth/passkey/options", null));
        var assertion = authenticator.Get(opts.GetProperty("options"));
        await Json(await login.PostAsJsonAsync("/auth/passkey/login", new { requestId = opts.GetProperty("requestId").GetString(), response = assertion }));
        Assert.Equal(id, (await Json(await login.GetAsync("/api/me"))).GetProperty("user").GetProperty("id").GetGuid());

        // Challenge kann nicht wiederverwendet werden.
        var again = await _app.Client().PostAsJsonAsync("/auth/passkey/login", new { requestId = opts.GetProperty("requestId").GetString(), response = assertion });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);

        // Eigenen letzten Passkey kann man nicht löschen.
        var mine = await Json(await login.GetAsync("/api/me/passkeys"));
        Assert.Equal("YubiKey 5C", mine[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await login.DeleteAsync($"/api/me/passkeys/{mine[0].GetProperty("id").GetGuid()}")).StatusCode);

        // Deaktivierte Benutzer kommen nicht mehr rein.
        await Json(await _owner.PutAsJsonAsync($"/api/users/{id}", new { upn = "lokal@contoso.example", role = "Operator", allTenants = true, isActive = false }));
        var opts2 = await Json(await _app.Client().PostAsync("/auth/passkey/options", null));
        var denied = await _app.Client().PostAsJsonAsync("/auth/passkey/login", new { requestId = opts2.GetProperty("requestId").GetString(), response = authenticator.Get(opts2.GetProperty("options")) });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    // ---------- Partnerebene: Entra-SSO ----------

    [Fact]
    public async Task Entra_sso_can_only_be_disabled_with_a_working_local_owner()
    {
        var blocked = await _owner.PutAsJsonAsync("/api/platform/login", new { entraSsoEnabled = false });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var (_, token) = await CreateUserAsync("owner-lokal@contoso.example", "Local");
        var authenticator = new SoftwareAuthenticator("ylm.test", "https://ylm.test");
        var c = _app.Client();
        var reg = await Json(await c.PostAsJsonAsync("/auth/account-setup/passkey/options", new { token }));
        await Json(await c.PostAsJsonAsync("/auth/account-setup/passkey", new { token, requestId = reg.GetProperty("requestId").GetString(), response = authenticator.Create(reg.GetProperty("options")) }));

        var ok = await Json(await _owner.PutAsJsonAsync("/api/platform/login", new { entraSsoEnabled = false }));
        Assert.False(ok.GetProperty("entraSsoEnabled").GetBoolean());
        var methods = await Json(await _app.Client().GetAsync("/auth/methods"));
        Assert.False(methods.GetProperty("entraSso").GetBoolean());
        Assert.True(methods.GetProperty("passkey").GetBoolean());
        var redirect = await _app.Client().GetAsync("/auth/login");
        Assert.Equal("/login?error=sso_disabled", redirect.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Entra_user_needs_no_setup_link()
    {
        var user = await Json(await _owner.PostAsJsonAsync("/api/users", new { upn = "sso@home.example", role = "Auditor", allTenants = true }));
        Assert.Equal("Entra", user.GetProperty("loginKind").GetString());
        var res = await _owner.PostAsync($"/api/users/{user.GetProperty("id").GetGuid()}/setup-link", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in s.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }
}

/// <summary>Minimaler Software-Authenticator (ES256, Attestation "none") für Tests.</summary>
internal sealed class SoftwareAuthenticator(string rpId, string origin)
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(32);
    private byte[] _userHandle = [];
    private uint _counter;

    private static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private byte[] ClientData(string type, JsonElement options) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge = options.GetProperty("challenge").GetString(), origin, crossOrigin = false }));

    private byte[] AuthData(byte flags, byte[]? attested)
    {
        var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId))) { flags };
        _counter++;
        data.AddRange([(byte)(_counter >> 24), (byte)(_counter >> 16), (byte)(_counter >> 8), (byte)_counter]);
        if (attested is not null) data.AddRange(attested);
        return [.. data];
    }

    public object Create(JsonElement options)
    {
        _userHandle = FromB64(options.GetProperty("user").GetProperty("id").GetString()!);
        var p = _key.ExportParameters(false);
        var cose = new CborWriter();
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);
        cose.WriteInt32(3); cose.WriteInt32(-7);
        cose.WriteInt32(-1); cose.WriteInt32(1);
        cose.WriteInt32(-2); cose.WriteByteString(p.Q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(p.Q.Y!);
        cose.WriteEndMap();
        var attested = new List<byte>(new byte[16]) { (byte)(_credentialId.Length >> 8), (byte)_credentialId.Length };
        attested.AddRange(_credentialId);
        attested.AddRange(cose.Encode());
        var authData = AuthData(0x45, [.. attested]);
        var att = new CborWriter();
        att.WriteStartMap(3);
        att.WriteTextString("fmt"); att.WriteTextString("none");
        att.WriteTextString("attStmt"); att.WriteStartMap(0); att.WriteEndMap();
        att.WriteTextString("authData"); att.WriteByteString(authData);
        att.WriteEndMap();
        return new
        {
            id = B64(_credentialId), rawId = B64(_credentialId), type = "public-key",
            response = new { attestationObject = B64(att.Encode()), clientDataJSON = B64(ClientData("webauthn.create", options)), transports = new[] { "usb" } },
            clientExtensionResults = new { },
        };
    }

    public object Get(JsonElement options)
    {
        var clientData = ClientData("webauthn.get", options);
        var authData = AuthData(0x05, null);
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new
        {
            id = B64(_credentialId), rawId = B64(_credentialId), type = "public-key",
            response = new { authenticatorData = B64(authData), clientDataJSON = B64(clientData), signature = B64(signature), userHandle = B64(_userHandle) },
            clientExtensionResults = new { },
        };
    }
}
