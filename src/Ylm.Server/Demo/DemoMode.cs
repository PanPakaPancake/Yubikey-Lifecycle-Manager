using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Integrations;
using Ylm.Server.Inventory;
using Ylm.Server.Security;
using Ylm.Server.Tenants;

namespace Ylm.Server.Demo;

/// <summary>
/// Demo-Modus (Ylm:DemoMode=true, docker-compose.demo.yml): startet ohne Entra-Anbindung mit erfundenen Tenants und
/// Beispieldaten. Anmeldung per Rollenwahl ohne Passwort, Mails werden nicht verschickt, es geht keine Anfrage an Microsoft.
/// Nur für eine eigene, leere Datenbank: Eine echte Installation startet nie im Demo-Modus und umgekehrt.
/// </summary>
public static class DemoMode
{
    /// <summary>Markierung im Datenordner: Diese Datenbank enthält Demodaten.</summary>
    public const string MarkerFile = "demo-mode";

    public sealed record DemoAccount(PlatformRole Role, string Upn, string DisplayName, string Description);

    public static readonly DemoAccount[] Accounts =
    [
        new(PlatformRole.Owner, "owner@demo.example", "Olivia Owner", "Darf alles, auch Einstellungen, Rollen und Tenants."),
        new(PlatformRole.Operator, "operator@demo.example", "Oskar Operator", "Registriert, sperrt und verwaltet Keys."),
        new(PlatformRole.Helpdesk, "helpdesk@demo.example", "Hanna Helpdesk", "Sucht Personen und Keys, sperrt verlorene Keys."),
        new(PlatformRole.Auditor, "auditor@demo.example", "Arne Auditor", "Liest alles, ändert nichts."),
    ];

    public static void AddDemoServices(this IServiceCollection services)
    {
        services.AddSingleton<DemoGraphFactory>();
        foreach (var d in services.Where(d => d.ServiceType == typeof(IGraphClientFactory) || d.ServiceType == typeof(IMailSender)).ToList()) services.Remove(d);
        services.AddSingleton<IGraphClientFactory>(sp => sp.GetRequiredService<DemoGraphFactory>());
        services.AddSingleton<IMailSender, DemoMailSender>();
    }

    /// <summary>Verhindert, dass eine echte Datenbank im Demo-Modus (oder die Demo-Datenbank im Echtbetrieb) startet.</summary>
    public static async Task GuardAsync(YlmDbContext db, string dataDir, bool demoMode)
    {
        var marker = File.Exists(Path.Combine(dataDir, MarkerFile));
        var settings = await db.Installation.AsNoTracking().FirstOrDefaultAsync();
        var used = settings?.IsSetupComplete == true || await db.Keys.AnyAsync();
        if (demoMode && used && !marker)
            throw new InvalidOperationException("Demo-Modus abgebrochen: Diese Datenbank gehört zu einer eingerichteten Installation. " +
                                                "Die Demo braucht eine eigene, leere Datenbank (docker-compose.demo.yml).");
        if (!demoMode && marker)
            throw new InvalidOperationException("Dieser Datenordner gehört zur Demo (Datei 'demo-mode'). Für den Echtbetrieb eine neue Installation " +
                                                "mit eigener Datenbank anlegen, statt die Demodaten weiterzuverwenden.");
    }

    public static void MapDemo(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/demo", () => Results.Ok(Accounts.Select(a => new { Role = a.Role.ToString(), a.DisplayName, a.Upn, a.Description }))).AllowAnonymous();

        app.MapPost("/auth/demo-login", async (DemoLoginRequest body, HttpContext ctx, YlmDbContext db, AuditService audit) =>
        {
            var account = Accounts.FirstOrDefault(a => a.Role == body.Role) ?? throw ApiException.BadRequest("invalid", "Unbekannte Rolle.");
            var user = await db.PlatformUsers.FirstOrDefaultAsync(u => u.Upn == account.Upn)
                       ?? throw ApiException.NotFound("Demokonto");
            user.LastLoginAt = DateTimeOffset.UtcNow;
            audit.Add(AuditService.User(user), AuditActions.Login, details: new { mode = "demo" });
            await db.SaveChangesAsync();
            await ctx.SignInAsync(AuthSchemes.Cookie, PlatformSignIn.BuildPrincipal(user, "Demo"));
            return Results.Ok(new { user.Id, user.Upn, Role = user.Role.ToString() });
        }).AllowAnonymous().RequireRateLimiting(SecurityHeaders.AuthRateLimit);
    }

    public sealed record DemoLoginRequest(PlatformRole Role);
}

/// <summary>Demo: Mails (Berichte, Alarme) werden nur protokolliert, nie verschickt.</summary>
public sealed class DemoMailSender(ILogger<DemoMailSender> logger) : IMailSender
{
    public Task SendAsync(SmtpSettings settings, string? password, OutgoingMail mail, CancellationToken ct = default)
    {
        logger.LogInformation("Demo-Modus: Mail an {To} nicht verschickt ({Subject})", string.Join(", ", mail.To), mail.Subject);
        return Task.CompletedTask;
    }
}
