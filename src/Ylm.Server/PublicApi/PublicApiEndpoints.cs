using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Inventory;
using Ylm.Server.Registration;
using Ylm.Server.Reporting;
using Ylm.Server.Security;
using Ylm.Contracts.Localization;

namespace Ylm.Server.PublicApi;

/// <summary>
/// REST API for external tools. API keys are managed on platform level, are valid for all tenants or only for selected ones,
/// and carry their own rights (ApiScopes): reading per area and a few deliberately chosen write functions.
/// Dangerous operations (reset, retire, delete keys, remove sign-in methods, TAP) are not available over the API.
/// </summary>
public static class PublicApiEndpoints
{
    public const string GroupName = "v1";

    public sealed record ApiKeyDto(string Id, string TenantId, string? Tenant, string? SerialNumber, string? Model, string? Firmware, string Status, string? LockReason, string Source,
        IReadOnlyList<string> Tags, IReadOnlyDictionary<string, string?> CustomFields, IReadOnlyList<ApiRegistrationDto> Registrations,
        IReadOnlyList<string> Advisories, DateTimeOffset UpdatedAt);

    public sealed record ApiRegistrationDto(string UserPrincipalName, string? UserDisplayName, string? DisplayName, DateTimeOffset RegisteredAt, string Source);

    public sealed record ApiPage<T>(int Total, int Page, int PageSize, IReadOnlyList<T> Items);

    public sealed record ApiTenant(string Id, Guid EntraTenantId, string DisplayName, string? DefaultDomain, DateTimeOffset? LastSyncAt);

    public sealed record ApiMe(string Id, string Name, string Prefix, bool AllTenants, IReadOnlyList<ApiTenant> Tenants, IReadOnlyList<string> Scopes, DateTimeOffset? ExpiresAt);

    public sealed record ApiAuditEntry(long Sequence, DateTimeOffset Timestamp, string ActorType, string? ActorName, string Action, string? TenantId, string? KeyId, string? Target, string? Details);

    /// <param name="Tags">Ersetzt alle Tags des Keys.</param>
    /// <param name="CustomFields">Nur die genannten Zusatzfelder werden geändert; leerer Wert löscht das Feld.</param>
    /// <param name="Notes">Leerer Text löscht die Notiz.</param>
    public sealed record ApiUpdateKey(List<string>? Tags, Dictionary<string, string?>? CustomFields, string? Notes);

    public sealed record ApiLockKey(LockReason Reason, string? Note);

    public sealed record ApiSummary(string Tenant, int Total, IReadOnlyDictionary<string, int> ByStatus, int UsersWithKey, int? TargetUsers, double? CoveragePercent, DateTimeOffset? LastSyncAt);

    /// <summary>Tenants the API key may read (resolved at authentication).</summary>
    private static List<Guid> Allowed(ClaimsPrincipal p) =>
        p.FindAll(YlmClaims.ApiKeyTenant).Select(c => Guid.Parse(c.Value)).ToList();

    /// <summary>
    /// The tenants a request reads: all allowed ones, or the one named in <paramref name="tenant"/>
    /// (platform id, Entra tenant id or default domain).
    /// </summary>
    private static async Task<List<Guid>> ScopeAsync(ClaimsPrincipal p, YlmDbContext db, string? tenant)
    {
        var allowed = Allowed(p);
        if (string.IsNullOrWhiteSpace(tenant)) return allowed;
        var t = tenant.Trim();
        var q = db.Tenants.AsNoTracking().Where(x => allowed.Contains(x.Id));
        var match = Guid.TryParse(t, out var g)
            ? await q.Where(x => x.Id == g || x.EntraTenantId == g).Select(x => x.Id).FirstOrDefaultAsync()
            : await q.Where(x => x.DefaultDomain != null && x.DefaultDomain.ToLower() == t.ToLower()).Select(x => x.Id).FirstOrDefaultAsync();
        if (match == Guid.Empty) throw ApiException.NotFound(L.T("Tenant"));
        return [match];
    }

    private static ApiKeyDto Map(YubiKey k) => new(
        k.Id.ToString(), k.TenantId.ToString(), k.Tenant?.DisplayName, k.SerialNumber?.ToString(), k.Model, k.FirmwareVersion, k.Status.ToString(), k.LockReason?.ToString(), k.Source.ToString(),
        k.Tags, k.CustomFields,
        k.Registrations.Where(r => r.RemovedAt == null).Select(r => new ApiRegistrationDto(r.UserPrincipalName, r.UserDisplayName, r.DisplayName, r.RegisteredAt, r.Source.ToString())).ToList(),
        FirmwareAdvisories.For(k.FirmwareVersion, k.Model, k.IsFips).Select(a => a.Id).ToList(), k.UpdatedAt);

    public static void MapPublicApi(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1").RequireAuthorization("ApiKey").WithGroupName(GroupName).WithTags("Inventar");
        const string tenantHint = " Optional tenant: ID der Plattform, Entra-Tenant-ID oder Standard-Domain; ohne Angabe alle Tenants, für die der Schlüssel gilt.";

        g.MapGet("/me", async (ClaimsPrincipal p, YlmDbContext db) =>
        {
            var id = Guid.Parse(p.FindFirstValue(YlmClaims.ApiKeyId)!);
            var key = await db.ApiKeys.AsNoTracking().FirstAsync(k => k.Id == id);
            var allowed = Allowed(p);
            var tenants = await db.Tenants.AsNoTracking().Where(t => allowed.Contains(t.Id)).OrderBy(t => t.DisplayName).ToListAsync();
            return TypedResults.Ok(new ApiMe(key.Id.ToString(), key.Name, key.Prefix, key.AllTenants,
                tenants.Select(t => new ApiTenant(t.Id.ToString(), t.EntraTenantId, t.DisplayName, t.DefaultDomain, t.LastSyncAt)).ToList(), key.Scopes, key.ExpiresAt));
        }).WithTags("Schlüssel").WithSummary("Dieser API-Schlüssel").WithDescription("Name, Tenants und Rechte des verwendeten Schlüssels. Braucht kein besonderes Recht; gut zum Prüfen der Einrichtung.");

        g.MapGet("/tenants", async (ClaimsPrincipal p, YlmDbContext db) =>
        {
            var allowed = Allowed(p);
            var tenants = await db.Tenants.AsNoTracking().Where(t => allowed.Contains(t.Id)).OrderBy(t => t.DisplayName).ToListAsync();
            return TypedResults.Ok(tenants.Select(t => new ApiTenant(t.Id.ToString(), t.EntraTenantId, t.DisplayName, t.DefaultDomain, t.LastSyncAt)).ToList());
        }).WithSummary("Tenants, für die der API-Schlüssel gilt").RequireScope(ApiScopes.InventoryRead);

        g.MapGet("/keys", async (ClaimsPrincipal p, YlmDbContext db, string? tenant, string? status, string? serial, string? upn, string? tag, int? page, int? pageSize) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            var q = db.Keys.AsNoTracking().Include(k => k.Registrations).Include(k => k.Tenant).Where(k => k.TenantId != null && tenantIds.Contains(k.TenantId.Value));
            if (Enum.TryParse<KeyStatus>(status, true, out var st)) q = q.Where(k => k.Status == st);
            if (int.TryParse(serial, out var s)) q = q.Where(k => k.SerialNumber == s);
            if (!string.IsNullOrWhiteSpace(upn)) q = q.Where(k => k.Registrations.Any(r => r.RemovedAt == null && r.UserPrincipalName.ToLower() == upn.ToLower()));
            if (!string.IsNullOrWhiteSpace(tag)) q = q.Where(k => k.Tags.Contains(tag));
            var size = Math.Clamp(pageSize ?? 100, 1, 1000);
            var pg = Math.Max(page ?? 1, 1);
            var total = await q.CountAsync();
            var items = await q.OrderBy(k => k.Tenant!.DisplayName).ThenBy(k => k.SerialNumber).Skip((pg - 1) * size).Take(size).ToListAsync();
            return TypedResults.Ok(new ApiPage<ApiKeyDto>(total, pg, size, items.Select(Map).ToList()));
        }).WithSummary("Keys auflisten").WithDescription($"Filter: tenant, status (InStock, Active, Locked, Retired), serial, upn, tag. Seitenweise.{tenantHint}").RequireScope(ApiScopes.InventoryRead);

        g.MapGet("/keys/{id:guid}", async Task<Microsoft.AspNetCore.Http.HttpResults.Results<Microsoft.AspNetCore.Http.HttpResults.Ok<ApiKeyDto>, Microsoft.AspNetCore.Http.HttpResults.NotFound>> (Guid id, ClaimsPrincipal p, YlmDbContext db) =>
        {
            var allowed = Allowed(p);
            var key = await db.Keys.AsNoTracking().Include(k => k.Registrations).Include(k => k.Tenant).FirstOrDefaultAsync(k => k.Id == id && k.TenantId != null && allowed.Contains(k.TenantId.Value));
            return key is null ? TypedResults.NotFound() : TypedResults.Ok(Map(key));
        }).WithSummary("Einen Key abrufen").RequireScope(ApiScopes.InventoryRead);

        g.MapGet("/users/{upn}/keys", async (string upn, string? tenant, ClaimsPrincipal p, YlmDbContext db) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            var keys = await db.Keys.AsNoTracking().Include(k => k.Registrations).Include(k => k.Tenant)
                .Where(k => k.TenantId != null && tenantIds.Contains(k.TenantId.Value) && k.Registrations.Any(r => r.RemovedAt == null && r.UserPrincipalName.ToLower() == upn.ToLower())).ToListAsync();
            return TypedResults.Ok(keys.Select(Map).ToList());
        }).WithSummary("Aktive Keys einer Person").WithDescription(tenantHint.Trim()).RequireScope(ApiScopes.InventoryRead);

        g.MapGet("/assignments", async (ClaimsPrincipal p, string? tenant, string? q, string? usage, YlmDbContext db, AssignmentService assignments) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            return TypedResults.Ok(await assignments.BuildAsync(tenantIds, new AssignmentFilter(q, usage)));
        }).WithSummary("Wer hat welchen Key").WithDescription($"Ausgegebene Keys mit Person bzw. Verwahrer und allen Konten darauf. Filter: q (Person, UPN, Seriennummer, Verwahrer), usage (Personal, Shared, Breakglass).{tenantHint}").RequireScope(ApiScopes.AssignmentsRead);

        g.MapGet("/advisories", async (ClaimsPrincipal p, string? tenant, YlmDbContext db, AdvisoryService advisories) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            return TypedResults.Ok(await advisories.BuildAsync(tenantIds, includePartnerStock: false));
        }).WithTags("Berichte").WithSummary("Keys mit Yubico-Sicherheitshinweis").WithDescription($"Alle bekannten Yubico-Sicherheitshinweise zur Key-Firmware mit den betroffenen Keys (nicht ausgemustert) und Personen. Die Bereiche hängen von Reihe und Modell ab (z. B. YubiKey Bio, FIPS).{tenantHint}").RequireScope(ApiScopes.ReportsRead);

        g.MapGet("/reminders", async (ClaimsPrincipal p, string? tenant, YlmDbContext db, ReminderService reminders) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            return TypedResults.Ok(await reminders.BuildAsync(tenantIds));
        }).WithTags("Berichte").WithSummary("Lebenszyklus-Erinnerungen").WithDescription($"Offene Erinnerungen der Tenants, die sie eingeschaltet haben: Keys lange nicht benutzt (UnusedKey), Personen ohne Ersatz-Key (NoSpareKey), ausgeschiedene Personen mit aktivem Key (Leaver), gesperrte oder ausgemusterte Keys mit Passkey in Entra (RetiredWithPasskey). Ausgeblendete Erinnerungen fehlen.{tenantHint}").RequireScope(ApiScopes.ReportsRead);

        g.MapGet("/summary", async (ClaimsPrincipal p, string? tenant, DashboardService dash, YlmDbContext db) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            var tenants = await db.Tenants.AsNoTracking().Where(x => tenantIds.Contains(x.Id)).ToListAsync();
            var d = await dash.BuildAsync(tenantIds);
            var name = tenants.Count == 1 ? tenants[0].DisplayName : L.F($"Alle ({tenants.Count} Tenants)");
            return TypedResults.Ok(new ApiSummary(name, d.Total, d.ByStatus, d.UsersWithKey, d.TargetUsers, d.CoveragePercent, tenants.Max(t => t.LastSyncAt)));
        }).WithTags("Berichte").WithSummary("Kennzahlen").WithDescription($"Für einen Tenant oder zusammengefasst über alle Tenants des Schlüssels.{tenantHint}").RequireScope(ApiScopes.ReportsRead);

        g.MapGet("/audit", async (ClaimsPrincipal p, YlmDbContext db, string? tenant, string? action, DateTimeOffset? from, DateTimeOffset? to, long? after, int? take) =>
        {
            var tenantIds = await ScopeAsync(p, db, tenant);
            var q = db.AuditEntries.AsNoTracking().AsQueryable();
            // Einträge ohne Tenant (Plattform, Anmeldungen, Schlüsselverwaltung) nur für Schlüssel, die für alle Tenants gelten.
            var allTenants = p.HasClaim(YlmClaims.ApiKeyAllTenants, "true") && string.IsNullOrWhiteSpace(tenant);
            q = allTenants ? q.Where(a => a.TenantId == null || tenantIds.Contains(a.TenantId.Value)) : q.Where(a => a.TenantId != null && tenantIds.Contains(a.TenantId.Value));
            if (!string.IsNullOrWhiteSpace(action)) q = q.Where(a => a.Action.StartsWith(action));
            if (from is not null) q = q.Where(a => a.Timestamp >= from);
            if (to is not null) q = q.Where(a => a.Timestamp <= to);
            if (after is not null) q = q.Where(a => a.Sequence > after);
            var items = await q.OrderBy(a => a.Sequence).Take(Math.Clamp(take ?? 500, 1, 1000)).ToListAsync();
            return TypedResults.Ok(items.Select(a => new ApiAuditEntry(a.Sequence, a.Timestamp, a.ActorType.ToString(), a.ActorName, a.Action,
                a.TenantId?.ToString(), a.KeyId?.ToString(), a.Target, a.Details)).ToList());
        }).WithTags("Audit-Log").WithSummary("Audit-Log lesen")
          .WithDescription($"Aufsteigend nach laufender Nummer. Zum fortlaufenden Abholen (z. B. SIEM) die höchste bekannte Nummer als after übergeben. Filter: action (Präfix, z. B. key.), from, to, take (max. 1000).{tenantHint}")
          .RequireScope(ApiScopes.AuditRead);

        // Schreibende Funktionen: jeweils nur mit dem passenden Recht am Schlüssel, alles landet im Audit-Log mit dem Schlüssel als Akteur.
        var w = app.MapGroup("/api/v1").RequireAuthorization("ApiKey").WithGroupName(GroupName).WithTags("Inventar");

        w.MapPatch("/keys/{id:guid}", async Task<Microsoft.AspNetCore.Http.HttpResults.Ok<ApiKeyDto>> (Guid id, ApiUpdateKey body, ClaimsPrincipal p, YlmDbContext db, KeyService keys, AuditService audit) =>
        {
            var allowed = Allowed(p);
            var key = await db.Keys.Include(k => k.Registrations).Include(k => k.Tenant).FirstOrDefaultAsync(k => k.Id == id && k.TenantId != null && allowed.Contains(k.TenantId.Value))
                ?? throw ApiException.NotFound(L.T("Key"));
            var before = new { key.Tags, key.CustomFields, key.Notes };
            if (body.Tags is not null) key.Tags = Tags.Normalize(body.Tags).ToList();
            if (body.CustomFields is not null) key.CustomFields = await keys.ValidateFieldsAsync(key.TenantId, body.CustomFields, key.CustomFields);
            if (body.Notes is not null) key.Notes = body.Notes.Length == 0 ? null : body.Notes;
            key.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add(ApiScopes.Actor(p), AuditActions.KeyUpdated, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { before, after = new { key.Tags, key.CustomFields, key.Notes } });
            await db.SaveChangesAsync();
            return TypedResults.Ok(Map(key));
        }).WithSummary("Tags, Zusatzfelder oder Notiz eines Keys ändern")
          .WithDescription("Nur angegebene Felder werden geändert. Status, Person und Entra bleiben unberührt.")
          .RequireScope(ApiScopes.InventoryWrite);

        w.MapPost("/keys/{id:guid}/lock", async Task<Microsoft.AspNetCore.Http.HttpResults.Ok<ApiKeyDto>> (Guid id, ApiLockKey body, ClaimsPrincipal p, YlmDbContext db, KeyService keys) =>
        {
            var allowed = Allowed(p);
            var key = await db.Keys.Include(k => k.Registrations).Include(k => k.Tenant).FirstOrDefaultAsync(k => k.Id == id && k.TenantId != null && allowed.Contains(k.TenantId.Value))
                ?? throw ApiException.NotFound(L.T("Key"));
            // Vier-Augen: über die API gibt es keinen zweiten Admin, aktive Breakglass-Keys nur in der Oberfläche.
            if (ApprovalService.RemovalNeedsApproval(key, key.Tenant))
                throw ApiException.Conflict("approval_required", L.T("Breakglass-Keys lassen sich über die API nicht sperren. Bitte in der Oberfläche mit Vier-Augen-Freigabe sperren."));
            await keys.LockAsync(key, body.Reason, body.Note, ApiScopes.Actor(p));
            return TypedResults.Ok(Map(key));
        }).WithSummary("Key sperren")
          .WithDescription("Wie „Sperren“ in der Oberfläche, z. B. wenn ein Ticket einen Verlust meldet: entfernt die Passkeys des Keys in Entra und setzt den Status auf gesperrt. reason: Lost, Defective, Stolen, Other. Lässt sich über die API nicht rückgängig machen. Aktive Breakglass-Keys lehnt die API ab (409 approval_required), sie brauchen die Vier-Augen-Freigabe in der Oberfläche.")
          .RequireScope(ApiScopes.KeysLock);

        app.MapGroup("/api/v1").RequireAuthorization("ApiKey").WithGroupName(GroupName).WithTags("ToDo-Liste").MapTodoApi(ScopeAsync);

        // ----- HTTPS-Zertifikat der Plattform (eigenes Zertifikat, z. B. aus der Firmen-PKI) -----
        var c = app.MapGroup("/api/v1/certificate").RequireAuthorization("ApiKey").WithGroupName(GroupName).WithTags("Zertifikat");
        c.MapGet("", async (ClaimsPrincipal p, Updates.HostingService hosting, CancellationToken ct) =>
        {
            RequireAllTenants(p);
            var state = await hosting.GetAsync(ct);
            return TypedResults.Ok(new ApiCertificate(state.View.Tls.ToString(), state.View.Domain, state.View.Aliases, state.Certificate));
        }).WithSummary("HTTPS-Zertifikat der Plattform")
          .WithDescription("Zertifikatsart (Internal, Own, LetsEncrypt, LetsEncryptDns), DNS-Name und beim eigenen Zertifikat Aussteller, Gültigkeit und Namen.")
          .RequireScope(ApiScopes.CertificateWrite);

        c.MapPut("", async Task<Microsoft.AspNetCore.Http.HttpResults.Accepted<ApiCertificate>> (ApiReplaceCertificate body, ClaimsPrincipal p, Updates.HostingService hosting, CancellationToken ct) =>
        {
            RequireAllTenants(p);
            await hosting.ReplaceCertificateAsync(body.CertificatePem, body.KeyPem, ApiScopes.Actor(p), ct);
            var info = Updates.HostingService.Describe(body.CertificatePem);
            var state = await hosting.GetAsync(ct);
            return TypedResults.Accepted((string?)null, new ApiCertificate("Own", state.View.Domain, state.View.Aliases, info));
        }).WithSummary("Eigenes HTTPS-Zertifikat ersetzen")
          .WithDescription("Zertifikat (PEM, mit Zwischenzertifikaten) und privater Schlüssel (PEM, ohne Passwort). Die Plattform prüft, dass beide zusammenpassen, gültig sind und für den DNS-Namen gelten; danach lädt Caddy das neue Zertifikat (ein paar Sekunden). Schaltet bei Bedarf von einer anderen Zertifikatsart auf das eigene Zertifikat um.")
          .RequireScope(ApiScopes.CertificateWrite);
    }

    public sealed record ApiCertificate(string Type, string? Domain, string? Aliases, Updates.CertificateInfo? Certificate);
    public sealed record ApiReplaceCertificate(string CertificatePem, string KeyPem);

    private static void RequireAllTenants(ClaimsPrincipal p)
    {
        if (!p.HasClaim(YlmClaims.ApiKeyAllTenants, "true"))
            throw ApiException.Forbidden(L.T("Das Zertifikat betrifft die ganze Installation: nur Schlüssel mit Zugriff auf alle Tenants."));
    }

    /// <param name="Scopes">Rechte des Schlüssels (siehe GET /api/api-keys/scopes). Ohne Angabe: alles Lesende, mit AllowTodoWrite zusätzlich ToDo schreiben (wie bisher).</param>
    public sealed record CreateApiKeyRequest(string Name, int? ExpiresInDays, bool AllTenants, List<Guid>? TenantIds, bool AllowTodoWrite = false, List<string>? Scopes = null);
    /// <param name="Scopes">Neue Rechte; ohne Angabe bleiben sie, AllowTodoWrite schaltet dann nur ToDo schreiben um (alte Clients).</param>
    public sealed record UpdateApiKeyRequest(bool AllTenants, List<Guid>? TenantIds, bool? AllowTodoWrite = null, List<string>? Scopes = null);

    /// <summary>Management of API keys in the platform area of the web UI (Owner).</summary>
    public static void MapApiKeyManagement(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/api-keys").RequireAuthorization();

        async Task<List<Guid>> ValidateScopeAsync(PlatformUser user, bool allTenants, List<Guid>? tenantIds, YlmDbContext db)
        {
            if (allTenants)
            {
                if (!user.AllTenants) throw ApiException.Forbidden(L.T("Nur Benutzer mit Zugriff auf alle Tenants können Schlüssel für alle Tenants erstellen."));
                return [];
            }
            var ids = (tenantIds ?? []).Distinct().ToList();
            if (ids.Count == 0) throw ApiException.BadRequest("invalid", L.T("Bitte mindestens einen Tenant auswählen oder „Alle Tenants“ wählen."));
            var existing = await db.Tenants.Where(t => ids.Contains(t.Id) && t.Status != TenantStatus.Offboarded).Select(t => t.Id).ToListAsync();
            if (existing.Count != ids.Count) throw ApiException.BadRequest("invalid", L.T("Mindestens ein ausgewählter Tenant existiert nicht oder ist offboarded."));
            if (ids.Any(id => !CurrentUser.CanAccessTenant(user, id))) throw ApiException.Forbidden(L.T("Sie sind nicht für alle ausgewählten Tenants zuständig."));
            return ids;
        }

        g.MapGet("/scopes", async (CurrentUser cu) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            return Results.Ok(ApiScopes.Catalog);
        });

        g.MapGet("", async (CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ManageApiKeys);
            var names = await db.Tenants.ToDictionaryAsync(t => t.Id, t => t.DisplayName);
            var keys = await db.ApiKeys.OrderByDescending(k => k.CreatedAt).ToListAsync();
            return Results.Ok(keys.Select(k => new
            {
                k.Id, k.Name, k.Prefix, k.CreatedAt, k.CreatedBy, k.LastUsedAt, k.ExpiresAt, k.RevokedAt, k.AllTenants, k.Scopes,
                Tenants = k.TenantIds.Select(id => new { Id = id, DisplayName = names.GetValueOrDefault(id, L.T("(gelöscht)")) }),
            }));
        });

        g.MapPost("", async (CreateApiKeyRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            if (string.IsNullOrWhiteSpace(body.Name)) throw ApiException.BadRequest("invalid", L.T("Bitte einen Namen angeben."));
            var tenantIds = await ValidateScopeAsync(user, body.AllTenants, body.TenantIds, db);
            var scopes = body.Scopes is null ? ApiScopes.Legacy(body.AllowTodoWrite) : ApiScopes.Normalize(body.Scopes);
            var prefix = TokenHashing.NewSecret(6).Replace("_", "x").Replace("-", "y");
            var secret = TokenHashing.NewSecret();
            var key = new ApiKey
            {
                AllTenants = body.AllTenants, TenantIds = tenantIds, Scopes = scopes,
                Name = body.Name.Trim(), Prefix = prefix, SecretHash = TokenHashing.Hash(secret), CreatedBy = user.Upn,
                ExpiresAt = body.ExpiresInDays is { } d and > 0 ? DateTimeOffset.UtcNow.AddDays(d) : null,
            };
            db.ApiKeys.Add(key);
            audit.Add(AuditService.User(user), AuditActions.ApiKeyCreated, tenantIds.Count == 1 ? tenantIds[0] : null, target: key.Name,
                details: new { key.Prefix, key.ExpiresAt, key.AllTenants, key.TenantIds, key.Scopes });
            await db.SaveChangesAsync();
            return Results.Ok(new { key.Id, key.Name, key.Prefix, key.ExpiresAt, apiKey = $"ylm_{prefix}_{secret}" });
        });

        g.MapPut("/{id:guid}/scope", async (Guid id, UpdateApiKeyRequest body, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound(L.T("API-Schlüssel"));
            if (key.RevokedAt is not null) throw ApiException.Conflict("revoked", L.T("Der Schlüssel ist widerrufen."));
            var before = new { key.AllTenants, key.TenantIds, Scopes = key.Scopes.ToList() };
            key.TenantIds = await ValidateScopeAsync(user, body.AllTenants, body.TenantIds, db);
            key.AllTenants = body.AllTenants;
            if (body.Scopes is not null) key.Scopes = ApiScopes.Normalize(body.Scopes);
            else if (body.AllowTodoWrite is { } todo)
                key.Scopes = ApiScopes.Normalize(todo ? [.. key.Scopes, ApiScopes.TodosWrite] : key.Scopes.Where(s => s != ApiScopes.TodosWrite));
            audit.Add(AuditService.User(user), AuditActions.ApiKeyScopeChanged, null, target: key.Name,
                details: new { key.Prefix, before, after = new { key.AllTenants, key.TenantIds, key.Scopes } });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db, AuditService audit) =>
        {
            var user = await cu.RequireAsync(Permission.ManageApiKeys);
            var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound(L.T("API-Schlüssel"));
            key.RevokedAt ??= DateTimeOffset.UtcNow;
            audit.Add(AuditService.User(user), AuditActions.ApiKeyRevoked, null, target: key.Name, details: new { key.Prefix });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}
