using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;
using Ylm.Server.Setup;

namespace Ylm.Server.Updates;

public sealed record CertificateInfo(string Subject, string Issuer, DateTimeOffset NotBefore, DateTimeOffset NotAfter, IReadOnlyList<string> DnsNames, string Thumbprint);

/// <summary>
/// Adresse, Ports und Zertifikat der Installation. Der Server ändert .env nicht selbst: das übernimmt der
/// Updater-Container in einem Hilfscontainer (prüft vorher Compose und Caddy, stellt bei Fehlern den vorigen Stand her).
/// </summary>
public sealed class HostingService(UpdateService updates, SetupService setup, YlmDbContext db, AuditService audit, IOptions<YlmOptions> options)
{
    public sealed record State(bool Available, string? Reason, HostingCurrent Current, HostingView View, CertificateInfo? Certificate);

    public async Task<State> GetAsync(CancellationToken ct = default)
    {
        var s = await setup.GetSettingsAsync();
        var (status, body) = await updates.UpdaterGetAsync("/hosting", ct);
        var current = Parse(body, out var certPem);
        var view = HostingPlan.Describe(current, ConfiguredUrl ?? s.PublicBaseUrl);
        string? reason = status switch
        {
            HttpStatusCode.OK => null,
            HttpStatusCode.ServiceUnavailable => "Ohne Updater-Container (UPDATER_TOKEN in .env) lassen sich Adresse und Zertifikat nur mit dem Installationsskript ändern.",
            HttpStatusCode.NotFound => "Der Updater kennt diese Funktion noch nicht. Bitte das Installationsskript einmal erneut ausführen (lädt den neuen Updater).",
            _ => "Der Updater-Container ist nicht erreichbar.",
        };
        return new State(status == HttpStatusCode.OK, reason, current, view, view.Tls == HostingTls.Own ? Describe(certPem) : null);
    }

    private string? ConfiguredUrl => string.IsNullOrWhiteSpace(options.Value.PublicBaseUrl) ? null : options.Value.PublicBaseUrl;

    /// <returns>Neue Adresse und ob sie sich geändert hat.</returns>
    public async Task<(string PublicUrl, bool AddressChanged, IReadOnlyList<string> Settings)> ApplyAsync(HostingRequest request, bool entraConfirmed, AuditActor actor, CancellationToken ct = default)
    {
        var state = await GetAsync(ct);
        if (!state.Available) throw ApiException.Conflict("hosting_unavailable", state.Reason ?? "Der Updater ist nicht erreichbar.");
        var change = HostingPlan.Build(state.Current, request);
        if (change.Env.Count == 0 && change.Files.Count == 0)
            throw ApiException.BadRequest("nothing_changed", "Es hat sich nichts geändert.");

        var s = await setup.GetSettingsAsync();
        var before = state.View.PublicUrl;
        var addressChanged = !SameOrigin(before, change.PublicUrl);
        if (addressChanged && s.ClientId is not null && !entraConfirmed)
            throw ApiException.BadRequest("entra_required", "Die neue Adresse muss zuerst in der App-Registrierung in Entra eingetragen sein, sonst klappt die Anmeldung danach nicht.");

        await updates.PostUpdaterAsync("/hosting", new { env = change.Env, files = change.Files }, ct);
        s.PublicBaseUrl = change.PublicUrl;
        await db.SaveChangesAsync(ct);
        var settings = change.Env.Keys.Order().ToList();
        await audit.WriteAsync(actor, AuditActions.HostingChanged, details: new
        {
            from = before,
            to = change.PublicUrl,
            // Nur Namen, keine Werte (Zugangsdaten des DNS-Anbieters, Schlüssel).
            settings,
            files = change.Files.Keys.Order().ToList(),
        });
        return (change.PublicUrl, addressChanged, settings);
    }

    /// <summary>Nur das eigene Zertifikat tauschen (Oberfläche oder API), Adresse und Ports bleiben.</summary>
    public async Task ReplaceCertificateAsync(string certificatePem, string keyPem, AuditActor actor, CancellationToken ct = default)
    {
        var state = await GetAsync(ct);
        var v = state.View;
        if (v.Mode != HostingMode.Https || string.IsNullOrEmpty(v.Domain))
            throw ApiException.Conflict("hosting_mode", "Ein eigenes Zertifikat gibt es nur mit dem mitgelieferten HTTPS (Caddy). Beim eigenen Reverse Proxy das Zertifikat dort tauschen.");
        await ApplyAsync(new HostingRequest(HostingMode.Https, v.Domain, v.Aliases, KeepOldAddress: false, v.HttpsPort, v.HttpPort, null,
            HostingTls.Own, v.AcmeEmail, CertificatePem: certificatePem, KeyPem: keyPem), entraConfirmed: true, actor, ct);
    }

    public static CertificateInfo? Describe(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return null;
        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            var names = new List<string>();
            foreach (var ext in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
                names.AddRange(ext.EnumerateDnsNames());
            return new CertificateInfo(cert.Subject, cert.Issuer, cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), names, cert.Thumbprint);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static bool SameOrigin(string? a, string? b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y)
        && string.Equals(x.GetLeftPart(UriPartial.Authority), y.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private static HostingCurrent Parse(JsonElement? body, out string? certificatePem)
    {
        var env = new Dictionary<string, string>();
        var secrets = new HashSet<string>();
        var files = new HashSet<string>();
        certificatePem = null;
        if (body is { ValueKind: JsonValueKind.Object } b)
        {
            if (b.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) env[p.Name] = p.Value.GetString() ?? "";
            if (b.TryGetProperty("secretsSet", out var sec) && sec.ValueKind == JsonValueKind.Array)
                foreach (var v in sec.EnumerateArray()) secrets.Add(v.GetString()!);
            if (b.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array)
                foreach (var v in f.EnumerateArray()) files.Add(v.GetString()!);
            if (b.TryGetProperty("certificate", out var c) && c.ValueKind == JsonValueKind.String)
                certificatePem = c.GetString();
        }
        return new HostingCurrent(env, secrets, files);
    }
}

/// <summary>Globale Einstellungen &gt; Adresse und Zertifikat.</summary>
public static class HostingEndpoints
{
    public sealed record ApplyHostingRequest(HostingRequest Settings, bool EntraConfirmed = false);
    public sealed record EntraStartRequest(string PublicUrl);
    public sealed record EntraCompleteRequest(string PublicUrl, string Url);

    public static void MapHosting(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/platform/hosting").RequireAuthorization();

        g.MapGet("", async (CurrentUser cu, HostingService hosting, SetupService setup, YlmDbContext db, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            var state = await hosting.GetAsync(ct);
            var s = await setup.GetSettingsAsync();
            return Results.Ok(new
            {
                available = state.Available,
                reason = state.Reason,
                current = state.View,
                certificate = state.Certificate,
                dnsProviders = HostingPlan.DnsProviders,
                entra = new
                {
                    clientId = s.ClientId,
                    homeTenantId = s.HomeTenantId,
                    redirectUris = state.View.PublicUrl is null ? [] : RedirectUris(state.View.PublicUrl),
                },
                localPasskeys = await db.UserPasskeys.CountAsync(ct),
                agents = await db.Agents.CountAsync(a => a.RevokedAt == null, ct),
            });
        });

        g.MapPut("", async (ApplyHostingRequest body, CurrentUser cu, HostingService hosting, CancellationToken ct) =>
        {
            var user = await cu.RequireAsync(Permission.ManageSettings);
            var (publicUrl, addressChanged, settings) = await hosting.ApplyAsync(body.Settings, body.EntraConfirmed, AuditService.User(user), ct);
            return Results.Accepted(value: new { publicUrl, addressChanged, settings });
        });

        g.MapGet("/root-ca", async (CurrentUser cu, UpdateService updates, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            var (status, body) = await updates.UpdaterGetAsync("/hosting/root-ca", ct);
            if (status != HttpStatusCode.OK || body is not { } b || !b.TryGetProperty("pem", out var pem))
                throw ApiException.NotFound("Stammzertifikat (Caddy läuft nicht oder nutzt keine interne Zertifizierungsstelle)");
            return Results.File(System.Text.Encoding.ASCII.GetBytes(pem.GetString()!), "application/x-x509-ca-cert", "ylm-root-ca.crt");
        });

        // Redirect-URIs der eigenen App-Registrierung ergänzen: interaktive Anmeldung eines Admins des Home-Tenants
        // (wie im Setup-Assistenten), danach trägt die Plattform die neuen Adressen per Graph ein. Die alten bleiben.
        g.MapPost("/entra/start", async (EntraStartRequest body, CurrentUser cu, SetupService setup, HttpRequest req) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            var s = await setup.GetSettingsAsync();
            if (s.ClientId is null) throw ApiException.Conflict("app_missing", "Die Plattform hat noch keine App-Registrierung.");
            var uris = RedirectUris(ValidUrl(body.PublicUrl));
            var (url, redirectUri, _) = setup.StartInteractiveSignIn(req, allowAutomatic: false);
            return Results.Ok(new { url, redirectUri, redirectUris = uris });
        });

        g.MapPost("/entra/complete", async (EntraCompleteRequest body, CurrentUser cu, SetupService setup, IEntraBootstrap bootstrap,
            SetupSignInStore signIns, AuditService audit, CancellationToken ct) =>
        {
            var user = await cu.RequireAsync(Permission.ManageSettings);
            var uris = RedirectUris(ValidUrl(body.PublicUrl));
            var s = await setup.GetSettingsAsync();
            if (s.ClientId is not { } clientId) throw ApiException.Conflict("app_missing", "Die Plattform hat noch keine App-Registrierung.");
            var result = await setup.CompleteInteractiveSignInAsync(body.Url, null, null, ct);
            if (result.Status != "signed_in" || signIns.Current is not { } signIn)
                throw ApiException.BadRequest("signin_failed", result.Message ?? "Die Anmeldung ist fehlgeschlagen.");
            try
            {
                if (s.HomeTenantId is { } home && signIn.Admin.TenantId != home)
                    throw ApiException.BadRequest("wrong_tenant", $"Angemeldet im Tenant {signIn.Admin.OrganizationName}. Die App-Registrierung liegt in einem anderen Tenant ({home}). Bitte mit einem Admin dieses Tenants anmelden.");
                var all = await bootstrap.AddRedirectUrisAsync(signIn.AccessToken, clientId, uris, ct);
                await audit.WriteAsync(AuditService.User(user), AuditActions.RedirectUrisAdded, details: new { added = uris, signedInAs = signIn.Admin.Upn });
                return Results.Ok(new { redirectUris = all });
            }
            finally
            {
                signIns.Clear();
            }
        });
    }

    public static IReadOnlyList<string> RedirectUris(string publicUrl)
    {
        var b = publicUrl.TrimEnd('/');
        return [b + "/signin-oidc", b + "/api/tenants/consent-callback"];
    }

    private static string ValidUrl(string url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == "https" || u.IsLoopback)
            ? u.GetLeftPart(UriPartial.Authority)
            : throw ApiException.BadRequest("invalid_url", "Ungültige Adresse.");
}
