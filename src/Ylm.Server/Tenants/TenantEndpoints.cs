using System.IO.Compression;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Reporting;
using Ylm.Server.Security;
using Ylm.Server.Setup;

namespace Ylm.Server.Tenants;

public static class TenantEndpoints
{
    public sealed record ConsentRequest(string? TenantHint);
    public sealed record FixRequest(List<string> CheckIds);
    public sealed record UpdateTenantRequest(string? DisplayName, List<GroupRef>? TargetIncludeGroups, List<GroupRef>? TargetExcludeGroups, int? SyncIntervalMinutes, bool? RequireBreakglassApproval = null);
    public sealed record OffboardRequest(bool Confirm);

    private sealed record ConsentState(Guid UserId, DateTimeOffset ExpiresAt, string Nonce);

    public static object ToDto(Tenant t) => new
    {
        t.Id,
        t.EntraTenantId,
        t.DisplayName,
        t.DefaultDomain,
        Status = t.Status.ToString(),
        t.CreatedAt,
        t.ConsentedAt,
        t.ConsentedBy,
        t.TargetIncludeGroups,
        t.TargetExcludeGroups,
        t.SyncIntervalMinutes,
        t.LastSyncAt,
        t.LastSyncSucceeded,
        t.LastSyncMessage,
        t.TargetUserCount,
        t.LastCheckAt,
        Checks = TenantCheckService.ReadChecks(t).Select(c => new { c.Id, c.Title, Status = c.Status.ToString(), c.Message, c.CanFix, c.ManualInstructions }),
        t.OffboardedAt,
        t.DataDeletionDueAt,
        t.RequireBreakglassApproval,
    };

    private static List<GroupRef> NormalizeGroups(IEnumerable<GroupRef> groups)
    {
        var list = groups.Where(g => !string.IsNullOrWhiteSpace(g.Id)).DistinctBy(g => g.Id)
            .Select(g => new GroupRef(g.Id.Trim(), string.IsNullOrWhiteSpace(g.Name) ? g.Id.Trim() : g.Name.Trim())).ToList();
        if (list.Count > 50) throw ApiException.BadRequest("invalid", "Höchstens 50 Gruppen.");
        return list;
    }

    public static void MapTenants(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/tenants").RequireAuthorization();

        g.MapGet("", async (CurrentUser cu) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            var tenants = await (await cu.AccessibleTenantsAsync()).OrderBy(t => t.DisplayName).ToListAsync();
            return Results.Ok(tenants.Select(ToDto));
        });

        g.MapGet("/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireTenantAsync(id, Permission.ReadInventory);
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            return Results.Ok(ToDto(t));
        });

        g.MapPost("/consent", async (ConsentRequest body, CurrentUser cu, SetupService setup, IDataProtectionProvider dp, HttpRequest req, IOptions<YlmOptions> opt) =>
        {
            var user = await cu.RequireAsync(Permission.ManageTenants);
            var s = await setup.GetSettingsAsync();
            if (s.ClientId is null) throw ApiException.Conflict("setup_incomplete", "Die App-Registrierung fehlt.");
            var state = dp.CreateProtector("Ylm.Consent").Protect(System.Text.Json.JsonSerializer.Serialize(
                new ConsentState(user.Id, DateTimeOffset.UtcNow.AddMinutes(15), TokenHashing.NewSecret(8))));
            var tenantSegment = string.IsNullOrWhiteSpace(body.TenantHint) ? "organizations" : Uri.EscapeDataString(body.TenantHint.Trim());
            var redirect = setup.BaseUrl(s, req) + "/api/tenants/consent-callback";
            var url = $"{opt.Value.LoginAuthority.TrimEnd('/')}/{tenantSegment}/v2.0/adminconsent" +
                      $"?client_id={s.ClientId}&scope={Uri.EscapeDataString(opt.Value.GraphBaseUrl.TrimEnd('/') + "/.default")}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirect)}&state={Uri.EscapeDataString(state)}";
            return Results.Ok(new { url });
        });

        // Redirect target after the admin granted consent in the customer tenant.
        g.MapGet("/consent-callback", async (HttpRequest req, CurrentUser cu, IDataProtectionProvider dp, YlmDbContext db,
            IGraphClientFactory graphFactory, TenantCheckService checks, AuditService audit, IOptions<YlmOptions> opt) =>
        {
            var user = await cu.RequireAsync(Permission.ManageTenants);
            var error = req.Query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
                return Results.Redirect("/tenants?consentError=" + Uri.EscapeDataString(req.Query["error_description"].ToString() is { Length: > 0 } d ? d : error));
            ConsentState? state;
            try
            {
                state = System.Text.Json.JsonSerializer.Deserialize<ConsentState>(dp.CreateProtector("Ylm.Consent").Unprotect(req.Query["state"].ToString()));
            }
            catch (Exception) { state = null; }
            if (state is null || state.UserId != user.Id || state.ExpiresAt < DateTimeOffset.UtcNow)
                return Results.Redirect("/tenants?consentError=" + Uri.EscapeDataString("Ungültiger oder abgelaufener Consent-Vorgang."));
            if (!Guid.TryParse(req.Query["tenant"], out var entraTenantId) || !string.Equals(req.Query["admin_consent"], "True", StringComparison.OrdinalIgnoreCase))
                return Results.Redirect("/tenants?consentError=" + Uri.EscapeDataString("Der Admin-Consent wurde nicht erteilt."));

            var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.EntraTenantId == entraTenantId);
            var isNew = tenant is null;
            tenant ??= new Tenant { EntraTenantId = entraTenantId, SyncIntervalMinutes = opt.Value.DefaultSyncIntervalMinutes };
            tenant.Status = TenantStatus.Active;
            tenant.ConsentedAt = DateTimeOffset.UtcNow;
            tenant.ConsentedBy = user.Upn;
            tenant.OffboardedAt = null;
            tenant.DataDeletionDueAt = null;
            if (isNew) db.Tenants.Add(tenant);
            try
            {
                // A token issued before this consent would still carry the old permissions.
                graphFactory.Invalidate(entraTenantId);
                // Consent propagation can take a few seconds.
                await Task.Delay(TimeSpan.FromSeconds(isNew ? 2 : 0));
                var org = await (await graphFactory.ForTenantAsync(entraTenantId)).GetOrganizationAsync();
                tenant.DisplayName = string.IsNullOrWhiteSpace(org.DisplayName) ? org.DefaultDomain ?? entraTenantId.ToString() : org.DisplayName;
                tenant.DefaultDomain = org.DefaultDomain;
            }
            catch (Exception)
            {
                if (string.IsNullOrEmpty(tenant.DisplayName)) tenant.DisplayName = entraTenantId.ToString();
            }
            audit.Add(AuditService.User(user), AuditActions.TenantOnboarded, tenant.Id, target: tenant.DisplayName, details: new { entraTenantId, isNew });
            await db.SaveChangesAsync();
            try { await checks.RunAsync(tenant, AuditService.User(user)); }
            catch (Exception) { /* checks can be repeated from the UI */ }
            return Results.Redirect($"/tenants/{tenant.Id}?onboarded=1");
        });

        g.MapPatch("/{id:guid}", async (Guid id, UpdateTenantRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.ManageTenants);
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            if (body.DisplayName is { Length: > 0 } name) t.DisplayName = name.Trim();
            if (body.TargetIncludeGroups is not null) t.TargetIncludeGroups = NormalizeGroups(body.TargetIncludeGroups);
            if (body.TargetExcludeGroups is not null) t.TargetExcludeGroups = NormalizeGroups(body.TargetExcludeGroups);
            if (t.TargetIncludeGroups.Any(i => t.TargetExcludeGroups.Any(e => e.Id == i.Id)))
                throw ApiException.BadRequest("invalid", "Eine Gruppe kann nicht gleichzeitig ein- und ausgeschlossen sein.");
            if (body.SyncIntervalMinutes is { } interval)
            {
                if (interval is < 5 or > 1440) throw ApiException.BadRequest("invalid", "Das Sync-Intervall muss zwischen 5 und 1440 Minuten liegen.");
                t.SyncIntervalMinutes = interval;
            }
            if (body.RequireBreakglassApproval is { } four) t.RequireBreakglassApproval = four;
            audit.Add(AuditService.User(user), AuditActions.TenantUpdated, t.Id, details: body);
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(t));
        });

        g.MapPost("/{id:guid}/checks", async (Guid id, CurrentUser cu, YlmDbContext db, TenantCheckService checks) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.ReadInventory);
            if (!CurrentUser.Has(user, Permission.ManageTenants) && !CurrentUser.Has(user, Permission.SyncTenants)) throw ApiException.Forbidden();
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            await checks.RunAsync(t, AuditService.User(user), freshToken: true);
            return Results.Ok(ToDto(t));
        });

        g.MapPost("/{id:guid}/fix", async (Guid id, FixRequest body, CurrentUser cu, YlmDbContext db, TenantCheckService checks) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.ManageTenants);
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            await checks.FixAsync(t, body.CheckIds, AuditService.User(user));
            return Results.Ok(ToDto(t));
        });

        g.MapGet("/{id:guid}/groups", async (Guid id, string q, CurrentUser cu, YlmDbContext db, IGraphClientFactory gf) =>
        {
            await cu.RequireTenantAsync(id, Permission.ManageTenants);
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            return Results.Ok(await (await gf.ForTenantAsync(t.EntraTenantId)).SearchGroupsAsync(q));
        });

        g.MapGet("/{id:guid}/users", async (Guid id, string q, CurrentUser cu, YlmDbContext db, IGraphClientFactory gf) =>
        {
            await cu.RequireTenantAsync(id, Permission.RegisterKeys);
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            var graph = await gf.ForTenantAsync(t.EntraTenantId);
            var users = await graph.SearchUsersAsync(q);
            var upns = users.Select(u => u.UserPrincipalName.ToLower()).ToList();
            var keyCounts = await db.Registrations.Where(r => r.TenantId == id && r.RemovedAt == null && upns.Contains(r.UserPrincipalName.ToLower()))
                .GroupBy(r => r.UserPrincipalName.ToLower()).Select(x => new { Upn = x.Key, Count = x.Count() }).ToListAsync();
            // Passkeys straight from Entra, so a person with a key registered elsewhere (or not synced yet) does not
            // show up as "no key". A failed lookup only leaves that person's Entra status unknown (null).
            using var gate = new SemaphoreSlim(5);
            var passkeys = await Task.WhenAll(users.Select(async u =>
            {
                await gate.WaitAsync();
                try
                {
                    var methods = await graph.ListFido2MethodsAsync(u.Id);
                    return methods.Select(m => new { m.DisplayName, m.Model, IsYubiKey = YubicoAaguids.IsYubiKey(m.AaGuid, m.Model) }).ToList();
                }
                catch (Exception) { return null; }
                finally { gate.Release(); }
            }));
            return Results.Ok(users.Select((u, i) => new
            {
                u.Id, u.UserPrincipalName, u.DisplayName, u.Mail, u.Department, u.AccountEnabled,
                ActiveKeys = keyCounts.FirstOrDefault(k => k.Upn == u.UserPrincipalName.ToLower())?.Count ?? 0,
                EntraPasskeys = passkeys[i],
            }));
        });

        g.MapGet("/{id:guid}/export.zip", async (Guid id, CurrentUser cu, YlmDbContext db, ExportService exports, AuditService audit) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.ManageTenants);
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                async Task Add(string name, byte[] content)
                {
                    var e = zip.CreateEntry(name);
                    await using var s = e.Open();
                    await s.WriteAsync(content);
                }
                await Add("keys.csv", await exports.KeysCsvAsync([id]));
                await Add("registrations.csv", await exports.RegistrationsCsvAsync([id]));
                await Add("audit.csv", await exports.AuditCsvAsync([id], null, null));
            }
            await audit.WriteAsync(AuditService.User(user), AuditActions.ExportCreated, id, details: new { kind = "tenant-export" });
            return Results.File(ms.ToArray(), "application/zip", $"ylm-export-{t.DefaultDomain ?? t.Id.ToString()}.zip");
        });

        // Offboarding: platform stops using the tenant; data is deleted after the retention period.
        g.MapPost("/{id:guid}/offboard", async (Guid id, OffboardRequest body, CurrentUser cu, YlmDbContext db, AuditService audit, SetupService setup, IOptions<YlmOptions> opt) =>
        {
            var user = await cu.RequireTenantAsync(id, Permission.ManageTenants);
            if (!body.Confirm) throw ApiException.BadRequest("confirm", "Bitte das Offboarding bestätigen.");
            var t = await db.Tenants.FindAsync(id) ?? throw ApiException.NotFound("Tenant");
            t.Status = TenantStatus.Offboarded;
            t.OffboardedAt = DateTimeOffset.UtcNow;
            t.DataDeletionDueAt = DateTimeOffset.UtcNow.AddDays(opt.Value.OffboardingRetentionDays);
            // API keys restricted to tenants lose this tenant; a key that then has no tenant left is revoked.
            foreach (var k in await db.ApiKeys.Where(k => !k.AllTenants && k.RevokedAt == null && k.TenantIds.Contains(id)).ToListAsync())
            {
                k.TenantIds = k.TenantIds.Where(x => x != id).ToList();
                if (k.TenantIds.Count == 0) k.RevokedAt = DateTimeOffset.UtcNow;
            }
            audit.Add(AuditService.User(user), AuditActions.TenantOffboarded, t.Id, target: t.DisplayName, details: new { t.DataDeletionDueAt });
            await db.SaveChangesAsync();
            var s = await setup.GetSettingsAsync();
            return Results.Ok(new
            {
                tenant = ToDto(t),
                // The app has no right to delete its own service principal in the customer tenant.
                removeServicePrincipalUrl = $"https://entra.microsoft.com/#view/Microsoft_AAD_IAM/ManagedAppMenuBlade/~/Overview/appId/{s.ClientId}",
                instructions = "Entfernen Sie im Kunden-Tenant die Unternehmensanwendung „Yubikey Lifecycle Manager“ (Entra Admin Center → Unternehmensanwendungen → Eigenschaften → Löschen). " +
                               $"Die Daten in der Plattform werden am {t.DataDeletionDueAt:dd.MM.yyyy} gelöscht; vorher können Sie sie exportieren.",
            });
        });
    }
}
