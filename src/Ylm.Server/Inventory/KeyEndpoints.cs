using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Agents;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Infrastructure;
using Ylm.Server.Registration;
using Ylm.Server.Security;

namespace Ylm.Server.Inventory;

public static class KeyEndpoints
{
    public sealed record UpdateKeyRequest(List<string>? Tags, Dictionary<string, string?>? CustomFields, string? Notes);
    public sealed record LockRequest(LockReason Reason, string? Note);
    /// <param name="ToPartnerStock">true: Key vom Tenant trennen und ins Partnerlager legen; false: bleibt im Tenant.</param>
    public sealed record RetireRequest(string? Note, bool ToPartnerStock = false);
    public sealed record AgentRequest(Guid AgentId, bool ToPartnerStock = false);
    public sealed record AssignRequest(Guid TenantId);
    public sealed record IntakeRequest(Guid AgentId, List<string>? Tags, Dictionary<string, string?>? CustomFields, string? Notes);

    public static object ToDto(YubiKey k, bool includeAll = false) => new
    {
        k.Id,
        k.TenantId,
        TenantName = k.TenantId is null ? null : k.Tenant?.DisplayName,
        InPartnerStock = k.TenantId is null,
        k.SerialNumber,
        k.FirmwareVersion,
        k.Model,
        k.FormFactor,
        k.AaGuid,
        k.IsFips,
        Status = k.Status.ToString(),
        StatusLabel = KeyLifecycle.DisplayName(k.Status),
        LockReason = k.LockReason?.ToString(),
        k.StatusNote,
        Source = k.Source.ToString(),
        NeedsDetails = k.SerialNumber is null,
        k.Tags,
        k.CustomFields,
        k.Notes,
        k.ProfileId,
        k.PinChangeForced,
        k.CreatedAt,
        k.UpdatedAt,
        k.StatusChangedAt,
        Advisories = FirmwareAdvisories.For(k.FirmwareVersion).Select(a => new { a.Id, a.Title, a.Severity, a.Url, a.AffectsFido }),
        NextStates = KeyLifecycle.NextStates(k.Status).Select(s => s.ToString()),
        Registrations = k.Registrations.Where(r => includeAll || r.RemovedAt == null).OrderByDescending(r => r.RegisteredAt).Select(r => new
        {
            r.Id, r.EntraUserId, r.UserPrincipalName, r.UserDisplayName, r.EntraMethodId, r.DisplayName, r.RegisteredAt,
            r.RegisteredByName, Source = r.Source.ToString(), r.RemovedAt, r.RemovedReason, r.LastSeenInSyncAt,
        }),
    };

    public static void MapKeys(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();

        // partner=true: nur das Partnerlager; ohne tenantId: alle zuständigen Tenants plus Partnerlager (mit Recht „Partnerlager verwalten“).
        g.MapGet("/keys", async (Guid? tenantId, bool? partner, string? status, string? q, string? tag, string? source, bool? needsDetails, int? page, int? pageSize, CurrentUser cu, YlmDbContext db) =>
        {
            var me = await cu.RequireAsync(Permission.ReadInventory);
            var ids = await cu.AccessibleTenantIdsAsync();
            var includePartner = CurrentUser.Has(me, Permission.ManagePartnerStock);
            if (partner == true)
            {
                if (!includePartner) throw ApiException.Forbidden("Für das Partnerlager fehlt das Recht „Partnerlager verwalten“.");
                ids = [];
            }
            else if (tenantId is { } tid)
            {
                if (!ids.Contains(tid)) throw ApiException.Forbidden("Sie sind für diesen Tenant nicht zuständig.");
                ids = [tid];
                includePartner = false;
            }
            var query = db.Keys.AsNoTracking().Include(k => k.Tenant).Include(k => k.Registrations)
                .Where(k => (k.TenantId != null && ids.Contains(k.TenantId.Value)) || (includePartner && k.TenantId == null));
            if (Enum.TryParse<KeyStatus>(status, true, out var st)) query = query.Where(k => k.Status == st);
            if (Enum.TryParse<KeySource>(source, true, out var src)) query = query.Where(k => k.Source == src);
            if (!string.IsNullOrWhiteSpace(tag)) query = query.Where(k => k.Tags.Contains(tag));
            if (needsDetails == true) query = query.Where(k => k.SerialNumber == null && k.Status != KeyStatus.Retired);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim().ToLower();
                var serial = int.TryParse(term, out var s) ? s : (int?)null;
                query = query.Where(k => k.SerialNumber == serial
                    || (k.Model != null && k.Model.ToLower().Contains(term))
                    || (k.Notes != null && k.Notes.ToLower().Contains(term))
                    || k.Registrations.Any(r => r.RemovedAt == null && (r.UserPrincipalName.ToLower().Contains(term) || (r.UserDisplayName != null && r.UserDisplayName.ToLower().Contains(term)))));
            }
            var size = Math.Clamp(pageSize ?? 50, 1, 500);
            var p = Math.Max(page ?? 1, 1);
            var total = await query.CountAsync();
            var items = await query.OrderByDescending(k => k.UpdatedAt).Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { total, page = p, pageSize = size, items = items.Select(k => ToDto(k)) });
        });

        g.MapGet("/keys/{id:guid}", async (Guid id, CurrentUser cu, YlmDbContext db) =>
        {
            var key = await db.Keys.AsNoTracking().Include(k => k.Tenant).Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("Key");
            await cu.RequireKeyAccessAsync(key.TenantId, Permission.ReadInventory);
            var history = await db.AuditEntries.AsNoTracking().Where(a => a.KeyId == id).OrderByDescending(a => a.Sequence).Take(200)
                .Select(a => new { a.Sequence, a.Timestamp, a.ActorName, a.Action, a.Target, a.Details }).ToListAsync();
            return Results.Ok(new { key = ToDto(key, includeAll: true), history });
        });

        g.MapPatch("/keys/{id:guid}", async (Guid id, UpdateKeyRequest body, CurrentUser cu, YlmDbContext db, KeyService keys, AuditService audit) =>
        {
            var key = await db.Keys.Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("Key");
            var user = await cu.RequireKeyAccessAsync(key.TenantId, key.TenantId is null ? Permission.ManagePartnerStock : Permission.ManageInventory);
            var before = new { key.Tags, key.CustomFields, key.Notes };
            if (body.Tags is not null) key.Tags = Tags.Normalize(body.Tags).ToList();
            if (body.CustomFields is not null) key.CustomFields = await keys.ValidateFieldsAsync(key.TenantId, body.CustomFields, key.CustomFields);
            if (body.Notes is not null) key.Notes = body.Notes.Length == 0 ? null : body.Notes;
            key.UpdatedAt = DateTimeOffset.UtcNow;
            audit.Add(AuditService.User(user), AuditActions.KeyUpdated, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { before, after = new { key.Tags, key.CustomFields, key.Notes } });
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(key));
        });

        g.MapPost("/keys/{id:guid}/lock", async (Guid id, LockRequest body, CurrentUser cu, YlmDbContext db, KeyService keys) =>
        {
            var key = await db.Keys.Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("Key");
            var user = await cu.RequireKeyAccessAsync(key.TenantId, Permission.LockKeys);
            await keys.LockAsync(key, body.Reason, body.Note, AuditService.User(user));
            return Results.Ok(ToDto(key, true));
        });

        g.MapPost("/keys/{id:guid}/retire", async (Guid id, RetireRequest body, CurrentUser cu, YlmDbContext db, KeyService keys) =>
        {
            var key = await db.Keys.Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("Key");
            var user = await cu.RequireKeyAccessAsync(key.TenantId, Permission.RetireKeys);
            var toPartner = body.ToPartnerStock && key.TenantId is not null;
            if (toPartner) await cu.RequireAsync(Permission.ManagePartnerStock);
            await keys.RetireAsync(key, body.Note, AuditService.User(user), toPartner);
            return Results.Ok(ToDto(key, true));
        });

        // Key im Lager (oder ausgemustert) vom Kunden-Tenant trennen und ins Partnerlager legen.
        g.MapPost("/keys/{id:guid}/to-partner-stock", async (Guid id, CurrentUser cu, YlmDbContext db, KeyService keys) =>
        {
            var key = await db.Keys.Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("Key");
            if (key.TenantId is null) throw ApiException.Conflict("already_partner_stock", "Der Key liegt bereits im Partnerlager.");
            await cu.RequireTenantAsync(key.TenantId.Value, Permission.ManageInventory);
            var user = await cu.RequireAsync(Permission.ManagePartnerStock);
            await keys.MoveToPartnerStockAsync(key, AuditService.User(user));
            return Results.Ok(ToDto(key, true));
        });

        // Key aus dem Partnerlager einem Kunden-Tenant zuweisen; dort liegt er dann bereit.
        g.MapPost("/keys/{id:guid}/assign", async (Guid id, AssignRequest body, CurrentUser cu, YlmDbContext db, KeyService keys) =>
        {
            var key = await db.Keys.Include(k => k.Registrations).FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("Key");
            await cu.RequireAsync(Permission.ManagePartnerStock);
            var user = await cu.RequireTenantAsync(body.TenantId, Permission.ManageInventory);
            await keys.AssignToTenantAsync(key, body.TenantId, AuditService.User(user));
            await db.Entry(key).Reference(k => k.Tenant).LoadAsync();
            return Results.Ok(ToDto(key, true));
        });

        g.MapPost("/keys/{id:guid}/reset", async (Guid id, AgentRequest body, CurrentUser cu, YlmDbContext db, RegistrationService reg, JobStore jobs, IServiceScopeFactory scopes) =>
        {
            var key = await db.Keys.FirstOrDefaultAsync(k => k.Id == id) ?? throw ApiException.NotFound("Key");
            var user = await cu.RequireKeyAccessAsync(key.TenantId, Permission.ResetKeys);
            KeyLifecycle.EnsureTransition(key.Status, KeyStatus.InStock);
            var toPartner = body.ToPartnerStock && key.TenantId is not null;
            if (toPartner) await cu.RequireAsync(Permission.ManagePartnerStock);
            var job = reg.StartReset(key, user, body.AgentId, toPartner, jobs, scopes);
            return Results.Accepted($"/api/jobs/{job.Id}", job.ToDto());
        });

        // Wareneingang per Einstecken (planning 5.3).
        g.MapPost("/tenants/{tenantId:guid}/keys/intake", (Guid tenantId, IntakeRequest body, CurrentUser cu, YlmDbContext db, AgentGateway agents, KeyService keys, AuditService audit) =>
            IntakeAsync(tenantId, body, cu, db, agents, keys, audit));
        g.MapPost("/partner/keys/intake", (IntakeRequest body, CurrentUser cu, YlmDbContext db, AgentGateway agents, KeyService keys, AuditService audit) =>
            IntakeAsync(null, body, cu, db, agents, keys, audit));

        // Wareneingang per CSV-Import: Seriennummer;Modell;Firmware;Tags;Notiz;<Feldschlüssel>...
        g.MapPost("/tenants/{tenantId:guid}/keys/import", (Guid tenantId, HttpRequest req, CurrentUser cu, YlmDbContext db, KeyService keys, AuditService audit) =>
            ImportAsync(tenantId, req, cu, db, keys, audit));
        g.MapPost("/partner/keys/import", (HttpRequest req, CurrentUser cu, YlmDbContext db, KeyService keys, AuditService audit) =>
            ImportAsync(null, req, cu, db, keys, audit));

        g.MapPost("/tenants/{tenantId:guid}/sync", async (Guid tenantId, CurrentUser cu, SyncService sync) =>
        {
            var user = await cu.RequireTenantAsync(tenantId, Permission.SyncTenants);
            var result = await sync.SyncAsync(tenantId, AuditService.User(user));
            return Results.Ok(result);
        });

        g.MapGet("/tags", async (Guid? tenantId, CurrentUser cu, YlmDbContext db) =>
        {
            await cu.RequireAsync(Permission.ReadInventory);
            var ids = await cu.AccessibleTenantIdsAsync();
            if (tenantId is { } t) ids = ids.Where(x => x == t).ToList();
            var tags = await db.Keys.Where(k => k.TenantId != null && ids.Contains(k.TenantId.Value)).SelectMany(k => k.Tags).Distinct().OrderBy(t => t).ToListAsync();
            return Results.Ok(tags);
        });
    }
    /// <summary>Wareneingang per Einstecken in ein Tenant-Lager oder (tenantId null) ins Partnerlager.</summary>
    private static async Task<IResult> IntakeAsync(Guid? tenantId, IntakeRequest body, CurrentUser cu, YlmDbContext db, AgentGateway agents, KeyService keys, AuditService audit)
    {
        var user = await cu.RequireKeyAccessAsync(tenantId, Permission.ManageInventory);
        var agent = agents.Require(body.AgentId, user.Id);
        var info = await agents.ReadKeyAsync(agent) ?? throw ApiException.Conflict("no_key", "Am Agent ist kein YubiKey eingesteckt.");
        if (info.SerialNumber is not { } serial) throw ApiException.Conflict("no_serial", "Die Seriennummer des Keys ist nicht lesbar.");
        if (await keys.FindBySerialAsync(serial) is { } existing)
            throw ApiException.Conflict("exists", $"Key {serial} ist bereits erfasst ({(existing.TenantId is null ? "Partnerlager" : $"Tenant „{existing.Tenant?.DisplayName}“")}, Status {KeyLifecycle.DisplayName(existing.Status)}).");
        var key = new YubiKey
        {
            TenantId = tenantId,
            SerialNumber = serial,
            FirmwareVersion = info.FirmwareVersion,
            Model = info.Model,
            FormFactor = info.FormFactor,
            AaGuid = info.AaGuid,
            IsFips = info.IsFips,
            Source = KeySource.StockIntake,
            Status = KeyStatus.InStock,
            Tags = Tags.Normalize(body.Tags).ToList(),
            CustomFields = await keys.ValidateFieldsAsync(tenantId, body.CustomFields),
            Notes = body.Notes,
        };
        db.Keys.Add(key);
        audit.Add(AuditService.User(user), AuditActions.KeyStockIntake, tenantId, key.Id, serial.ToString(), new { info.Model, info.FirmwareVersion, agent = agent.MachineName });
        await db.SaveChangesAsync();
        return Results.Ok(ToDto(key));
    }

    private static async Task<IResult> ImportAsync(Guid? tenantId, HttpRequest req, CurrentUser cu, YlmDbContext db, KeyService keys, AuditService audit)
    {
        var user = await cu.RequireKeyAccessAsync(tenantId, Permission.ManageInventory);
        using var reader = new StreamReader(req.Body);
        var text = await reader.ReadToEndAsync();
        if (text.Length > 5_000_000) throw ApiException.BadRequest("too_large", "Die Datei ist zu groß.");
        var rows = Csv.Read(text);
        if (rows.Count < 2) throw ApiException.BadRequest("empty", "Die Datei enthält keine Datenzeilen.");
        var header = rows[0].Select(h => h.Trim()).ToArray();
        int Col(params string[] names) => Array.FindIndex(header, h => names.Any(n => h.Equals(n, StringComparison.OrdinalIgnoreCase)));
        var cSerial = Col("Seriennummer", "Serial", "SerialNumber");
        if (cSerial < 0) throw ApiException.BadRequest("no_serial_column", "Die Spalte „Seriennummer“ fehlt.");
        var cModel = Col("Modell", "Model");
        var cFw = Col("Firmware", "FirmwareVersion");
        var cTags = Col("Tags");
        var cNotes = Col("Notiz", "Notes", "Notizen");
        var specs = await keys.FieldSpecsAsync(tenantId);
        var fieldCols = specs.Select(s => (s.Key, Index: Col(s.Key, s.Label))).Where(x => x.Index >= 0).ToList();
        var known = await db.Keys.Where(k => k.SerialNumber != null && k.Status != KeyStatus.Retired).Select(k => k.SerialNumber!.Value).ToListAsync();
        var knownSet = known.ToHashSet();
        int created = 0, skipped = 0;
        var errors = new List<string>();
        for (var i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            string? Get(int c) => c >= 0 && c < r.Length && r[c].Trim().Length > 0 ? r[c].Trim() : null;
            if (!int.TryParse(Get(cSerial), out var serial) || serial <= 0) { errors.Add($"Zeile {i + 1}: ungültige Seriennummer."); continue; }
            if (!knownSet.Add(serial)) { skipped++; continue; }
            var fw = Get(cFw);
            if (fw is not null && !FirmwareVersion.TryParse(fw, out _)) { errors.Add($"Zeile {i + 1}: ungültige Firmware „{fw}“."); continue; }
            var fields = fieldCols.ToDictionary(f => f.Key, f => Get(f.Index));
            var fieldErrors = CustomFieldValidator.Validate(specs, fields);
            if (fieldErrors.Count > 0) { errors.Add($"Zeile {i + 1}: {string.Join(" ", fieldErrors)}"); continue; }
            var key = new YubiKey
            {
                TenantId = tenantId,
                SerialNumber = serial,
                Model = Get(cModel),
                FirmwareVersion = fw,
                Source = KeySource.Import,
                Status = KeyStatus.InStock,
                Tags = Tags.Normalize(Get(cTags)?.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries)).ToList(),
                Notes = Get(cNotes),
                CustomFields = fields.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value),
            };
            db.Keys.Add(key);
            created++;
        }
        if (created > 0)
        {
            audit.Add(AuditService.User(user), AuditActions.KeyImported, tenantId, details: new { created, skipped, errors = errors.Count });
            await db.SaveChangesAsync();
        }
        return Results.Ok(new { created, skipped, errors });
    }
}
