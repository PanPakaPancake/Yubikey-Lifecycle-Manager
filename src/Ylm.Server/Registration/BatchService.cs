using Microsoft.EntityFrameworkCore;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Ylm.Core;
using Ylm.Server.Agents;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Registration;

/// <summary>Where the people of a new batch come from: an Entra group, CSV text or picked users.</summary>
public sealed record BatchCandidatesRequest(string? GroupId, string? Csv, List<string>? UserIds);

public sealed record BatchCandidate(
    string EntraUserId,
    string UserPrincipalName,
    string? DisplayName,
    string? Department,
    bool AccountEnabled,
    int ActiveKeys,
    int? EntraYubiKeys,
    int? EntraOtherPasskeys,
    Guid? ProfileId,
    string? ProfileGroupName,
    int? ExpectedSerialNumber);

public sealed record BatchCandidateError(int Line, string Value, string Message);

public sealed record BatchCandidatesResult(List<BatchCandidate> Candidates, List<BatchCandidateError> Errors);

public sealed record BatchItemInput(
    string EntraUserId,
    string UserPrincipalName,
    string? UserDisplayName,
    string? Department,
    Guid? ProfileId,
    string? ProfileGroupName,
    int? ExpectedSerialNumber);

public sealed record CreateBatchRequest(string? Name, Guid DefaultProfileId, string? Source, List<BatchItemInput> Items);

public sealed record AddBatchItemsRequest(List<BatchItemInput> Items);

public sealed record UpdateBatchItemRequest(Guid? ProfileId, int? ExpectedSerialNumber, bool ClearProfile = false, bool ClearSerial = false);

public sealed record BatchNextRequest(Guid AgentId, Guid? ItemId);

public sealed record BatchRegisterRequest(Guid AgentId, List<ViolationCode>? Overrides, bool PerformReset, int? ExpectedSerialNumber);

/// <summary>
/// Stapelregistrierung (planning 6.3.2, V1.1): a persisted list of people. The admin plugs in one key after the other;
/// each key goes to the next open person, unless the list fixes a serial number for a person (CSV).
/// Every single registration runs through <see cref="RegistrationService"/> exactly like the Einzel-Wizard.
/// </summary>
public sealed class BatchService(
    YlmDbContext db,
    IGraphClientFactory graphFactory,
    AgentGateway agents,
    RegistrationService registration,
    AuditService audit,
    PinLetterStore letters,
    JobStore jobs)
{
    public const int MaxItems = 500;

    // ---------- Kandidaten ----------

    private static readonly string[] UpnHeaders = ["upn", "userprincipalname", "benutzer", "benutzername", "user", "mail", "email", "e-mail", "person"];
    private static readonly string[] SerialHeaders = ["seriennummer", "serial", "serialnumber", "seriennr", "sn", "key"];
    private static readonly string[] ProfileHeaders = ["profil", "profile", "registrierungsprofil"];

    public async Task<BatchCandidatesResult> CandidatesAsync(Guid tenantId, BatchCandidatesRequest req, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var errors = new List<BatchCandidateError>();
        // (user, serial, explicit profile)
        var rows = new List<(GraphUser User, int? Serial, Guid? ProfileId, string? ProfileLabel)>();

        if (!string.IsNullOrWhiteSpace(req.GroupId))
        {
            var members = await graph.ListGroupUsersAsync(req.GroupId.Trim(), ct) ?? throw ApiException.NotFound(L.T("Gruppe"));
            rows.AddRange(members.OrderBy(u => u.DisplayName ?? u.UserPrincipalName, StringComparer.CurrentCultureIgnoreCase).Select(u => (u, (int?)null, (Guid?)null, (string?)null)));
        }
        else if (!string.IsNullOrWhiteSpace(req.Csv))
        {
            rows.AddRange(await ParseCsvAsync(tenantId, graph, req.Csv, errors, ct));
        }
        else if (req.UserIds is { Count: > 0 })
        {
            foreach (var id in req.UserIds.Distinct().Take(MaxItems))
            {
                var u = await graph.GetUserAsync(id, ct);
                if (u is null) errors.Add(new BatchCandidateError(0, id, L.T("Person nicht gefunden.")));
                else rows.Add((u, null, null, null));
            }
        }
        else throw ApiException.BadRequest("invalid", L.T("Bitte eine Gruppe, eine CSV-Datei oder Personen angeben."));

        if (rows.Count > MaxItems)
            throw ApiException.BadRequest("too_many", L.F($"Ein Stapel kann höchstens {MaxItems} Personen enthalten (gefunden: {rows.Count}). Bitte in mehrere Stapel aufteilen."));

        var userIds = rows.Select(r => r.User.Id).ToList();
        var keyCounts = await db.Registrations.Where(r => r.TenantId == tenantId && r.RemovedAt == null && userIds.Contains(r.EntraUserId))
            .GroupBy(r => r.EntraUserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var assignments = await db.GroupProfiles.AsNoTracking().Where(a => a.TenantId == tenantId).OrderBy(a => a.Priority).ToListAsync(ct);

        using var gate = new SemaphoreSlim(5);
        var enriched = await Task.WhenAll(rows.Select(async r =>
        {
            await gate.WaitAsync(ct);
            try
            {
                int? yubi = null, other = null;
                try
                {
                    var methods = await graph.ListFido2MethodsAsync(r.User.Id, ct);
                    yubi = methods.Count(m => YubicoAaguids.IsYubiKey(m.AaGuid, m.Model));
                    other = methods.Count - yubi;
                }
                catch (Exception) { /* Entra status unknown */ }
                Guid? profileId = r.ProfileId;
                string? groupName = r.ProfileLabel;
                if (profileId is null && assignments.Count > 0)
                {
                    try
                    {
                        var member = await graph.CheckMemberGroupsAsync(r.User.Id, assignments.Select(a => a.GroupId).ToList(), ct);
                        var match = assignments.FirstOrDefault(a => member.Contains(a.GroupId));
                        if (match is not null) { profileId = match.ProfileId; groupName = match.GroupName; }
                    }
                    catch (Exception) { /* no suggestion */ }
                }
                return new BatchCandidate(r.User.Id, r.User.UserPrincipalName, r.User.DisplayName, r.User.Department, r.User.AccountEnabled,
                    keyCounts.GetValueOrDefault(r.User.Id), yubi, other, profileId, groupName, r.Serial);
            }
            finally { gate.Release(); }
        }));
        return new BatchCandidatesResult([.. enriched], errors);
    }

    private async Task<List<(GraphUser, int?, Guid?, string?)>> ParseCsvAsync(Guid tenantId, IGraphClient graph, string csv, List<BatchCandidateError> errors, CancellationToken ct)
    {
        var lines = Csv.Read(csv);
        if (lines.Count == 0) return [];
        static int Find(string[] header, string[] names) =>
            Array.FindIndex(header, h => names.Contains(h.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "")));
        var first = lines[0];
        int upnCol = Find(first, UpnHeaders), serialCol = Find(first, SerialHeaders), profileCol = Find(first, ProfileHeaders);
        var hasHeader = upnCol >= 0 || serialCol >= 0 || profileCol >= 0;
        if (!hasHeader) { upnCol = 0; serialCol = first.Length > 1 ? 1 : -1; profileCol = first.Length > 2 ? 2 : -1; }
        else if (upnCol < 0) throw ApiException.BadRequest("csv_invalid", L.T("Die CSV-Datei braucht eine Spalte „UPN“ (oder „Mail“)."));
        var data = lines.Skip(hasHeader ? 1 : 0).Select((l, i) => (Line: i + (hasHeader ? 2 : 1), Cells: l)).ToList();
        if (data.Count > MaxItems)
            throw ApiException.BadRequest("too_many", L.F($"Ein Stapel kann höchstens {MaxItems} Personen enthalten (CSV: {data.Count} Zeilen)."));

        var profiles = await db.Profiles.AsNoTracking().Where(p => p.TenantId == tenantId || p.TenantId == null).ToListAsync(ct);
        var parsed = new List<(int Line, string Upn, int? Serial, Guid? ProfileId, string? ProfileName)>();
        foreach (var (line, cells) in data)
        {
            string Cell(int i) => i >= 0 && i < cells.Length ? cells[i].Trim() : "";
            var upn = Cell(upnCol);
            if (upn.Length == 0) continue;
            int? serial = null;
            var serialText = Cell(serialCol);
            if (serialText.Length > 0)
            {
                if (int.TryParse(serialText, out var s) && s > 0) serial = s;
                else { errors.Add(new BatchCandidateError(line, upn, L.F($"Seriennummer „{serialText}“ ist keine Zahl."))); continue; }
            }
            Guid? profileId = null;
            string? profileName = null;
            var profileText = Cell(profileCol);
            if (profileText.Length > 0)
            {
                // Tenant profiles win over global ones with the same name.
                var p = profiles.Where(x => x.Name.Equals(profileText, StringComparison.CurrentCultureIgnoreCase)).OrderBy(x => x.TenantId == null).FirstOrDefault();
                if (p is null) { errors.Add(new BatchCandidateError(line, upn, L.F($"Profil „{profileText}“ gibt es in diesem Tenant nicht."))); continue; }
                profileId = p.Id;
                profileName = "CSV";
            }
            parsed.Add((line, upn, serial, profileId, profileName));
        }

        using var gate = new SemaphoreSlim(5);
        var resolved = await Task.WhenAll(parsed.Select(async p =>
        {
            await gate.WaitAsync(ct);
            try { return (p, User: await graph.GetUserAsync(p.Upn, ct)); }
            catch (GraphException) { return (p, User: (GraphUser?)null); }
            finally { gate.Release(); }
        }));

        var result = new List<(GraphUser, int?, Guid?, string?)>();
        var seenUsers = new HashSet<string>();
        var seenSerials = new HashSet<int>();
        foreach (var (p, user) in resolved)
        {
            if (user is null) { errors.Add(new BatchCandidateError(p.Line, p.Upn, L.T("Person in Entra ID nicht gefunden."))); continue; }
            if (!seenUsers.Add(user.Id)) { errors.Add(new BatchCandidateError(p.Line, p.Upn, L.T("Person steht mehrfach in der Liste; nur der erste Eintrag zählt."))); continue; }
            if (p.Serial is { } s && !seenSerials.Add(s)) { errors.Add(new BatchCandidateError(p.Line, p.Upn, L.F($"Seriennummer {s} ist mehreren Personen zugeordnet."))); continue; }
            result.Add((user, p.Serial, p.ProfileId, p.ProfileName));
        }
        return result;
    }

    // ---------- Anlegen und Pflege ----------

    private async Task ValidateItemsAsync(Guid tenantId, IReadOnlyCollection<BatchItemInput> items, IEnumerable<RegistrationBatchItem> existing, CancellationToken ct)
    {
        var profileIds = items.Where(i => i.ProfileId is not null).Select(i => i.ProfileId!.Value).Distinct().ToList();
        var valid = await db.Profiles.CountAsync(p => profileIds.Contains(p.Id) && (p.TenantId == null || p.TenantId == tenantId), ct);
        if (valid != profileIds.Count) throw ApiException.BadRequest("invalid", L.T("Mindestens ein Profil gilt nicht für diesen Tenant."));
        var users = existing.Select(e => e.EntraUserId).ToHashSet();
        var serials = existing.Where(e => e.ExpectedSerialNumber is not null && e.Status != BatchItemStatus.Skipped).Select(e => e.ExpectedSerialNumber!.Value).ToHashSet();
        foreach (var i in items)
        {
            if (string.IsNullOrWhiteSpace(i.EntraUserId) || string.IsNullOrWhiteSpace(i.UserPrincipalName))
                throw ApiException.BadRequest("invalid", L.T("Jede Zeile braucht eine Person."));
            if (!users.Add(i.EntraUserId))
                throw ApiException.BadRequest("duplicate_user", L.F($"{i.UserPrincipalName} steht bereits im Stapel."));
            if (i.ExpectedSerialNumber is { } s && !serials.Add(s))
                throw ApiException.BadRequest("duplicate_serial", L.F($"Seriennummer {s} ist bereits einer anderen Person im Stapel zugeordnet."));
        }
    }

    private static RegistrationBatchItem ToItem(Guid batchId, int position, BatchItemInput i) => new()
    {
        BatchId = batchId,
        Position = position,
        EntraUserId = i.EntraUserId.Trim(),
        UserPrincipalName = i.UserPrincipalName.Trim(),
        UserDisplayName = i.UserDisplayName?.Trim(),
        Department = i.Department?.Trim(),
        ProfileId = i.ProfileId,
        ProfileGroupName = i.ProfileId is null ? null : i.ProfileGroupName,
        ExpectedSerialNumber = i.ExpectedSerialNumber,
    };

    public async Task<RegistrationBatch> CreateAsync(Guid tenantId, PlatformUser actor, CreateBatchRequest req, CancellationToken ct = default)
    {
        if (req.Items is not { Count: > 0 }) throw ApiException.BadRequest("invalid", L.T("Der Stapel enthält keine Personen."));
        if (req.Items.Count > MaxItems) throw ApiException.BadRequest("too_many", L.F($"Ein Stapel kann höchstens {MaxItems} Personen enthalten."));
        await RegistrationService.LoadProfileAsync(db, tenantId, req.DefaultProfileId, ct);
        await ValidateItemsAsync(tenantId, req.Items, [], ct);
        var batch = new RegistrationBatch
        {
            TenantId = tenantId,
            Name = string.IsNullOrWhiteSpace(req.Name) ? L.F($"Stapel vom {DateTime.Now:dd.MM.yyyy HH:mm}") : req.Name.Trim(),
            Source = req.Source?.Trim(),
            DefaultProfileId = req.DefaultProfileId,
            CreatedById = actor.Id,
            CreatedByName = actor.DisplayName,
        };
        batch.Items = req.Items.Select((i, n) => ToItem(batch.Id, n, i)).ToList();
        db.Batches.Add(batch);
        audit.Add(AuditService.User(actor), AuditActions.BatchCreated, tenantId, target: batch.Name, details: new { batch = batch.Id, people = batch.Items.Count, source = batch.Source });
        await db.SaveChangesAsync(ct);
        return batch;
    }

    public async Task<RegistrationBatch> LoadAsync(Guid batchId, CancellationToken ct = default)
    {
        var batch = await db.Batches.Include(b => b.Items).FirstOrDefaultAsync(b => b.Id == batchId, ct) ?? throw ApiException.NotFound(L.T("Stapel"));
        batch.Items = batch.Items.OrderBy(i => i.Position).ToList();
        // A registration interrupted by a server restart leaves its item in "Running" without a job.
        var stale = batch.Items.Where(i => i.Status == BatchItemStatus.Running && (i.JobId is not { } j || !jobs.Exists(j))).ToList();
        foreach (var i in stale)
        {
            i.Status = BatchItemStatus.Failed;
            i.ErrorCode = "interrupted";
            i.Error = L.T("Der Vorgang wurde unterbrochen (z. B. Neustart des Servers). Auf dem Key ist evtl. schon eine PIN gesetzt; für einen neuen Versuch mit Werksreset registrieren.");
        }
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
        return batch;
    }

    private static void EnsureOpen(RegistrationBatch batch)
    {
        if (batch.Status == BatchStatus.Cancelled) throw ApiException.Conflict("batch_cancelled", L.T("Der Stapel wurde abgebrochen."));
    }

    private static RegistrationBatchItem Item(RegistrationBatch batch, Guid itemId) =>
        batch.Items.FirstOrDefault(i => i.Id == itemId) ?? throw ApiException.NotFound(L.T("Person im Stapel"));

    public async Task AddItemsAsync(RegistrationBatch batch, PlatformUser actor, AddBatchItemsRequest req, CancellationToken ct = default)
    {
        EnsureOpen(batch);
        if (req.Items is not { Count: > 0 }) return;
        if (batch.Items.Count + req.Items.Count > MaxItems) throw ApiException.BadRequest("too_many", L.F($"Ein Stapel kann höchstens {MaxItems} Personen enthalten."));
        await ValidateItemsAsync(batch.TenantId, req.Items, batch.Items, ct);
        var pos = batch.Items.Count == 0 ? 0 : batch.Items.Max(i => i.Position) + 1;
        foreach (var i in req.Items) db.BatchItems.Add(ToItem(batch.Id, pos++, i));
        batch.Status = BatchStatus.Open;
        batch.CompletedAt = null;
        audit.Add(AuditService.User(actor), AuditActions.BatchChanged, batch.TenantId, target: batch.Name, details: new { batch = batch.Id, added = req.Items.Select(i => i.UserPrincipalName) });
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateItemAsync(RegistrationBatch batch, Guid itemId, UpdateBatchItemRequest req, CancellationToken ct = default)
    {
        EnsureOpen(batch);
        var item = Item(batch, itemId);
        if (item.Status is not (BatchItemStatus.Pending or BatchItemStatus.Failed or BatchItemStatus.Skipped))
            throw ApiException.Conflict("item_locked", L.T("Diese Person ist bereits registriert oder wird gerade registriert."));
        if (req.ClearProfile) { item.ProfileId = null; item.ProfileGroupName = null; }
        else if (req.ProfileId is { } p)
        {
            await RegistrationService.LoadProfileAsync(db, batch.TenantId, p, ct);
            item.ProfileId = p;
            item.ProfileGroupName = null;
        }
        if (req.ClearSerial) item.ExpectedSerialNumber = null;
        else if (req.ExpectedSerialNumber is { } s)
        {
            if (batch.Items.Any(i => i.Id != item.Id && i.ExpectedSerialNumber == s && i.Status != BatchItemStatus.Skipped))
                throw ApiException.BadRequest("duplicate_serial", L.F($"Seriennummer {s} ist bereits einer anderen Person im Stapel zugeordnet."));
            item.ExpectedSerialNumber = s;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task SetSkippedAsync(RegistrationBatch batch, Guid itemId, bool skipped, PlatformUser actor, CancellationToken ct = default)
    {
        EnsureOpen(batch);
        var item = Item(batch, itemId);
        if (skipped)
        {
            if (item.Status is not (BatchItemStatus.Pending or BatchItemStatus.Failed))
                throw ApiException.Conflict("item_locked", L.T("Nur offene oder fehlgeschlagene Personen können übersprungen werden."));
            item.Status = BatchItemStatus.Skipped;
        }
        else
        {
            if (item.Status is not (BatchItemStatus.Skipped or BatchItemStatus.Failed))
                throw ApiException.Conflict("item_locked", L.T("Nur übersprungene oder fehlgeschlagene Personen können wieder eingereiht werden."));
            item.Status = BatchItemStatus.Pending;
            item.Error = null;
            item.ErrorCode = null;
            batch.Status = BatchStatus.Open;
            batch.CompletedAt = null;
        }
        await db.SaveChangesAsync(ct);
        if (skipped) await CompleteIfFinishedAsync(db, audit, batch.Id, actor, ct);
    }

    public async Task RemoveItemAsync(RegistrationBatch batch, Guid itemId, CancellationToken ct = default)
    {
        EnsureOpen(batch);
        var item = Item(batch, itemId);
        if (item.Status is not (BatchItemStatus.Pending or BatchItemStatus.Skipped))
            throw ApiException.Conflict("item_locked", L.T("Nur offene oder übersprungene Personen können entfernt werden."));
        db.BatchItems.Remove(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetStatusAsync(RegistrationBatch batch, BatchStatus status, PlatformUser actor, CancellationToken ct = default)
    {
        if (batch.Items.Any(i => i.Status == BatchItemStatus.Running))
            throw ApiException.Conflict("batch_running", L.T("Gerade läuft eine Registrierung. Bitte warten, bis sie fertig ist."));
        batch.Status = status;
        batch.CompletedAt = status == BatchStatus.Open ? null : DateTimeOffset.UtcNow;
        var counts = Counts(batch.Items);
        audit.Add(AuditService.User(actor), status switch
        {
            BatchStatus.Cancelled => AuditActions.BatchCancelled,
            BatchStatus.Completed => AuditActions.BatchCompleted,
            _ => AuditActions.BatchChanged,
        }, batch.TenantId, target: batch.Name, details: new { batch = batch.Id, status = status.ToString(), counts });
        await db.SaveChangesAsync(ct);
    }

    // ---------- Ablauf am Agent ----------

    /// <summary>
    /// Finds the person for the key that is plugged in right now: the person the key is fixed to (CSV serial),
    /// otherwise the next open person without a fixed key. Returns the preview of the Einzel-Wizard for that pair.
    /// </summary>
    public async Task<object> NextAsync(RegistrationBatch batch, PlatformUser actor, BatchNextRequest req, CancellationToken ct = default)
    {
        EnsureOpen(batch);
        var agent = agents.Require(req.AgentId, actor.Id);
        var key = await agents.ReadKeyAsync(agent, ct) ?? throw ApiException.Conflict("no_key", L.T("Am Agent ist kein YubiKey eingesteckt."));
        var open = batch.Items.Where(i => i.Status is BatchItemStatus.Pending or BatchItemStatus.Failed).ToList();
        var fixedTo = key.SerialNumber is { } serial ? open.FirstOrDefault(i => i.ExpectedSerialNumber == serial) : null;

        RegistrationBatchItem? item;
        if (req.ItemId is { } wanted)
        {
            item = Item(batch, wanted);
            if (item.Status is not (BatchItemStatus.Pending or BatchItemStatus.Failed))
                throw ApiException.Conflict("item_locked", L.T("Diese Person ist bereits registriert, übersprungen oder wird gerade registriert."));
            if (fixedTo is not null && fixedTo.Id != item.Id)
                throw ApiException.Conflict("key_reserved", L.F($"Dieser Key ist laut Liste für {fixedTo.UserDisplayName ?? fixedTo.UserPrincipalName} vorgesehen."));
        }
        else
        {
            if (key.SerialNumber is { } s && batch.Items.FirstOrDefault(i => i.Status == BatchItemStatus.Done && i.SerialNumber == s) is { } done)
                return new { state = "already_done", item = ItemDto(done, null), key };
            item = fixedTo ?? open.FirstOrDefault(i => i.Status == BatchItemStatus.Pending && i.ExpectedSerialNumber is null);
            if (item is null)
            {
                if (open.Count == 0) return new { state = "finished", key };
                return new
                {
                    state = "no_match",
                    key,
                    message = L.F($"Für Key {key.SerialNumber} ist niemand im Stapel vorgesehen. Offen sind nur noch Personen mit fester Seriennummer{(open.Any(i => i.Status == BatchItemStatus.Failed) ? L.T(" oder fehlgeschlagene Registrierungen (dort „Jetzt registrieren“ wählen).") : ".")}"),
                };
            }
        }
        if (item.ExpectedSerialNumber is { } expected && key.SerialNumber != expected)
            throw ApiException.Conflict("wrong_key", L.F($"Für {item.UserDisplayName ?? item.UserPrincipalName} ist Key {expected} vorgesehen, eingesteckt ist {key.SerialNumber}."));

        var profileId = item.ProfileId ?? batch.DefaultProfileId;
        var preview = await registration.PreviewAsync(batch.TenantId, actor, new RegistrationPreviewRequest(item.EntraUserId, profileId, req.AgentId), ct);
        return new { state = "ready", item = ItemDto(item, null), profileId, preview, key };
    }

    public async Task<OperationJob> RegisterAsync(RegistrationBatch batch, Guid itemId, PlatformUser actor, BatchRegisterRequest req, IServiceScopeFactory scopes, CancellationToken ct = default)
    {
        EnsureOpen(batch);
        var item = Item(batch, itemId);
        if (item.ExpectedSerialNumber is { } expected && req.ExpectedSerialNumber != expected)
            throw ApiException.Conflict("wrong_key", L.F($"Für diese Person ist Key {expected} vorgesehen."));
        var jobId = Guid.NewGuid();
        // Atomic claim: two admins working on the same batch never register the same person twice.
        var claimed = await db.BatchItems
            .Where(i => i.Id == item.Id && (i.Status == BatchItemStatus.Pending || i.Status == BatchItemStatus.Failed))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, BatchItemStatus.Running).SetProperty(i => i.JobId, jobId), ct);
        if (claimed == 0) throw ApiException.Conflict("item_locked", L.T("Diese Person ist bereits registriert, übersprungen oder wird gerade registriert."));
        if (batch.Status == BatchStatus.Completed)
        {
            batch.Status = BatchStatus.Open;
            batch.CompletedAt = null;
            await db.SaveChangesAsync(ct);
        }
        var profileId = item.ProfileId ?? batch.DefaultProfileId;
        return registration.Start(batch.TenantId, actor,
            new RegistrationRequest(item.EntraUserId, profileId, req.AgentId, req.Overrides, req.PerformReset, req.ExpectedSerialNumber),
            jobs, scopes, item.Id, jobId);
    }

    /// <summary>Called by the registration job when it ends, successful or not.</summary>
    internal static async Task FinishItemAsync(YlmDbContext db, AuditService audit, Guid itemId, OperationJob job, bool succeeded, PlatformUser? actor)
    {
        db.ChangeTracker.Clear();
        var item = await db.BatchItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return;
        item.CompletedAt = DateTimeOffset.UtcNow;
        item.CompletedByName = actor?.DisplayName;
        if (succeeded)
        {
            item.Status = BatchItemStatus.Done;
            item.KeyId = job.KeyId;
            item.SerialNumber = job.SerialNumber;
            item.PinChangeForced = job.PinChangeForced;
            item.PinLetterId = job.PinLetterId;
            item.Error = null;
            item.ErrorCode = null;
            item.Note = job.Notes.Count > 0 ? string.Join(" ", job.Notes) : null;
        }
        else
        {
            item.Status = BatchItemStatus.Failed;
            item.Error = job.Error;
            item.ErrorCode = job.ErrorCode;
        }
        await db.SaveChangesAsync();
        if (actor is not null) await CompleteIfFinishedAsync(db, audit, item.BatchId, actor);
    }

    /// <summary>A batch is done when nobody is open, running or failed any more.</summary>
    internal static async Task CompleteIfFinishedAsync(YlmDbContext db, AuditService audit, Guid batchId, PlatformUser actor, CancellationToken ct = default)
    {
        var batch = await db.Batches.Include(b => b.Items).FirstAsync(b => b.Id == batchId, ct);
        if (batch.Status != BatchStatus.Open) return;
        if (batch.Items.Any(i => i.Status is BatchItemStatus.Pending or BatchItemStatus.Running or BatchItemStatus.Failed)) return;
        batch.Status = BatchStatus.Completed;
        batch.CompletedAt = DateTimeOffset.UtcNow;
        audit.Add(AuditService.User(actor), AuditActions.BatchCompleted, batch.TenantId, target: batch.Name, details: new { batch = batch.Id, counts = Counts(batch.Items) });
        await db.SaveChangesAsync(ct);
    }

    // ---------- PIN-Briefe ----------

    /// <summary>All PIN letters of the batch that are still in memory for this admin, merged into one PDF for printing.</summary>
    public async Task<(byte[] Pdf, string FileName, int Count)> MergedLettersAsync(RegistrationBatch batch, PlatformUser actor, IReadOnlyCollection<Guid>? itemIds, CancellationToken ct = default)
    {
        var pdfs = batch.Items
            .Where(i => i.Status == BatchItemStatus.Done && i.PinLetterId is not null && (itemIds is null || itemIds.Count == 0 || itemIds.Contains(i.Id)))
            .Select(i => letters.Get(i.PinLetterId!.Value, actor.Id))
            .Where(l => l is not null)
            .Select(l => l!.Value.Pdf)
            .ToList();
        if (pdfs.Count == 0)
            throw new ApiException(410, "expired", L.T("Keine PIN-Briefe mehr verfügbar. PINs werden nicht gespeichert; die Briefe liegen nur 4 Stunden im Arbeitsspeicher und nur für den Admin, der registriert hat."));
        using var output = new PdfDocument();
        output.Info.Title = "PIN-Briefe " + batch.Name;
        output.Info.Creator = "Yubikey Lifecycle Manager";
        foreach (var bytes in pdfs)
        {
            using var input = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
            foreach (var page in input.Pages) output.AddPage(page);
        }
        using var ms = new MemoryStream();
        output.Save(ms, false);
        await audit.WriteAsync(AuditService.User(actor), AuditActions.PinLetterDownloaded, batch.TenantId, target: $"PIN-Briefe {batch.Name}", details: new { batch = batch.Id, letters = pdfs.Count });
        var safeName = string.Concat(batch.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return (ms.ToArray(), L.F($"PIN-Briefe {safeName}.pdf"), pdfs.Count);
    }

    // ---------- DTOs ----------

    public static object Counts(IEnumerable<RegistrationBatchItem> items)
    {
        var list = items.ToList();
        return new
        {
            Total = list.Count,
            Pending = list.Count(i => i.Status == BatchItemStatus.Pending),
            Running = list.Count(i => i.Status == BatchItemStatus.Running),
            Done = list.Count(i => i.Status == BatchItemStatus.Done),
            Failed = list.Count(i => i.Status == BatchItemStatus.Failed),
            Skipped = list.Count(i => i.Status == BatchItemStatus.Skipped),
        };
    }

    public object ItemDto(RegistrationBatchItem i, Guid? viewerId) => new
    {
        i.Id, i.Position, i.EntraUserId, i.UserPrincipalName, i.UserDisplayName, i.Department, i.ProfileId, i.ProfileGroupName, i.ExpectedSerialNumber,
        Status = i.Status.ToString(), i.JobId, i.KeyId, i.SerialNumber, i.PinChangeForced, i.Error, i.ErrorCode, i.Note, i.CompletedAt, i.CompletedByName,
        i.PinLetterId,
        PinLetterAvailable = viewerId is { } v && i.PinLetterId is { } l && letters.Get(l, v) is not null,
    };

    public object Dto(RegistrationBatch b, Guid viewerId) => new
    {
        b.Id, b.TenantId, b.Name, b.Source, b.DefaultProfileId, Status = b.Status.ToString(), b.CreatedAt, b.CreatedByName, b.CompletedAt,
        Counts = Counts(b.Items),
        Items = b.Items.Select(i => ItemDto(i, viewerId)).ToList(),
    };
}
