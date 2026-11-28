using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Security;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Secrets;

/// <summary>Zustand des App-Zertifikats für die Anzeige (Globale Einstellungen, Übersicht).</summary>
public sealed record CertificateStatus(
    string State,
    string? Thumbprint,
    DateTimeOffset? NotAfter,
    int? DaysLeft,
    int RenewalDays,
    bool AutoRenew,
    bool AutoRenewPossible,
    DateTimeOffset? NextCreatedAt,
    string? NextThumbprint,
    DateTimeOffset? LastAttemptAt,
    string? LastError);

/// <summary>
/// Überwacht das Zertifikat, mit dem sich die Plattform in allen Tenants anmeldet, und erneuert es rechtzeitig.
/// Ablauf: neues Zertifikat erzeugen, per Graph <c>addKey</c> an die eigene App-Registrierung hängen (der Nachweis
/// ist ein mit dem aktuellen Zertifikat signiertes JWT, dafür braucht die App kein zusätzliches Recht), warten, bis
/// Entra mit dem neuen Zertifikat Token ausgibt, dann umschalten. Das alte Zertifikat läuft in Entra von selbst ab.
/// Geht das nicht (Zertifikat schon abgelaufen, App manuell angelegt und Objekt-ID unbekannt), lädt der Admin das
/// vorbereitete Zertifikat selbst in Entra hoch und aktiviert es hier.
/// </summary>
public sealed class CertificateRenewalService(
    YlmDbContext db,
    AppCertificateService certificates,
    IGraphClientFactory graphs,
    AuditService audit,
    IOptions<YlmOptions> options,
    ILogger<CertificateRenewalService> logger)
{
    /// <summary>Audience für den addKey-Nachweis (Azure AD Graph, so von Microsoft vorgegeben).</summary>
    public const string ProofAudience = "00000002-0000-0000-c000-000000000000";
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(6);

    private int RenewalDays => Math.Max(1, options.Value.AppCertificateRenewalDays);

    public async Task<CertificateStatus> StatusAsync(CancellationToken ct = default)
    {
        var s = await db.Installation.AsNoTracking().FirstOrDefaultAsync(ct);
        var cert = await certificates.GetAsync(ct);
        var next = await certificates.GetNextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? notAfter = cert is null ? null : new DateTimeOffset(cert.NotAfter.ToUniversalTime());
        int? daysLeft = notAfter is { } na ? (int)Math.Floor((na - now).TotalDays) : null;
        var state = notAfter is null ? "missing"
            : notAfter <= now ? "expired"
            : daysLeft <= 7 ? "critical"
            : daysLeft <= RenewalDays ? "warning"
            : "ok";
        return new CertificateStatus(state, cert?.Thumbprint, notAfter, daysLeft, RenewalDays, options.Value.AutoRenewAppCertificate,
            AutoRenewPossible: s?.ClientId is not null && s.HomeTenantId is not null && state != "expired",
            NextCreatedAt: next is null ? null : new DateTimeOffset(next.NotBefore.ToUniversalTime()).AddDays(1),
            NextThumbprint: next?.Thumbprint,
            s?.CertificateRenewalAttemptAt, s?.CertificateRenewalError);
    }

    /// <summary>Erneuert automatisch über Graph. Wirft mit verständlicher Meldung, wenn das nicht geht.</summary>
    public async Task<CertificateStatus> RenewAsync(AuditActor actor, CancellationToken ct = default)
    {
        var s = await db.Installation.FirstAsync(ct);
        if (s.ClientId is null || s.HomeTenantId is null)
            throw ApiException.Conflict("setup_incomplete", L.T("Die App-Registrierung der Installation ist noch nicht eingerichtet."));
        var current = await certificates.GetAsync(ct) ?? throw ApiException.Conflict("setup_incomplete", L.T("Das App-Zertifikat fehlt."));
        if (current.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw ApiException.Conflict("certificate_expired",
                L.T("Das App-Zertifikat ist abgelaufen. Automatisch geht es nicht mehr: Bitte ein neues Zertifikat vorbereiten, in Entra hochladen und aktivieren."));

        // Ein Nachfolger aus einem früheren Versuch wartet vielleicht nur auf Entra.
        if (await certificates.GetNextAsync(ct) is not null && await TryActivateAsync(actor, ct)) return await StatusAsync(ct);

        s.CertificateRenewalAttemptAt = DateTimeOffset.UtcNow;
        X509Certificate2? next = null;
        try
        {
            var graph = await graphs.ForTenantAsync(s.HomeTenantId.Value, ct);
            var objectId = s.ApplicationObjectId ?? await graph.GetApplicationObjectIdAsync(s.ClientId.Value, ct)
                ?? throw ApiException.Conflict("certificate_manual",
                    L.T("Die Plattform kennt die Objekt-ID ihrer App-Registrierung nicht (App wurde von Hand angelegt). Bitte das neue Zertifikat vorbereiten, in Entra hochladen und aktivieren."));
            s.ApplicationObjectId = objectId;
            next = await certificates.CreateNextAsync(s.OrganizationName, ct);
            await graph.AddApplicationCertificateAsync(objectId, next.Export(X509ContentType.Cert),
                $"YLM {DateTime.UtcNow:yyyy-MM-dd}", Proof(current, objectId), ct);
            s.CertificateRenewalError = null;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Neues App-Zertifikat {Thumbprint} in Entra hinterlegt", next.Thumbprint);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (next is not null) await certificates.DiscardNextAsync(ct);
            var message = ex is GraphException g
                ? L.F($"Entra hat das neue Zertifikat abgelehnt ({g.StatusCode}{(g.GraphCode is null ? "" : ", " + g.GraphCode)}): {g.Message}")
                : ex.Message;
            s.CertificateRenewalError = message;
            audit.Add(actor, AuditActions.AppCertificateRenewalFailed, details: new { error = message });
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Erneuerung des App-Zertifikats fehlgeschlagen: {Message}", message);
            throw ex as ApiException ?? new ApiException(502, "certificate_renewal_failed", message);
        }

        await TryActivateAsync(actor, ct);
        return await StatusAsync(ct);
    }

    /// <summary>Legt einen Nachfolger an, den der Admin selbst in Entra hochlädt (Fallback ohne Graph).</summary>
    public async Task<X509Certificate2> PrepareManualAsync(CancellationToken ct = default)
    {
        var existing = await certificates.GetNextAsync(ct);
        if (existing is not null) return existing;
        var s = await db.Installation.AsNoTracking().FirstAsync(ct);
        return await certificates.CreateNextAsync(s.OrganizationName, ct);
    }

    /// <summary>Schaltet auf den Nachfolger um, sobald Entra damit Token ausgibt. False = Entra nimmt ihn (noch) nicht an.</summary>
    public async Task<bool> TryActivateAsync(AuditActor actor, CancellationToken ct = default)
    {
        var s = await db.Installation.FirstAsync(ct);
        var next = await certificates.GetNextAsync(ct);
        if (next is null || s.HomeTenantId is null) return false;
        try
        {
            await graphs.TestCertificateAsync(s.HomeTenantId.Value, next, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogInformation("Neues App-Zertifikat wird von Entra noch nicht angenommen: {Message}", ex.Message);
            return false;
        }
        var old = await certificates.GetAsync(ct);
        var promoted = (await certificates.PromoteNextAsync(ct))!;
        s.CertificateThumbprint = promoted.Thumbprint;
        s.CertificateExpiresAt = promoted.NotAfter.ToUniversalTime();
        s.CertificateRenewalError = null;
        s.CertificateWarnedAt = null;
        audit.Add(actor, AuditActions.AppCertificateRenewed, details: new
        {
            oldThumbprint = old?.Thumbprint, oldNotAfter = old?.NotAfter.ToUniversalTime(),
            newThumbprint = promoted.Thumbprint, newNotAfter = promoted.NotAfter.ToUniversalTime(),
        });
        await db.SaveChangesAsync(ct);
        graphs.Reset();
        logger.LogInformation("App-Zertifikat gewechselt auf {Thumbprint}, gültig bis {NotAfter:yyyy-MM-dd}", promoted.Thumbprint, promoted.NotAfter);
        return true;
    }

    public async Task DiscardNextAsync(CancellationToken ct = default) => await certificates.DiscardNextAsync(ct);

    /// <summary>Hintergrundlauf (jede Minute): Nachfolger aktivieren, rechtzeitig erneuern, vor dem Ablauf warnen.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var s = await db.Installation.FirstOrDefaultAsync(ct);
        if (s?.IsSetupComplete != true) return;
        if (await certificates.GetNextAsync(ct) is not null && await TryActivateAsync(AuditService.System, ct)) return;

        var status = await StatusAsync(ct);
        if (status.State is "ok" or "missing") return;
        var now = DateTimeOffset.UtcNow;

        if (status.AutoRenew && status.AutoRenewPossible && status.NextThumbprint is null
            && (s.CertificateRenewalAttemptAt is null || s.CertificateRenewalAttemptAt < now - RetryAfterFailure))
        {
            try
            {
                await RenewAsync(AuditService.System, ct);
                if ((await StatusAsync(ct)).State == "ok") return;
            }
            catch (ApiException)
            {
                // Fehler steht in den Einstellungen und im Audit-Log; gewarnt wird unten trotzdem.
            }
        }

        // Warnung: wöchentlich, in der letzten Woche täglich. Webhooks und Mail-Alarme hängen am Audit-Eintrag.
        var interval = status.DaysLeft is <= 7 ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
        await db.Entry(s).ReloadAsync(ct);
        if (s.CertificateWarnedAt is null || s.CertificateWarnedAt < now - interval + TimeSpan.FromMinutes(5))
        {
            s.CertificateWarnedAt = now;
            audit.Add(AuditService.System, AuditActions.AppCertificateExpiring, target: status.Thumbprint, details: new
            {
                notAfter = status.NotAfter, daysLeft = status.DaysLeft, state = status.State,
                autoRenew = status.AutoRenew, lastError = s.CertificateRenewalError,
            });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Nachweis für addKey: JWT, signiert mit dem aktuell gültigen Zertifikat der App.</summary>
    public static string Proof(X509Certificate2 current, string applicationObjectId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var header = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", x5t = B64Url(current.GetCertHash()), kid = current.Thumbprint });
        var payload = JsonSerializer.Serialize(new { aud = ProofAudience, iss = applicationObjectId, nbf = now, exp = now + 600 });
        var data = B64Url(Encoding.UTF8.GetBytes(header)) + "." + B64Url(Encoding.UTF8.GetBytes(payload));
        using var rsa = current.GetRSAPrivateKey() ?? throw new InvalidOperationException(L.T("Das App-Zertifikat hat keinen privaten RSA-Schlüssel."));
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return data + "." + B64Url(signature);
    }

    private static string B64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class CertificateEndpoints
{
    public static void MapAppCertificate(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/platform/certificate").RequireAuthorization();

        g.MapGet("", async (CurrentUser cu, CertificateRenewalService renewal, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            return Results.Ok(await renewal.StatusAsync(ct));
        });
        g.MapPost("/renew", async (CurrentUser cu, CertificateRenewalService renewal, CancellationToken ct) =>
        {
            var user = await cu.RequireAsync(Permission.ManageSettings);
            return Results.Ok(await renewal.RenewAsync(AuditService.User(user), ct));
        });
        // Fallback von Hand: Zertifikat vorbereiten, .cer herunterladen, in Entra hochladen, aktivieren.
        g.MapPost("/prepare", async (CurrentUser cu, CertificateRenewalService renewal, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            await renewal.PrepareManualAsync(ct);
            return Results.Ok(await renewal.StatusAsync(ct));
        });
        g.MapGet("/next.cer", async (CurrentUser cu, AppCertificateService certificates, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            var next = await certificates.GetNextAsync(ct) ?? throw new ApiException(404, "not_found", L.T("Es ist kein neues Zertifikat vorbereitet."));
            return Results.File(next.Export(X509ContentType.Cert), "application/pkix-cert", $"ylm-app-{DateTime.UtcNow:yyyyMMdd}.cer");
        });
        g.MapPost("/activate", async (CurrentUser cu, CertificateRenewalService renewal, CancellationToken ct) =>
        {
            var user = await cu.RequireAsync(Permission.ManageSettings);
            if (!await renewal.TryActivateAsync(AuditService.User(user), ct))
                throw ApiException.Conflict("certificate_not_accepted",
                    L.T("Entra nimmt das neue Zertifikat noch nicht an. Ist es in der App-Registrierung unter „Zertifikate und Geheimnisse“ hochgeladen? Nach dem Hochladen kann es ein bis zwei Minuten dauern."));
            return Results.Ok(await renewal.StatusAsync(ct));
        });
        g.MapDelete("/next", async (CurrentUser cu, CertificateRenewalService renewal, CancellationToken ct) =>
        {
            await cu.RequireAsync(Permission.ManageSettings);
            await renewal.DiscardNextAsync(ct);
            return Results.NoContent();
        });
    }
}
