using System.Security.Cryptography.X509Certificates;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Setup;

public static class SetupEndpoints
{
    public const string TokenHeader = "X-Setup-Token";

    public sealed record BasicsRequest(string OrganizationName, string PublicBaseUrl, string? BootstrapOwnerUpn);
    public sealed record CompleteRequest(string? BootstrapOwnerUpn, bool? AddHomeTenant);
    public sealed record AppRegistrationRequest(Guid ClientId, Guid HomeTenantId);
    public sealed record InteractiveCompleteRequest(string? Url, string? Code, string? State);

    public static void MapSetup(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/setup/status", async (SetupService setup, HttpRequest req, Microsoft.Extensions.Options.IOptions<Infrastructure.YlmOptions> options) =>
        {
            var s = await setup.GetSettingsAsync();
            return Results.Ok(new { setupComplete = s.IsSetupComplete, organizationName = s.OrganizationName, suggestedBaseUrl = setup.BaseUrl(s, req), demo = options.Value.DemoMode });
        }).AllowAnonymous();

        // Rücksprung der interaktiven Anmeldung. Ohne Setup-Token: Die Seite auf http://localhost wird in einem neuen Tab
        // geöffnet. Berechtigt ist, wer den einmaligen state kennt; den bekommt nur, wer die Anmeldung mit Token gestartet hat
        // (PKCE-Verifier bleibt auf dem Server).
        app.MapPost("/api/setup/signin/complete", async (InteractiveCompleteRequest body, SetupService setup, CancellationToken ct) =>
        {
            var s = await setup.GetSettingsAsync();
            if (s.IsSetupComplete) return Results.Json(new ApiError("setup_token", "Die Einrichtung ist bereits abgeschlossen."), statusCode: 401);
            return Results.Ok(await setup.CompleteInteractiveSignInAsync(body.Url, body.Code, body.State, ct));
        }).AllowAnonymous().RequireRateLimiting(Security.SecurityHeaders.AuthRateLimit);

        var group = app.MapGroup("/api/setup").AllowAnonymous().AddEndpointFilter(async (ctx, next) =>
        {
            var setup = ctx.HttpContext.RequestServices.GetRequiredService<SetupService>();
            if (!await setup.ValidateTokenAsync(ctx.HttpContext.Request.Headers[TokenHeader]))
                return Results.Json(new ApiError("setup_token", "Ungültiges Setup-Token oder Einrichtung bereits abgeschlossen."), statusCode: 401);
            return await next(ctx);
        });

        group.MapGet("/state", async (SetupService setup, HttpRequest req) =>
        {
            var s = await setup.GetSettingsAsync();
            return Results.Ok(new
            {
                s.OrganizationName,
                publicBaseUrl = setup.BaseUrl(s, req),
                s.BootstrapOwnerUpn,
                s.ClientId,
                s.HomeTenantId,
                s.CertificateThumbprint,
                s.CertificateExpiresAt,
                redirectUris = setup.RedirectUris(s, req),
                requiredPermissions = SetupService.RequiredApplicationPermissions,
                signedInAdmin = setup.LastSignedInAdmin,
            });
        });

        // Sign-in with Microsoft (interactive, authorization code + PKCE), then the platform creates the app registration itself.
        group.MapPost("/signin/interactive", (SetupService setup, HttpRequest req) =>
        {
            var (url, redirectUri, automatic) = setup.StartInteractiveSignIn(req);
            return Results.Ok(new { url, redirectUri, automatic });
        });

        // Fallback: device code (often blocked by Conditional Access).
        group.MapPost("/signin/start", async (SetupService setup, CancellationToken ct) =>
        {
            var p = await setup.StartSignInAsync(ct);
            return Results.Ok(new { p.UserCode, p.VerificationUri, p.ExpiresAt, p.IntervalSeconds });
        });

        group.MapPost("/signin/poll", async (SetupService setup, CancellationToken ct) => Results.Ok(await setup.PollSignInAsync(ct)));

        group.MapPost("/provision", async (SetupService setup, CancellationToken ct) =>
        {
            var result = await setup.ProvisionAppAsync(ct);
            var s = await setup.GetSettingsAsync();
            return Results.Ok(new { result.ClientId, s.HomeTenantId, result.Steps, s.BootstrapOwnerUpn });
        });

        // Tests the app-only sign-in with the certificate. New credentials and consent can take a minute in Entra.
        group.MapPost("/verify", async (SetupService setup) =>
        {
            var (ok, message, granted) = await setup.TestAppRegistrationAsync();
            var missing = SetupService.RequiredApplicationPermissions.Except(granted).ToList();
            return Results.Ok(new { ok, message, granted, missing });
        });

        group.MapPost("/basics", async (BasicsRequest body, SetupService setup) =>
        {
            await setup.SaveBasicsAsync(body.OrganizationName, body.PublicBaseUrl, body.BootstrapOwnerUpn);
            return Results.NoContent();
        });

        group.MapPost("/certificate", async (SetupService setup) =>
        {
            var cert = await setup.EnsureCertificateAsync();
            return Results.Ok(new { cert.Thumbprint, notAfter = cert.NotAfter.ToUniversalTime() });
        });

        group.MapGet("/certificate.cer", async (SetupService setup) =>
        {
            var cert = await setup.EnsureCertificateAsync();
            return Results.File(cert.Export(X509ContentType.Cert), "application/pkix-cert", "ylm-app.cer");
        });

        group.MapGet("/app-registration.ps1", async (SetupService setup, HttpRequest req) =>
        {
            var s = await setup.GetSettingsAsync();
            var cert = await setup.EnsureCertificateAsync();
            return Results.Text(setup.BuildAppRegistrationScript(s, cert, req), "text/plain; charset=utf-8");
        });

        group.MapPost("/app-registration", async (AppRegistrationRequest body, SetupService setup) =>
        {
            if (body.ClientId == Guid.Empty || body.HomeTenantId == Guid.Empty)
                throw ApiException.BadRequest("invalid", "Bitte Client-ID und Tenant-ID angeben.");
            await setup.SaveAppRegistrationAsync(body.ClientId, body.HomeTenantId);
            var (ok, message, granted) = await setup.TestAppRegistrationAsync();
            var missing = SetupService.RequiredApplicationPermissions.Except(granted).ToList();
            return Results.Ok(new { ok, message, granted, missing });
        });

        group.MapPost("/complete", async (CompleteRequest? body, SetupService setup, CancellationToken ct) =>
        {
            await setup.CompleteAsync(new SetupService.CompleteOptions(body?.BootstrapOwnerUpn, body?.AddHomeTenant ?? true), ct);
            return Results.NoContent();
        });
    }
}
