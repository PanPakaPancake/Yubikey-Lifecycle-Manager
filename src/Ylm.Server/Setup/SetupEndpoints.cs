using System.Security.Cryptography.X509Certificates;
using Ylm.Server.Infrastructure;

namespace Ylm.Server.Setup;

public static class SetupEndpoints
{
    public const string TokenHeader = "X-Setup-Token";

    public sealed record BasicsRequest(string OrganizationName, string PublicBaseUrl, string BootstrapOwnerUpn);
    public sealed record AppRegistrationRequest(Guid ClientId, Guid HomeTenantId);

    public static void MapSetup(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/setup/status", async (SetupService setup, HttpRequest req) =>
        {
            var s = await setup.GetSettingsAsync();
            return Results.Ok(new { setupComplete = s.IsSetupComplete, organizationName = s.OrganizationName, suggestedBaseUrl = setup.BaseUrl(s, req) });
        }).AllowAnonymous();

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
            });
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

        group.MapPost("/complete", async (SetupService setup) =>
        {
            await setup.CompleteAsync();
            return Results.NoContent();
        });
    }
}
