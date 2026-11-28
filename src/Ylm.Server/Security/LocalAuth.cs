using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Security;

/// <summary>
/// Anmeldung ohne Entra: lokale Benutzer mit Passkey (WebAuthn, z. B. YubiKey) und Notfallkonten mit Passwort + TOTP.
/// Konten werden über einen Einmal-Link eingerichtet, den ein Owner erzeugt.
/// </summary>
public sealed class LocalAuthService(
    YlmDbContext db, AuditService audit, IMemoryCache cache, IDataProtectionProvider dataProtection,
    IHttpContextAccessor http, IOptions<YlmOptions> options)
{
    public static readonly TimeSpan SetupLinkLifetime = TimeSpan.FromDays(3);
    private static readonly TimeSpan CeremonyLifetime = TimeSpan.FromMinutes(5);
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string TotpPurpose = "Ylm.LocalAuth.Totp.v1";

    private IDataProtector TotpProtector => dataProtection.CreateProtector(TotpPurpose);

    // ---------- WebAuthn plumbing ----------

    private async Task<Fido2> CreateFido2Async()
    {
        var request = http.HttpContext!.Request;
        var requestOrigin = $"{request.Scheme}://{request.Host}";
        var configured = options.Value.PublicBaseUrl ?? (await db.Installation.AsNoTracking().FirstOrDefaultAsync())?.PublicBaseUrl;
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requestOrigin };
        var rpId = request.Host.Host;
        if (Uri.TryCreate(configured, UriKind.Absolute, out var baseUri))
        {
            rpId = baseUri.Host;
            origins.Add(baseUri.GetLeftPart(UriPartial.Authority));
        }
        return new Fido2(new Fido2Configuration
        {
            ServerDomain = rpId,
            ServerName = "YubiKey Lifecycle Manager",
            Origins = origins,
            TimestampDriftTolerance = 300_000,
        });
    }

    private string Remember(string kind, string json, Guid? userId = null)
    {
        var id = TokenHashing.NewSecret(18);
        cache.Set("webauthn:" + id, new Ceremony(kind, json, userId), CeremonyLifetime);
        return id;
    }

    private Ceremony Take(string requestId, string kind)
    {
        if (!cache.TryGetValue("webauthn:" + requestId, out Ceremony? c) || c is null || c.Kind != kind)
            throw ApiException.BadRequest("expired", L.T("Die Anfrage ist abgelaufen. Bitte noch einmal versuchen."));
        cache.Remove("webauthn:" + requestId);
        return c;
    }

    private sealed record Ceremony(string Kind, string OptionsJson, Guid? UserId);

    public sealed record Challenge(string RequestId, JsonElement Options);

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Options to register a new passkey for the user.</summary>
    public async Task<Challenge> RegistrationOptionsAsync(PlatformUser user)
    {
        user.WebAuthnUserHandle ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await db.SaveChangesAsync();
        var existing = await db.UserPasskeys.Where(p => p.PlatformUserId == user.Id).Select(p => p.CredentialId).ToListAsync();
        var fido = await CreateFido2Async();
        var opts = fido.RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.WebAuthnUserHandle, Name = user.Upn, DisplayName = user.DisplayName },
            ExcludeCredentials = existing.Select(id => new PublicKeyCredentialDescriptor(id)).ToList(),
            AuthenticatorSelection = new AuthenticatorSelection
            {
                ResidentKey = ResidentKeyRequirement.Required,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });
        var json = opts.ToJson();
        return new Challenge(Remember("register", json, user.Id), Element(json));
    }

    /// <summary>Verifies the browser's response and stores the passkey.</summary>
    public async Task<UserPasskey> CompleteRegistrationAsync(PlatformUser user, string requestId, AuthenticatorAttestationRawResponse response, string? name, CancellationToken ct = default)
    {
        var ceremony = Take(requestId, "register");
        if (ceremony.UserId != user.Id) throw ApiException.BadRequest("expired", L.T("Die Anfrage gehört zu einem anderen Konto."));
        var fido = await CreateFido2Async();
        RegisteredPublicKeyCredential cred;
        try
        {
            cred = await fido.MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(ceremony.OptionsJson),
                IsCredentialIdUniqueToUserCallback = async (args, token) => !await db.UserPasskeys.AnyAsync(p => p.CredentialId == args.CredentialId, token),
            }, ct);
        }
        catch (Fido2VerificationException e)
        {
            throw ApiException.BadRequest("passkey_invalid", L.F($"Der Passkey konnte nicht geprüft werden: {e.Message}"));
        }
        var passkey = new UserPasskey
        {
            PlatformUserId = user.Id,
            CredentialId = cred.Id,
            PublicKey = cred.PublicKey,
            SignCount = cred.SignCount,
            Aaguid = cred.AaGuid,
            Name = string.IsNullOrWhiteSpace(name) ? L.F($"Passkey {DateTimeOffset.UtcNow.ToString("dd.MM.yyyy")}") : name.Trim(),
        };
        db.UserPasskeys.Add(passkey);
        audit.Add(AuditService.User(user), AuditActions.UserPasskeyAdded, target: user.Upn, details: new { passkey.Name, aaguid = passkey.Aaguid });
        await db.SaveChangesAsync(ct);
        return passkey;
    }

    /// <summary>Options for a username-less passkey sign-in (discoverable credential).</summary>
    public async Task<Challenge> LoginOptionsAsync()
    {
        var fido = await CreateFido2Async();
        var opts = fido.GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],
            UserVerification = UserVerificationRequirement.Required,
        });
        var json = opts.ToJson();
        return new Challenge(Remember("login", json), Element(json));
    }

    public async Task<PlatformUser> CompleteLoginAsync(string requestId, AuthenticatorAssertionRawResponse response, CancellationToken ct = default)
    {
        var ceremony = Take(requestId, "login");
        var credentialId = response.RawId;
        var passkey = await db.UserPasskeys.Include(p => p.PlatformUser).FirstOrDefaultAsync(p => p.CredentialId == credentialId, ct);
        if (passkey?.PlatformUser is not { } user)
            return await DenyAsync("passkey_unknown", null, L.T("Dieser Passkey ist auf der Plattform nicht bekannt."));
        if (!user.IsActive || user.LoginKind != UserLoginKind.Local)
            return await DenyAsync("not_registered", user.Upn, L.T("Ihr Konto ist für die Plattform nicht freigeschaltet."));
        var fido = await CreateFido2Async();
        VerifyAssertionResult result;
        try
        {
            result = await fido.MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(ceremony.OptionsJson),
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = (uint)passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) =>
                    Task.FromResult(user.WebAuthnUserHandle is { } h && args.UserHandle.AsSpan().SequenceEqual(h)),
            }, ct);
        }
        catch (Fido2VerificationException e)
        {
            return await DenyAsync("passkey_invalid", user.Upn, L.F($"Der Passkey konnte nicht geprüft werden: {e.Message}"));
        }
        passkey.SignCount = result.SignCount;
        passkey.LastUsedAt = DateTimeOffset.UtcNow;
        await SignInAsync(user, "passkey");
        return user;
    }

    // ---------- Notfallkonto ----------

    public async Task<PlatformUser> EmergencyLoginAsync(string upn, string password, string code)
    {
        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Upn.ToLower() == upn.Trim().ToLower() && u.LoginKind == UserLoginKind.Emergency);
        var now = DateTimeOffset.UtcNow;
        if (user is null)
        {
            PasswordHashing.Verify(password, "pbkdf2-sha256$600000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="); // gleiche Laufzeit
            return await DenyAsync("invalid_credentials", upn, L.T("Anmeldename, Passwort oder Code falsch."));
        }
        if (user.LockedUntil is { } locked && locked > now)
            return await DenyAsync("locked", user.Upn, L.F($"Zu viele Fehlversuche. Das Konto ist bis {locked.ToLocalTime():HH:mm} Uhr gesperrt."));
        var secret = user.TotpSecretProtected is { } p && user.PasswordHash is not null ? TotpProtector.Unprotect(Convert.FromBase64String(p)) : null;
        var passwordOk = PasswordHashing.Verify(password, user.PasswordHash);
        var step = secret is null ? null : Totp.Verify(secret, code, now, user.TotpLastStep);
        if (!passwordOk || step is null || !user.IsActive)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedLogins)
            {
                user.LockedUntil = now + LockoutDuration;
                user.FailedLoginCount = 0;
            }
            await db.SaveChangesAsync();
            return await DenyAsync(user.IsActive ? "invalid_credentials" : "not_registered", user.Upn, L.T("Anmeldename, Passwort oder Code falsch."));
        }
        user.TotpLastStep = step;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await SignInAsync(user, "emergency");
        return user;
    }

    // ---------- Einrichtung per Einmal-Link ----------

    /// <summary>Creates a new one-time setup link. For emergency accounts the old password and TOTP stop working.</summary>
    public async Task<(string Url, DateTimeOffset ExpiresAt)> CreateSetupLinkAsync(PlatformUser user, PlatformUser actor)
    {
        if (user.LoginKind == UserLoginKind.Entra) throw ApiException.Conflict("entra_user", L.T("Entra-Konten melden sich per SSO an und brauchen keinen Einrichtungslink."));
        var token = TokenHashing.NewSecret();
        user.SetupTokenHash = TokenHashing.Hash(token);
        user.SetupTokenExpiresAt = DateTimeOffset.UtcNow + SetupLinkLifetime;
        if (user.LoginKind == UserLoginKind.Emergency)
        {
            user.PasswordHash = null;
            user.TotpSecretProtected = null;
            user.TotpLastStep = null;
        }
        audit.Add(AuditService.User(actor), AuditActions.UserSetupLinkCreated, target: user.Upn, details: new { kind = user.LoginKind.ToString(), user.SetupTokenExpiresAt });
        await db.SaveChangesAsync();
        var request = http.HttpContext!.Request;
        var baseUrl = options.Value.PublicBaseUrl ?? (await db.Installation.AsNoTracking().FirstOrDefaultAsync())?.PublicBaseUrl ?? $"{request.Scheme}://{request.Host}";
        return ($"{baseUrl.TrimEnd('/')}/account-setup#{token}", user.SetupTokenExpiresAt.Value);
    }

    public async Task<PlatformUser> UserForSetupTokenAsync(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw ApiException.NotFound(L.T("Einrichtungslink"));
        var hash = TokenHashing.Hash(token);
        var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.SetupTokenHash == hash);
        if (user is null || user.SetupTokenExpiresAt < DateTimeOffset.UtcNow || !user.IsActive)
            throw new ApiException(410, "link_invalid", L.T("Der Einrichtungslink ist ungültig oder abgelaufen. Bitte einen Owner um einen neuen Link."));
        return user;
    }

    public async Task<(string Secret, string Uri)> StartTotpAsync(PlatformUser user)
    {
        if (user.LoginKind != UserLoginKind.Emergency) throw ApiException.Conflict("wrong_kind", L.T("Nur Notfallkonten nutzen Passwort und TOTP."));
        var secret = Totp.NewSecret();
        user.TotpSecretProtected = Convert.ToBase64String(TotpProtector.Protect(secret));
        user.PasswordHash = null;
        await db.SaveChangesAsync();
        var org = (await db.Installation.AsNoTracking().FirstOrDefaultAsync())?.OrganizationName;
        var issuer = string.IsNullOrWhiteSpace(org) ? "YubiKey Lifecycle Manager" : org + " YLM";
        return (Totp.Base32(secret), Totp.Uri(issuer, user.Upn, secret));
    }

    public async Task CompleteEmergencySetupAsync(PlatformUser user, string password, string code)
    {
        if (user.LoginKind != UserLoginKind.Emergency || user.TotpSecretProtected is null)
            throw ApiException.Conflict("wrong_state", L.T("Bitte zuerst den QR-Code für die Authenticator-App abrufen."));
        if (PasswordHashing.Validate(password, user.Upn) is { } error) throw ApiException.BadRequest("weak_password", error);
        var secret = TotpProtector.Unprotect(Convert.FromBase64String(user.TotpSecretProtected));
        var step = Totp.Verify(secret, code, DateTimeOffset.UtcNow, null)
                   ?? throw ApiException.BadRequest("invalid_code", L.T("Der Code aus der Authenticator-App stimmt nicht. Bitte Uhrzeit des Geräts prüfen und erneut versuchen."));
        user.PasswordHash = PasswordHashing.Hash(password);
        user.TotpLastStep = step;
        FinishSetup(user);
        await db.SaveChangesAsync();
        await SignInAsync(user, "emergency");
    }

    public static void FinishSetup(PlatformUser user)
    {
        user.SetupTokenHash = null;
        user.SetupTokenExpiresAt = null;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
    }

    // ---------- gemeinsam ----------

    public async Task SignInAsync(PlatformUser user, string method)
    {
        user.LastLoginAt = DateTimeOffset.UtcNow;
        audit.Add(AuditService.User(user), AuditActions.Login, details: new { method });
        await db.SaveChangesAsync();
        await http.HttpContext!.SignInAsync(AuthSchemes.Cookie, PlatformSignIn.BuildPrincipal(user, method switch { "emergency" => "Emergency", "recovery" => "Recovery", _ => "Passkey" }));
    }

    private async Task<PlatformUser> DenyAsync(string code, string? upn, string message)
    {
        await audit.WriteAsync(new AuditActor(ActorType.User, null, upn ?? L.T("unbekannt")), AuditActions.LoginDenied, details: new { code, message });
        throw new ApiException(401, code, message);
    }

    public static bool CanSignIn(PlatformUser u, bool entraSso) => u.IsActive && u.LoginKind switch
    {
        UserLoginKind.Local => u.Passkeys.Count > 0,
        UserLoginKind.Emergency => u.PasswordHash is not null,
        _ => entraSso,
    };
}
