using System.Text.Json;
using Fido2NetLib;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Security;

public static class LocalAuthEndpoints
{
    public sealed record PasskeyLoginRequest(string RequestId, JsonElement Response);
    public sealed record PasskeyRegisterRequest(string RequestId, JsonElement Response, string? Name);
    public sealed record EmergencyLoginRequest(string Upn, string Password, string Code);
    public sealed record TokenRequest(string Token);
    public sealed record SetupPasskeyRequest(string Token, string RequestId, JsonElement Response, string? Name);
    public sealed record SetupEmergencyRequest(string Token, string Password, string Code);
    public sealed record LoginSettingsRequest(bool EntraSsoEnabled);
    public sealed record RecoveryLoginRequest(string Code);

    public static object PasskeyDto(UserPasskey p) => new { p.Id, p.Name, p.Aaguid, p.CreatedAt, p.LastUsedAt };

    public static void MapLocalAuth(this IEndpointRouteBuilder app)
    {
        // ----- Anmeldeseite -----
        app.MapGet("/auth/methods", async (YlmDbContext db, IWebHostEnvironment env, Microsoft.Extensions.Options.IOptions<Infrastructure.YlmOptions> options) =>
        {
            var s = await db.Installation.AsNoTracking().FirstOrDefaultAsync();
            return Results.Ok(new
            {
                entraSso = s is { EntraSsoEnabled: true, ClientId: not null },
                passkey = await db.UserPasskeys.AnyAsync(),
                emergency = await db.PlatformUsers.AnyAsync(u => u.LoginKind == UserLoginKind.Emergency && u.PasswordHash != null),
                recovery = await db.RecoveryCodes.AnyAsync(c => c.UsedAt == null),
                development = AuthEndpoints.IsDevAuth(env, options.Value),
                demo = options.Value.DemoMode,
            });
        }).AllowAnonymous();

        app.MapPost("/auth/passkey/options", async (LocalAuthService local) => Results.Ok(await local.LoginOptionsAsync())).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        app.MapPost("/auth/passkey/login", async (PasskeyLoginRequest body, LocalAuthService local, CancellationToken ct) =>
        {
            var user = await local.CompleteLoginAsync(body.RequestId, Parse<AuthenticatorAssertionRawResponse>(body.Response), ct);
            return Results.Ok(new { user.Id, user.Upn });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        app.MapPost("/auth/emergency", async (EmergencyLoginRequest body, LocalAuthService local) =>
        {
            var user = await local.EmergencyLoginAsync(body.Upn ?? "", body.Password ?? "", body.Code ?? "");
            return Results.Ok(new { user.Id, user.Upn });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        app.MapPost("/auth/recovery", async (RecoveryLoginRequest body, RecoveryCodeService recovery) =>
        {
            var (user, remaining) = await recovery.LoginAsync(body.Code);
            return Results.Ok(new { user.Id, user.Upn, remaining });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        // ----- Eigene Notfallcodes (nur Owner) -----
        var codes = app.MapGroup("/api/me/recovery-codes").RequireAuthorization();
        codes.MapGet("", async (CurrentUser cu, RecoveryCodeService recovery) =>
        {
            var user = await cu.GetAsync();
            if (!RecoveryCodeService.Eligible(user)) throw ApiException.Forbidden(L.T("Notfallcodes gibt es nur für Owner."));
            return Results.Ok(await recovery.StatusAsync(user.Id));
        });
        codes.MapPost("", async (CurrentUser cu, RecoveryCodeService recovery) =>
        {
            var user = await cu.GetAsync();
            var list = await recovery.CreateAsync(user);
            return Results.Ok(new { codes = list, createdAt = DateTimeOffset.UtcNow });
        });

        // ----- Kontoeinrichtung per Einmal-Link -----
        app.MapPost("/auth/account-setup", async (TokenRequest body, LocalAuthService local) =>
        {
            var user = await local.UserForSetupTokenAsync(body.Token);
            return Results.Ok(new { user.Upn, user.DisplayName, loginKind = user.LoginKind.ToString(), user.SetupTokenExpiresAt });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        app.MapPost("/auth/account-setup/passkey/options", async (TokenRequest body, LocalAuthService local) =>
        {
            var user = await local.UserForSetupTokenAsync(body.Token);
            if (user.LoginKind != UserLoginKind.Local) throw ApiException.Conflict("wrong_kind", L.T("Dieses Konto meldet sich nicht mit Passkey an."));
            return Results.Ok(await local.RegistrationOptionsAsync(user));
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        app.MapPost("/auth/account-setup/passkey", async (SetupPasskeyRequest body, LocalAuthService local, YlmDbContext db, CancellationToken ct) =>
        {
            var user = await local.UserForSetupTokenAsync(body.Token);
            await local.CompleteRegistrationAsync(user, body.RequestId, Parse<AuthenticatorAttestationRawResponse>(body.Response), body.Name, ct);
            LocalAuthService.FinishSetup(user);
            await db.SaveChangesAsync(ct);
            await local.SignInAsync(user, "passkey");
            return Results.Ok(new { user.Id, user.Upn });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        app.MapPost("/auth/account-setup/totp", async (TokenRequest body, LocalAuthService local) =>
        {
            var user = await local.UserForSetupTokenAsync(body.Token);
            var (secret, uri) = await local.StartTotpAsync(user);
            return Results.Ok(new { secret, uri });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        app.MapPost("/auth/account-setup/emergency", async (SetupEmergencyRequest body, LocalAuthService local) =>
        {
            var user = await local.UserForSetupTokenAsync(body.Token);
            await local.CompleteEmergencySetupAsync(user, body.Password ?? "", body.Code ?? "");
            return Results.Ok(new { user.Id, user.Upn });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);

        // ----- Eigene Passkeys -----
        var me = app.MapGroup("/api/me/passkeys").RequireAuthorization();
        me.MapGet("", async (CurrentUser cu, YlmDbContext db) =>
        {
            var user = await cu.GetAsync();
            var list = await db.UserPasskeys.AsNoTracking().Where(p => p.PlatformUserId == user.Id).OrderBy(p => p.CreatedAt).ToListAsync();
            return Results.Ok(list.Select(PasskeyDto));
        });
        me.MapPost("/options", async (CurrentUser cu, LocalAuthService local) =>
        {
            var user = await RequireLocalAsync(cu);
            return Results.Ok(await local.RegistrationOptionsAsync(user));
        });
        me.MapPost("", async (PasskeyRegisterRequest body, CurrentUser cu, LocalAuthService local, CancellationToken ct) =>
        {
            var user = await RequireLocalAsync(cu);
            return Results.Ok(PasskeyDto(await local.CompleteRegistrationAsync(user, body.RequestId, Parse<AuthenticatorAttestationRawResponse>(body.Response), body.Name, ct)));
        });
        me.MapDelete("/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.GetAsync();
            await RemovePasskeyAsync(db, audit, user, user.Id, id);
            return Results.NoContent();
        });

        // ----- Verwaltung durch Owner -----
        var users = app.MapGroup("/api/users").RequireAuthorization();
        users.MapPost("/{id:guid}/setup-link", async (Guid id, CurrentUser cu, YlmDbContext db, LocalAuthService local) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Id == id) ?? throw ApiException.NotFound(L.T("Benutzer"));
            var (url, expiresAt) = await local.CreateSetupLinkAsync(user, actor);
            return Results.Ok(new { url, expiresAt });
        });
        users.MapGet("/{id:guid}/passkeys", async (Guid id, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageUsers);
            var list = await db.UserPasskeys.AsNoTracking().Where(p => p.PlatformUserId == id).OrderBy(p => p.CreatedAt).ToListAsync();
            return Results.Ok(list.Select(PasskeyDto));
        });
        users.MapDelete("/{id:guid}/passkeys/{passkeyId:guid}", async (Guid id, Guid passkeyId, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageUsers);
            await RemovePasskeyAsync(db, audit, actor, id, passkeyId);
            return Results.NoContent();
        });

        // ----- Partnerebene: Anmeldeverfahren -----
        app.MapGet("/api/platform/login", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            var s = await db.Installation.AsNoTracking().FirstAsync();
            return Results.Ok(new { s.EntraSsoEnabled, entraConfigured = s.ClientId is not null, homeTenantId = s.HomeTenantId });
        });
        app.MapPut("/api/platform/login", async (LoginSettingsRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var actor = await cu.RequireAsync(Permission.ManageSettings);
            var s = await db.Installation.FirstAsync();
            if (!body.EntraSsoEnabled)
            {
                var owners = await db.PlatformUsers.Include(u => u.Passkeys).Where(u => u.Role == PlatformRole.Owner && u.IsActive && u.LoginKind != UserLoginKind.Entra).ToListAsync();
                if (!owners.Any(o => LocalAuthService.CanSignIn(o, false)))
                    throw ApiException.Conflict("no_local_owner", L.T("Ohne Entra-SSO braucht es mindestens einen aktiven Owner mit eingerichtetem lokalem Konto (Passkey oder Passwort + TOTP). Sonst sperren Sie sich aus."));
            }
            audit.Add(AuditService.User(actor), AuditActions.LoginSettingsChanged, details: new { before = s.EntraSsoEnabled, after = body.EntraSsoEnabled });
            s.EntraSsoEnabled = body.EntraSsoEnabled;
            await db.SaveChangesAsync();
            return Results.Ok(new { s.EntraSsoEnabled, entraConfigured = s.ClientId is not null, homeTenantId = s.HomeTenantId });
        });
    }

    /// <summary>WebAuthn-Antworten ohne die globalen Enum-Konverter lesen (die Fido2-Typen bringen eigene mit).</summary>
    private static T Parse<T>(JsonElement json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json.GetRawText()) ?? throw new JsonException(); }
        catch (JsonException) { throw ApiException.BadRequest("passkey_invalid", L.T("Ungültige Antwort des Sicherheitsschlüssels.")); }
    }

    private static async Task<PlatformUser> RequireLocalAsync(CurrentUser cu)
    {
        var user = await cu.GetAsync();
        if (user.LoginKind != UserLoginKind.Local) throw ApiException.Conflict("wrong_kind", L.T("Passkeys für die Plattform gibt es nur bei lokalen Konten."));
        return user;
    }

    private static async Task RemovePasskeyAsync(YlmDbContext db, AuditService audit, PlatformUser actor, Guid userId, Guid passkeyId)
    {
        var passkey = await db.UserPasskeys.Include(p => p.PlatformUser).FirstOrDefaultAsync(p => p.Id == passkeyId && p.PlatformUserId == userId)
                      ?? throw ApiException.NotFound(L.T("Passkey"));
        var remaining = await db.UserPasskeys.CountAsync(p => p.PlatformUserId == userId && p.Id != passkeyId);
        if (remaining == 0 && actor.Id == userId)
            throw ApiException.Conflict("last_passkey", L.T("Das ist Ihr letzter Passkey. Bitte zuerst einen weiteren hinzufügen, sonst können Sie sich nicht mehr anmelden."));
        db.UserPasskeys.Remove(passkey);
        audit.Add(AuditService.User(actor), AuditActions.UserPasskeyRemoved, target: passkey.PlatformUser?.Upn, details: new { passkey.Name });
        await db.SaveChangesAsync();
    }
}
