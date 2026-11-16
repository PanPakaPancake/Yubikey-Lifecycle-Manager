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
        app.MapGet("/auth/login", (string? returnUrl) =>
        {
            var target = returnUrl is { Length: > 0 } r && r.StartsWith('/') && !r.StartsWith("//") ? r : "/";
            return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [AuthSchemes.Entra]);
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

    public static bool IsDevAuth(IWebHostEnvironment env, YlmOptions options) =>
        string.Equals(options.AuthMode, "Development", StringComparison.OrdinalIgnoreCase)
        && (env.IsDevelopment() || env.IsEnvironment("Testing"));
}
