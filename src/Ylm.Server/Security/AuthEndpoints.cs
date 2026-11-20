using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Security;

public static class AuthEndpoints
{
    public sealed record DevLoginRequest(string Upn, string? DisplayName, PlatformRole? Role);

    public static void MapAuth(this IEndpointRouteBuilder app, IWebHostEnvironment env, YlmOptions options)
    {
        app.MapGet("/auth/login", async (string? returnUrl, int? retry, bool? reauth, YlmDbContext db, IOptionsMonitor<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions> oidc,
            IOptionsMonitorCache<Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions> oidcCache) =>
        {
            var target = IsLocalPath(returnUrl) ? returnUrl! : "/";
            var settings = await db.Installation.AsNoTracking().FirstOrDefaultAsync();
            if (settings is { EntraSsoEnabled: false }) return Results.Redirect("/login?error=sso_disabled");
            // Ohne eingetragene Entra-App nicht mit leerer Client-ID zu Microsoft schicken (AADSTS700038).
            if (settings?.ClientId is not { } clientId) return Results.Redirect(settings is { IsSetupComplete: true } ? "/login?error=sso_not_configured" : "/setup");
            // Optionen, die vor dem Eintragen der App (oder bei nicht erreichbarer Datenbank) gebaut wurden, verwerfen.
            // Die Authentifizierungs-Middleware hat den Handler für diese Anfrage schon damit gebaut, daher einmal neu laden.
            if (retry is null && oidc.Get(AuthSchemes.Entra).ClientId != clientId.ToString())
            {
                oidcCache.TryRemove(AuthSchemes.Entra);
                return Results.Redirect("/auth/login?retry=1" + (reauth == true ? "&reauth=true" : "") + "&returnUrl=" + Uri.EscapeDataString(target));
            }
            // reauth: Microsoft soll neu fragen (prompt=login) statt eine bestehende Sitzung ohne Sicherheitsschlüssel zu nutzen.
            AuthenticationProperties properties = reauth == true
                ? new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectChallengeProperties { RedirectUri = target, Prompt = "login" }
                : new AuthenticationProperties { RedirectUri = target };
            return Results.Challenge(properties, [AuthSchemes.Entra]);
        }).AllowAnonymous();

        app.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(AuthSchemes.Cookie);
            return Results.NoContent();
        }).AllowAnonymous();

        if (IsDevAuth(env, options))
        {
            // Local development and automated tests only: sign in without Entra.
            app.MapPost("/auth/dev-login", async (DevLoginRequest body, HttpContext ctx, YlmDbContext db, AuditService audit) =>
            {
                var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Upn.ToLower() == body.Upn.ToLower());
                if (user is null)
                {
                    user = new PlatformUser
                    {
                        Upn = body.Upn,
                        DisplayName = body.DisplayName ?? body.Upn,
                        Role = body.Role ?? (await db.PlatformUsers.AnyAsync() ? PlatformRole.Auditor : PlatformRole.Owner),
                    };
                    db.PlatformUsers.Add(user);
                }
                else if (body.Role is { } role) user.Role = role;
                user.LastLoginAt = DateTimeOffset.UtcNow;
                audit.Add(AuditService.User(user), AuditActions.Login, details: new { mode = "development" });
                await db.SaveChangesAsync();
                await ctx.SignInAsync(AuthSchemes.Cookie, PlatformSignIn.BuildPrincipal(user, "Development"));
                return Results.Ok(new { user.Id, user.Upn, Role = user.Role.ToString() });
            }).AllowAnonymous();
        }
    }

    /// <summary>
    /// Nur Pfade dieser Plattform als Ziel nach der Anmeldung (wie Url.IsLocalUrl): "/x" ja, "//host", "/\\host" und Steuerzeichen nein.
    /// Browser lesen "/\\host" wie "//host", das wäre sonst eine offene Weiterleitung.
    /// </summary>
    public static bool IsLocalPath(string? url) =>
        url is { Length: > 0 } && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
        && !url.Any(char.IsControl);

    public static bool IsDevAuth(IWebHostEnvironment env, YlmOptions options) =>
        string.Equals(options.AuthMode, "Development", StringComparison.OrdinalIgnoreCase)
        && (env.IsDevelopment() || env.IsEnvironment("Testing"));
}
