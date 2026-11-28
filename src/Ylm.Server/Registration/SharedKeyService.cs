using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Ylm.Contracts;
using Ylm.Core;
using Ylm.Server.Agents;
using Ylm.Server.Audit;
using Ylm.Server.Data;
using Ylm.Server.Graph;
using Ylm.Server.Infrastructure;
using Ylm.Server.Inventory;
using Ylm.Server.Pdf;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Registration;

public sealed record SharedPreviewRequest(List<string> EntraUserIds, Guid ProfileId, Guid AgentId, KeyUsage Usage = KeyUsage.Shared);

public sealed record SharedRegistrationRequest(
    List<string> EntraUserIds,
    Guid ProfileId,
    Guid AgentId,
    KeyUsage Usage,
    string? Custodian,
    List<ViolationCode>? Overrides,
    bool PerformReset,
    int? ExpectedSerialNumber,
    /// <summary>Aktuelle PIN, wenn Konten zu einem geteilten Key hinzukommen, auf dem schon eine PIN gesetzt ist.</summary>
    string? CurrentPin,
    /// <summary>Freigabe (Vier-Augen) für Breakglass-Keys, wenn der Tenant sie verlangt.</summary>
    Guid? ApprovalId = null);

public sealed record SharedPreviewUser(string Id, string UserPrincipalName, string? DisplayName, string? Department, bool AccountEnabled, bool AlreadyOnKey);

public sealed record SharedPreview(
    KeyInfo Key,
    IReadOnlyList<ProfileViolation> Violations,
    bool ResetRequired,
    bool ResetPlanned,
    bool WillForcePinChange,
    IReadOnlyList<SharedPreviewUser> Users,
    object? InventoryKey,
    int? RemainingSlots,
    /// <summary>True, wenn ohne Reset Konten zu einem Key mit gesetzter PIN hinzukommen: dann wird die aktuelle PIN gebraucht.</summary>
    bool NeedsCurrentPin,
    bool ApprovalRequired,
    IReadOnlyList<object> Approvals,
    IReadOnlyList<string> Problems);

/// <summary>
/// Geteilter Key (planning 6.3.3): mehrere Konten auf einem Key, Pflichtfeld Verwahrer, Anzeige der freien Passkey-Plätze.
/// Ein Lauf setzt höchstens einmal die PIN und registriert danach jedes Konto einzeln in Entra.
/// </summary>
public sealed class SharedKeyService(
    YlmDbContext db,
    IGraphClientFactory graphFactory,
    AgentGateway agents,
    KeyService keys,
    RegistrationService registration,
    AuditService audit,
    IDataProtectionProvider dataProtection,
    PinLetterStore letters,
    Microsoft.Extensions.Options.IOptions<YlmOptions> ylmOptions,
    ILogger<SharedKeyService> logger)
{
    public const int MaxAccounts = 25;

    private static List<string> Normalize(List<string>? ids) =>
        (ids ?? []).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void ValidateUsage(KeyUsage usage)
    {
        if (usage == KeyUsage.Personal) throw ApiException.BadRequest("invalid_usage", L.T("Für persönliche Keys bitte den normalen Wizard verwenden."));
    }

    /// <summary>
    /// Kommen Konten zu einem bekannten geteilten Key dazu, ist die gesetzte PIN gewollt: sie wird weiterverwendet statt zurückgesetzt.
    /// </summary>
    private static EvaluationResult WithoutPinOnSharedKey(EvaluationResult eval, YubiKey? existing, List<KeyRegistration> active, Guid tenantId) =>
        existing is { Usage: not KeyUsage.Personal } && existing.TenantId == tenantId && active.Count > 0
            ? new EvaluationResult(eval.Violations.Where(v => v.Code != ViolationCode.PinAlreadySet).ToList())
            : eval;

    public async Task<SharedPreview> PreviewAsync(Guid tenantId, PlatformUser actor, SharedPreviewRequest req, CancellationToken ct = default)
    {
        ValidateUsage(req.Usage);
        var ids = Normalize(req.EntraUserIds);
        if (ids.Count == 0) throw ApiException.BadRequest("no_users", L.T("Bitte mindestens ein Konto auswählen."));
        if (ids.Count > MaxAccounts) throw ApiException.BadRequest("too_many", L.F($"Höchstens {MaxAccounts} Konten pro Vorgang."));
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        var profile = await RegistrationService.LoadProfileAsync(db, tenantId, req.ProfileId, ct);
        var agent = agents.Require(req.AgentId, actor.Id);
        var key = await agents.ReadKeyAsync(agent, ct) ?? throw ApiException.Conflict("no_key", L.T("Am Agent ist kein YubiKey eingesteckt."));
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var existing = key.SerialNumber is { } serial ? await keys.FindBySerialAsync(serial, ct) : null;
        var active = existing?.Registrations.Where(r => r.RemovedAt == null).ToList() ?? [];
        var eval = WithoutPinOnSharedKey(registration.Evaluate(tenant, profile, key), existing, active, tenantId);
        var resetRequired = eval.Violations.Any(v => v.ResolvedByReset);
        var problems = new List<string>();
        var users = new List<SharedPreviewUser>();
        foreach (var id in ids)
        {
            var u = await graph.GetUserAsync(id, ct);
            if (u is null) { problems.Add(L.F($"Konto {id} wurde in Entra nicht gefunden.")); continue; }
            var onKey = active.Any(r => r.EntraUserId == u.Id);
            users.Add(new SharedPreviewUser(u.Id, u.UserPrincipalName, u.DisplayName, u.Department, u.AccountEnabled, onKey));
            if (onKey) problems.Add(L.F($"{u.UserPrincipalName} ist auf diesem Key schon registriert."));
        }
        if (existing is not null && existing.TenantId is { } other && other != tenantId)
            problems.Add(L.F($"Dieser Key ist im Inventar des Tenants „{existing.Tenant?.DisplayName}“ erfasst."));
        if (existing is not null && active.Count > 0 && existing.Usage == KeyUsage.Personal)
            problems.Add(L.F($"Der Key ist als persönlicher Key für {string.Join(", ", active.Select(r => r.UserPrincipalName))} registriert. Für die Nutzung als geteilter Key bitte mit Werksreset registrieren."));
        var resetPlanned = resetRequired || profile.FactoryResetBeforeRegistration;
        var newCount = users.Count(u => !u.AlreadyOnKey);
        if (key.RemainingDiscoverableCredentials is { } free && !resetPlanned && newCount > free)
            problems.Add(L.F($"Auf dem Key sind nur noch {free} Passkey-Plätze frei, es sollen {newCount} Konten registriert werden."));
        var approvalRequired = req.Usage == KeyUsage.Breakglass && tenant.RequireBreakglassApproval;
        var approvals = approvalRequired
            ? (await ApprovalService.UsableAsync(db, tenantId, actor.Id, ct)).Select(ApprovalService.ToDto).ToList()
            : [];
        return new SharedPreview(
            key,
            eval.Violations,
            resetRequired,
            resetPlanned,
            profile.ForcePinChange && key.SupportsForcePinChange,
            users,
            existing is null ? null : new
            {
                existing.Id, existing.TenantId, TenantName = existing.Tenant?.DisplayName, Status = existing.Status.ToString(),
                Usage = existing.Usage.ToString(), existing.Custodian,
                ActiveRegistrations = active.Select(r => r.UserPrincipalName).ToList(),
            },
            key.RemainingDiscoverableCredentials,
            key.PinIsSet && !resetPlanned,
            approvalRequired,
            approvals,
            problems);
    }

    public OperationJob Start(Guid tenantId, PlatformUser actor, SharedRegistrationRequest req, JobStore jobs, IServiceScopeFactory scopes)
    {
        ValidateUsage(req.Usage);
        var ids = Normalize(req.EntraUserIds);
        if (ids.Count == 0) throw ApiException.BadRequest("no_users", L.T("Bitte mindestens ein Konto auswählen."));
        if (ids.Count > MaxAccounts) throw ApiException.BadRequest("too_many", L.F($"Höchstens {MaxAccounts} Konten pro Vorgang."));
        if (string.IsNullOrWhiteSpace(req.Custodian)) throw ApiException.BadRequest("custodian_required", L.T("Bei geteilten Keys ist der Verwahrer Pflicht."));
        var job = jobs.Add(new OperationJob { UserId = actor.Id, TenantId = tenantId, Kind = "shared_registration", TotalSteps = 4 + ids.Count });
        var clean = req with { EntraUserIds = ids, Custodian = req.Custodian.Trim() };
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<SharedKeyService>();
            await svc.RunAsync(job, tenantId, actor.Id, clean);
        });
        return job;
    }

    private void Step(OperationJob job, ConnectedAgent agent, int n, string text, string? prompt = null)
    {
        job.StepNumber = n;
        job.Step = text;
        job.Prompt = prompt;
        job.Agent = agent;
        agent.CurrentPrompt = null;
        agents.Progress(agent, new AgentProgress(text, prompt, n, job.TotalSteps));
    }

    internal async Task RunAsync(OperationJob job, Guid tenantId, Guid actorId, SharedRegistrationRequest req)
    {
        var ct = CancellationToken.None;
        ConnectedAgent? agent = null;
        PlatformUser? actorUser = null;
        var registered = new List<string>();
        var stage = "start";
        try
        {
            actorUser = await db.PlatformUsers.FirstAsync(u => u.Id == actorId, ct);
            var actor = AuditService.User(actorUser);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
            var profile = await RegistrationService.LoadProfileAsync(db, tenantId, req.ProfileId, ct);
            // agent erst nach dem Belegen setzen: Eine Absage wegen agent_busy darf den laufenden Vorgang nicht freigeben.
            var claim = agents.Require(req.AgentId, actorId);
            if (claim.BusyWith is not null) throw ApiException.Conflict("agent_busy", L.T("Der Agent führt bereits einen Vorgang aus."));
            claim.BusyWith = L.T("Geteilter Key");
            agent = claim;
            var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);

            // Vier-Augen-Freigabe (V2): Breakglass nur mit gültiger Freigabe eines zweiten Admins.
            RegistrationApproval? approval = null;
            if (req.Usage == KeyUsage.Breakglass && tenant.RequireBreakglassApproval)
                approval = await ApprovalService.RequireUsableAsync(db, tenantId, actorId, req.ApprovalId, req.EntraUserIds, ct);

            Step(job, agent, 1, L.T("Key wird geprüft"));
            var key = await agents.ReadKeyAsync(agent, ct) ?? throw ApiException.Conflict("no_key", L.T("Am Agent ist kein YubiKey eingesteckt."));
            if (req.ExpectedSerialNumber is { } expected && key.SerialNumber != expected)
                throw ApiException.Conflict("key_changed", L.T("Es steckt ein anderer Key als in der Vorschau. Bitte die Prüfung wiederholen."));
            var users = new List<GraphUser>();
            foreach (var id in req.EntraUserIds)
                users.Add(await graph.GetUserAsync(id, ct) ?? throw ApiException.NotFound(L.F($"Konto {id}")));
            var existing = key.SerialNumber is { } serial ? await keys.FindBySerialAsync(serial, ct) : null;
            var eval = WithoutPinOnSharedKey(registration.Evaluate(tenant, profile, key), existing, existing?.Registrations.Where(r => r.RemovedAt is null).ToList() ?? [], tenantId);
            var overrides = req.Overrides ?? [];
            var reset = req.PerformReset || profile.FactoryResetBeforeRegistration;
            var blocking = eval.Blocking(overrides, reset);
            if (blocking.Count > 0) throw ApiException.Conflict("profile_violation", string.Join(" ", blocking.Select(b => b.Message)));
            var overridden = eval.Violations.Where(v => v.CanOverride && overrides.Contains(v.Code)).ToList();

            if (existing is not null && existing.TenantId is { } otherTenant && otherTenant != tenantId)
                throw ApiException.Conflict("other_tenant", L.F($"Dieser Key ist im Inventar des Tenants „{existing.Tenant?.DisplayName}“ erfasst."));
            var activeRegs = existing?.Registrations.Where(r => r.RemovedAt is null).ToList() ?? [];
            if (!reset && activeRegs.Count > 0 && existing!.Usage == KeyUsage.Personal)
                throw ApiException.Conflict("already_registered", L.F($"Der Key ist als persönlicher Key für {string.Join(", ", activeRegs.Select(r => r.UserPrincipalName))} registriert. Bitte mit Werksreset registrieren."));
            if (!reset && users.FirstOrDefault(u => activeRegs.Any(r => r.EntraUserId == u.Id)) is { } dup)
                throw ApiException.Conflict("already_on_key", L.F($"{dup.UserPrincipalName} ist auf diesem Key schon registriert."));
            if (!reset && key.RemainingDiscoverableCredentials is { } free && users.Count > free)
                throw ApiException.Conflict("no_slots", L.F($"Auf dem Key sind nur noch {free} Passkey-Plätze frei."));

            if (reset)
            {
                stage = "reset";
                if (existing is not null && activeRegs.Count > 0)
                    await keys.RemoveEntraRegistrationsAsync(existing, L.T("Neuvergabe mit Werksreset"), actor, ct);
                Step(job, agent, 2, L.T("Werksreset der FIDO2-Funktion"), L.T("Bitte den Key ab- und wieder anstecken und danach berühren."));
                var resetResult = await agents.CallAsync(agent, (c, t) => c.ResetFido(new ResetRequest { ExpectedSerialNumber = key.SerialNumber }, t), TimeSpan.FromSeconds(150), ct);
                if (!resetResult.Success) throw ApiException.Conflict(resetResult.ErrorCode ?? "reset_failed", L.F($"Werksreset fehlgeschlagen: {resetResult.Error}"));
                audit.Add(actor, AuditActions.KeyReset, tenantId, existing?.Id, key.SerialNumber?.ToString(), new { context = "shared_registration" });
                await db.SaveChangesAsync(ct);
                key = await agents.ReadKeyAsync(agent, ct) ?? throw ApiException.Conflict("no_key", L.T("Nach dem Reset wurde kein Key gefunden."));
                activeRegs = [];
            }

            // PIN: neu setzen (Werkszustand) oder die aktuelle PIN des geteilten Keys verwenden.
            stage = "pin";
            Step(job, agent, 3, key.PinIsSet ? L.T("Aktuelle PIN wird verwendet") : L.T("PIN wird gesetzt"));
            string pin;
            var newPin = !key.PinIsSet;
            if (newPin)
            {
                pin = profile.PinStrategy == PinStrategy.Fixed
                    ? dataProtection.CreateProtector(RegistrationService.FixedPinPurpose).Unprotect(profile.FixedPinProtected ?? throw ApiException.Conflict("profile_invalid", L.T("Im Profil ist keine feste PIN hinterlegt.")))
                    : PinGenerator.Generate(Math.Max(profile.PinLength, key.MinimumPinLength ?? PinGenerator.MinLength));
                var setPin = await agents.CallAsync(agent, (c, t) => c.SetPin(new SetPinRequest { ExpectedSerialNumber = key.SerialNumber, NewPin = pin }, t), TimeSpan.FromSeconds(30), ct);
                if (!setPin.Success) throw ApiException.Conflict(setPin.ErrorCode ?? "set_pin_failed", L.F($"PIN konnte nicht gesetzt werden: {setPin.Error}"));
            }
            else
            {
                pin = req.CurrentPin?.Trim() ?? "";
                if (pin.Length == 0) throw ApiException.Conflict("pin_required", L.T("Auf dem Key ist schon eine PIN gesetzt. Bitte die aktuelle PIN des Keys eingeben (Verwahrer fragen) oder mit Werksreset registrieren."));
            }

            // Inventar-Eintrag vorbereiten, damit jede erfolgreiche Registrierung sofort gespeichert wird.
            stage = "save";
            var inv = existing;
            if (inv is null)
            {
                inv = new YubiKey { TenantId = tenantId, Source = KeySource.Platform, Status = KeyStatus.InStock };
                db.Keys.Add(inv);
            }
            else if (inv.TenantId is null)
            {
                inv.TenantId = tenantId;
                audit.Add(actor, AuditActions.KeyAssignedToTenant, tenantId, inv.Id, inv.SerialNumber?.ToString(), new { from = "partner_stock", context = "shared_registration" });
            }
            if (reset && inv.Status is KeyStatus.Active or KeyStatus.Locked) KeyService.SetStatus(inv, KeyStatus.InStock, L.T("Werksreset"));
            inv.SerialNumber = key.SerialNumber;
            inv.FirmwareVersion = key.FirmwareVersion;
            inv.Model = key.Model;
            inv.FormFactor = key.FormFactor;
            inv.AaGuid = key.AaGuid;
            inv.IsFips = key.IsFips;
            inv.ProfileId = profile.Id;
            var beforeCustodian = inv.Custodian;
            inv.Usage = req.Usage;
            inv.Custodian = req.Custodian;
            inv.UpdatedAt = DateTimeOffset.UtcNow;

            var step = 4;
            foreach (var user in users)
            {
                stage = "credential";
                Step(job, agent, step++, L.F($"Passkey für {user.DisplayName ?? user.UserPrincipalName}"), L.T("Bitte den Key berühren, sobald er blinkt."));
                var options = await graph.GetCreationOptionsAsync(user.Id, 5, ct);
                var cred = await agents.CallAsync(agent, (c, t) => c.MakeCredential(new MakeCredentialRequest
                {
                    ExpectedSerialNumber = key.SerialNumber,
                    Pin = pin,
                    PublicKeyOptionsJson = options.PublicKey.GetRawText(),
                }, t), TimeSpan.FromSeconds(90), ct);
                if (!cred.Success)
                {
                    if (cred.ErrorCode == "pin_rejected" && !newPin)
                        throw ApiException.Conflict("pin_rejected", L.T("Die eingegebene PIN ist falsch. Achtung: Nach 8 Fehleingaben sperrt der Key."));
                    throw ApiException.Conflict(cred.ErrorCode ?? "make_credential_failed", L.F($"Passkey für {user.UserPrincipalName} konnte nicht erzeugt werden: {cred.Error}{(newPin ? "" : L.T(" Steht auf dem Key noch der erzwungene PIN-Wechsel aus, muss der Verwahrer die PIN zuerst ändern."))}"));
                }
                stage = "graph";
                var displayName = DisplayNameTemplate.Render(profile.DisplayNameTemplate, key, user.UserPrincipalName, DateOnly.FromDateTime(DateTime.Today));
                var method = await graph.RegisterFido2MethodAsync(user.Id, displayName, new NewFido2Credential(cred.CredentialId!, cred.ClientDataJson!, cred.AttestationObject!), ct);
                var now = DateTimeOffset.UtcNow;
                inv.AaGuid = cred.AaGuid ?? inv.AaGuid;
                if (inv.Status != KeyStatus.Active) KeyService.SetStatus(inv, KeyStatus.Active);
                db.Registrations.Add(new KeyRegistration
                {
                    TenantId = tenantId, KeyId = inv.Id, EntraUserId = user.Id, UserPrincipalName = user.UserPrincipalName, UserDisplayName = user.DisplayName,
                    EntraMethodId = method.Id, DisplayName = method.DisplayName ?? displayName, AaGuid = method.AaGuid ?? inv.AaGuid, RegisteredAt = now,
                    RegisteredById = actorUser.Id, RegisteredByName = actorUser.DisplayName, ProfileId = profile.Id, Source = KeySource.Platform, LastSeenInSyncAt = now,
                });
                audit.Add(actor, AuditActions.KeyRegistered, tenantId, inv.Id, user.UserPrincipalName, new
                {
                    serial = key.SerialNumber, firmware = key.FirmwareVersion, model = key.Model, profile = profile.Name, usage = req.Usage.ToString(), custodian = req.Custodian,
                    pinStrategy = newPin ? profile.PinStrategy.ToString() : "existing", reset, entraMethodId = method.Id, agent = agent.MachineName, approval = approval?.Id,
                });
                await TodoService.CompleteForUserAsync(db, audit, tenantId, user.Id, inv.Id, actor, ct);
                await db.SaveChangesAsync(ct);
                registered.Add(user.UserPrincipalName);
            }

            foreach (var o in overridden)
                audit.Add(actor, AuditActions.KeyProfileOverride, tenantId, inv.Id, req.Custodian, new { code = o.Code.ToString(), o.Message, profile = profile.Name });
            if (beforeCustodian != req.Custodian)
                audit.Add(actor, AuditActions.KeyCustodianChanged, tenantId, inv.Id, inv.SerialNumber?.ToString(), new { before = beforeCustodian, after = req.Custodian, usage = req.Usage.ToString() });

            // PIN-Wechsel nur erzwingen, wenn in diesem Lauf eine neue PIN gesetzt wurde.
            var forced = false;
            if (newPin && profile.ForcePinChange && key.SupportsForcePinChange)
            {
                stage = "force_pin_change";
                Step(job, agent, step, L.T("PIN-Wechsel wird erzwungen"));
                var force = await agents.CallAsync(agent, (c, t) => c.ForcePinChange(new ForcePinChangeRequest { ExpectedSerialNumber = key.SerialNumber, Pin = pin }, t), TimeSpan.FromSeconds(30), ct);
                forced = force.Success;
                if (!force.Success) job.Notes.Add(L.F($"PIN-Wechsel konnte nicht erzwungen werden: {force.Error}."));
            }
            if (newPin) inv.PinChangeForced = forced;
            if (approval is not null) ApprovalService.MarkUsed(approval, inv.Id, actor, audit);
            await db.SaveChangesAsync(ct);

            job.KeyId = inv.Id;
            job.SerialNumber = key.SerialNumber;
            job.PinChangeForced = forced;
            if (newPin)
            {
                var settings = await db.Installation.AsNoTracking().FirstAsync(ct);
                var brand = await new Setup.BrandingService(db).ResolveAsync(tenant.Id, ct);
                using var brandScope = Theme.Use(brand);
                var pdf = PinLetterPdf.Create(new PinLetterData(
                    brand.CompanyName ?? settings.OrganizationName, tenant.DisplayName, req.Custodian!, req.Usage == KeyUsage.Breakglass ? L.T("Breakglass-Key") : L.T("Geteilter Key"), pin,
                    key.Model ?? "YubiKey", key.SerialNumber, forced, DateTime.Now, users.Select(u => u.UserPrincipalName).ToList(),
                InfoUrl: PinLetterPdf.InfoUrl(await PinLetterPdf.BaseUrlAsync(db, ylmOptions.Value, ct: ct), tenant.Id)));
                job.PinLetterId = letters.Add(actorId, pdf, L.F($"PIN-Brief geteilter Key {key.SerialNumber}.pdf"));
            }
            else job.Notes.Add(L.T("Die PIN des Keys bleibt unverändert; es gibt deshalb keinen neuen PIN-Brief."));
            job.Step = L.F($"{users.Count} Konto/Konten registriert");
            job.Prompt = L.T("Key abziehen und dem Verwahrer übergeben.");
            if (agent is not null) agent.BusyWith = null; // vor dem Endzustand freigeben, sonst meldet ein sofortiger Folgeauftrag agent_busy
            job.State = JobState.Succeeded;
            agents.Progress(agent, new AgentProgress(L.T("Geteilter Key fertig"), L.F($"{users.Count} Konten registriert. Key kann abgezogen werden."), job.TotalSteps, job.TotalSteps, Done: true));
        }
        catch (Exception ex)
        {
            var (code, message) = ex switch
            {
                ApiException a => (a.Code, a.Message),
                DomainException d => (d.Code, d.Message),
                GraphException g => ("graph_error", g.Message),
                _ => ("unexpected", L.F($"Unerwarteter Fehler: {ex.Message}")),
            };
            if (ex is not ApiException) logger.LogError(ex, "Shared registration failed at {Stage}", stage);
            if (registered.Count > 0) message += L.F($" Bereits registriert: {string.Join(", ", registered)}.");
            if (agent is not null) agent.BusyWith = null; // vor dem Endzustand freigeben, sonst meldet ein sofortiger Folgeauftrag agent_busy
            job.State = JobState.Failed;
            job.Error = message;
            job.ErrorCode = code;
            try
            {
                db.ChangeTracker.Clear();
                if (actorUser is not null)
                    await audit.WriteAsync(AuditService.User(actorUser), AuditActions.KeyRegistrationFailed, tenantId, null, string.Join(", ", req.EntraUserIds), new { stage, code, message, usage = req.Usage.ToString() });
            }
            catch (Exception auditEx) { logger.LogError(auditEx, "Could not audit failed shared registration"); }
            if (agent is not null) agents.Progress(agent, new AgentProgress(L.T("Registrierung fehlgeschlagen"), message, Failed: true));
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            if (agent is not null) agent.BusyWith = null;
        }
    }

    /// <summary>Entfernt ein Konto von einem geteilten Key: der Passkey wird in Entra gelöscht, die übrigen Konten bleiben.</summary>
    public async Task RemoveAccountAsync(YubiKey key, Guid registrationId, AuditActor actor, CancellationToken ct = default)
    {
        var reg = key.Registrations.FirstOrDefault(r => r.Id == registrationId && r.RemovedAt == null) ?? throw ApiException.NotFound(L.T("Registrierung"));
        var tenant = await db.Tenants.FirstAsync(t => t.Id == key.TenantId, ct);
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        await graph.DeleteFido2MethodAsync(reg.EntraUserId, reg.EntraMethodId, ct);
        reg.RemovedAt = DateTimeOffset.UtcNow;
        reg.RemovedReason = L.T("Konto vom Key entfernt");
        key.UpdatedAt = reg.RemovedAt.Value;
        audit.Add(actor, AuditActions.RegistrationRemoved, key.TenantId, key.Id, reg.UserPrincipalName, new { reg.EntraMethodId, usage = key.Usage.ToString() });
        // Der physische Passkey bleibt auf dem Key, ist ohne Entra-Eintrag aber wertlos.
        if (!key.Registrations.Any(r => r.RemovedAt == null)) key.StatusNote = L.T("Alle Konten entfernt; für eine Neuvergabe bitte zurücksetzen.");
        await db.SaveChangesAsync(ct);
    }
}
