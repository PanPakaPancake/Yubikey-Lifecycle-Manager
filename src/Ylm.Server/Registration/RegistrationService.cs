using System.Text.Json;
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
using Ylm.Server.Tenants;
using Ylm.Contracts.Localization;

namespace Ylm.Server.Registration;

public sealed record RegistrationPreviewRequest(string EntraUserId, Guid ProfileId, Guid AgentId);

public sealed record RegistrationRequest(string EntraUserId, Guid ProfileId, Guid AgentId, List<ViolationCode>? Overrides, bool PerformReset, int? ExpectedSerialNumber);

public sealed record RegistrationPreview(
    KeyInfo Key,
    IReadOnlyList<ProfileViolation> Violations,
    bool ResetRequired,
    bool ResetPlanned,
    bool WillForcePinChange,
    string DisplayName,
    object? InventoryKey,
    object User,
    int UserActiveKeys);

/// <summary>Einzel-Wizard (planning 6.3.1): person → key → profile → registration → PIN PDF.</summary>
public sealed class RegistrationService(
    YlmDbContext db,
    IGraphClientFactory graphFactory,
    AgentGateway agents,
    KeyService keys,
    AuditService audit,
    IDataProtectionProvider dataProtection,
    PinLetterStore letters,
    Microsoft.Extensions.Options.IOptions<YlmOptions> ylmOptions,
    ILogger<RegistrationService> logger)
{
    public const string FixedPinPurpose = "Ylm.Profile.FixedPin";

    public static async Task<RegistrationProfile> LoadProfileAsync(YlmDbContext db, Guid tenantId, Guid profileId, CancellationToken ct = default) =>
        await db.Profiles.FirstOrDefaultAsync(p => p.Id == profileId && (p.TenantId == tenantId || p.TenantId == null), ct)
        ?? throw ApiException.NotFound(L.T("Profil"));

    private static ProfileViolation? TenantAaguidViolation(Tenant tenant, KeyInfo key)
    {
        var policy = TenantCheckService.ReadPolicy(tenant);
        if (policy is null || !policy.KeyRestrictionsEnforced || key.AaGuid is not { } aaguid) return null;
        var allowed = policy.EnforcementType == "block" ? !policy.AaGuids.Contains(aaguid) : policy.AaGuids.Contains(aaguid);
        return allowed ? null : new ProfileViolation(ViolationCode.AaguidNotAllowedByTenant,
            L.F($"Die FIDO2-Richtlinie des Tenants lässt diesen Key-Typ (AAGUID {aaguid}) nicht zu. Bitte zuerst die Tenant-Checks beheben."), false, false);
    }

    public EvaluationResult Evaluate(Tenant tenant, RegistrationProfile profile, KeyInfo key)
    {
        var result = ProfileEvaluator.Evaluate(profile.ToSettings(), key);
        var aaguid = TenantAaguidViolation(tenant, key);
        return aaguid is null ? result : new EvaluationResult([.. result.Violations, aaguid]);
    }

    public async Task<RegistrationPreview> PreviewAsync(Guid tenantId, PlatformUser actor, RegistrationPreviewRequest req, CancellationToken ct = default)
    {
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        var profile = await LoadProfileAsync(db, tenantId, req.ProfileId, ct);
        var agent = agents.Require(req.AgentId, actor.Id);
        var key = await agents.ReadKeyAsync(agent, ct) ?? throw ApiException.Conflict("no_key", L.T("Am Agent ist kein YubiKey eingesteckt."));
        var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);
        var user = await graph.GetUserAsync(req.EntraUserId, ct) ?? throw ApiException.NotFound(L.T("Person"));
        var eval = Evaluate(tenant, profile, key);
        var resetRequired = eval.Violations.Any(v => v.ResolvedByReset);
        var existing = key.SerialNumber is { } serial ? await keys.FindBySerialAsync(serial, ct) : null;
        var userKeys = await db.Registrations.CountAsync(r => r.TenantId == tenantId && r.EntraUserId == user.Id && r.RemovedAt == null, ct);
        return new RegistrationPreview(
            key,
            eval.Violations,
            resetRequired,
            resetRequired || profile.FactoryResetBeforeRegistration,
            profile.ForcePinChange && key.SupportsForcePinChange,
            DisplayNameTemplate.Render(profile.DisplayNameTemplate, key, user.UserPrincipalName, DateOnly.FromDateTime(DateTime.Today)),
            existing is null ? null : new
            {
                existing.Id, existing.TenantId, TenantName = existing.Tenant?.DisplayName, Status = existing.Status.ToString(),
                ActiveRegistrations = existing.Registrations.Where(r => r.RemovedAt == null).Select(r => r.UserPrincipalName).ToList(),
            },
            new { user.Id, user.UserPrincipalName, user.DisplayName, user.Department, user.AccountEnabled },
            userKeys);
    }

    /// <summary>Starts the registration as a background job and returns it for progress polling.</summary>
    public OperationJob Start(Guid tenantId, PlatformUser actor, RegistrationRequest req, JobStore jobs, IServiceScopeFactory scopes, Guid? batchItemId = null, Guid? jobId = null)
    {
        var job = jobs.Add(new OperationJob { Id = jobId ?? Guid.NewGuid(), UserId = actor.Id, TenantId = tenantId, Kind = "registration", TotalSteps = 7, BatchItemId = batchItemId });
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            await svc.RunAsync(job, tenantId, actor.Id, req);
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

    internal async Task RunAsync(OperationJob job, Guid tenantId, Guid actorId, RegistrationRequest req)
    {
        var ct = CancellationToken.None;
        ConnectedAgent? agent = null;
        PlatformUser? actorUser = null;
        var stage = "start";
        // Stapel: „Erfolgreich“ erst melden, wenn der Stapeleintrag gespeichert ist, sonst sieht der Aufrufer einen halben Stand.
        var succeeded = false;
        try
        {
            actorUser = await db.PlatformUsers.FirstAsync(u => u.Id == actorId, ct);
            var actor = AuditService.User(actorUser);
            var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
            var profile = await LoadProfileAsync(db, tenantId, req.ProfileId, ct);
            // agent erst nach dem Belegen setzen: Eine Absage wegen agent_busy darf den laufenden Vorgang nicht freigeben.
            var claim = agents.Require(req.AgentId, actorId);
            if (claim.BusyWith is not null) throw ApiException.Conflict("agent_busy", L.T("Der Agent führt bereits einen Vorgang aus."));
            claim.BusyWith = L.T("Registrierung");
            agent = claim;
            var graph = await graphFactory.ForTenantAsync(tenant.EntraTenantId, ct);

            // 1. Key auslesen und gegen das Profil prüfen
            Step(job, agent, 1, L.T("Key wird geprüft"));
            var key = await agents.ReadKeyAsync(agent, ct) ?? throw ApiException.Conflict("no_key", L.T("Am Agent ist kein YubiKey eingesteckt."));
            if (req.ExpectedSerialNumber is { } expected && key.SerialNumber != expected)
                throw ApiException.Conflict("key_changed", L.T("Es steckt ein anderer Key als in der Vorschau. Bitte den Wizard neu starten."));
            var user = await graph.GetUserAsync(req.EntraUserId, ct) ?? throw ApiException.NotFound(L.T("Person"));
            var eval = Evaluate(tenant, profile, key);
            var overrides = req.Overrides ?? [];
            var reset = req.PerformReset || profile.FactoryResetBeforeRegistration;
            var blocking = eval.Blocking(overrides, reset);
            if (blocking.Count > 0) throw ApiException.Conflict("profile_violation", string.Join(" ", blocking.Select(b => b.Message)));
            var overridden = eval.Violations.Where(v => v.CanOverride && overrides.Contains(v.Code)).ToList();

            // 2. Inventar prüfen
            var existing = key.SerialNumber is { } serial ? await keys.FindBySerialAsync(serial, ct) : null;
            // Keys aus dem Partnerlager (TenantId null) werden bei der Registrierung dem Tenant zugewiesen.
            if (existing is not null && existing.TenantId is { } otherTenant && otherTenant != tenantId)
                throw ApiException.Conflict("other_tenant", L.F($"Dieser Key ist im Inventar des Tenants „{existing.Tenant?.DisplayName}“ erfasst."));
            var activeRegs = existing?.Registrations.Where(r => r.RemovedAt is null).ToList() ?? [];
            if (activeRegs.Count > 0 && !reset)
                throw ApiException.Conflict("already_registered",
                    L.F($"Der Key ist bereits für {string.Join(", ", activeRegs.Select(r => r.UserPrincipalName))} registriert. Weitere Konten kommen über „Geteilter Key“ dazu; für eine Neuvergabe bitte mit Werksreset registrieren."));

            // 3. Werksreset (optional)
            if (reset)
            {
                stage = "reset";
                if (existing is not null && activeRegs.Count > 0)
                    await keys.RemoveEntraRegistrationsAsync(existing, L.T("Neuvergabe mit Werksreset"), actor, ct);
                Step(job, agent, 2, L.T("Werksreset der FIDO2-Funktion"), L.T("Bitte den Key ab- und wieder anstecken und danach berühren."));
                var resetResult = await agents.CallAsync(agent, (c, t) => c.ResetFido(new ResetRequest { ExpectedSerialNumber = key.SerialNumber }, t), TimeSpan.FromSeconds(150), ct);
                if (!resetResult.Success) throw ApiException.Conflict(resetResult.ErrorCode ?? "reset_failed", L.F($"Werksreset fehlgeschlagen: {resetResult.Error}"));
                audit.Add(actor, AuditActions.KeyReset, tenantId, existing?.Id, key.SerialNumber?.ToString(), new { context = "registration" });
                await db.SaveChangesAsync(ct);
                key = await agents.ReadKeyAsync(agent, ct) ?? throw ApiException.Conflict("no_key", L.T("Nach dem Reset wurde kein Key gefunden."));
            }

            // 4. PIN setzen
            stage = "pin";
            Step(job, agent, 3, L.T("PIN wird gesetzt"));
            var pin = profile.PinStrategy == PinStrategy.Fixed
                ? dataProtection.CreateProtector(FixedPinPurpose).Unprotect(profile.FixedPinProtected ?? throw ApiException.Conflict("profile_invalid", L.T("Im Profil ist keine feste PIN hinterlegt.")))
                : PinGenerator.Generate(Math.Max(profile.PinLength, key.MinimumPinLength ?? PinGenerator.MinLength));
            var setPin = await agents.CallAsync(agent, (c, t) => c.SetPin(new SetPinRequest { ExpectedSerialNumber = key.SerialNumber, NewPin = pin }, t), TimeSpan.FromSeconds(30), ct);
            if (!setPin.Success) throw ApiException.Conflict(setPin.ErrorCode ?? "set_pin_failed", L.F($"PIN konnte nicht gesetzt werden: {setPin.Error}"));

            // 5. Passkey erzeugen (creationOptions von Graph, makeCredential am Key)
            stage = "credential";
            Step(job, agent, 4, L.T("Passkey wird auf dem Key erzeugt"), L.T("Bitte den Key berühren, sobald er blinkt."));
            var options = await graph.GetCreationOptionsAsync(user.Id, 5, ct);
            var cred = await agents.CallAsync(agent, (c, t) => c.MakeCredential(new MakeCredentialRequest
            {
                ExpectedSerialNumber = key.SerialNumber,
                Pin = pin,
                PublicKeyOptionsJson = options.PublicKey.GetRawText(),
            }, t), TimeSpan.FromSeconds(90), ct);
            if (!cred.Success) throw ApiException.Conflict(cred.ErrorCode ?? "make_credential_failed", L.F($"Passkey konnte nicht erzeugt werden: {cred.Error}"));

            // 6. In Entra registrieren
            stage = "graph";
            Step(job, agent, 5, L.T("Passkey wird in Entra registriert"));
            var displayName = DisplayNameTemplate.Render(profile.DisplayNameTemplate, key, user.UserPrincipalName, DateOnly.FromDateTime(DateTime.Today));
            var method = await graph.RegisterFido2MethodAsync(user.Id, displayName, new NewFido2Credential(cred.CredentialId!, cred.ClientDataJson!, cred.AttestationObject!), ct);

            // 7. PIN-Wechsel erzwingen
            stage = "force_pin_change";
            var forced = false;
            if (profile.ForcePinChange && key.SupportsForcePinChange)
            {
                Step(job, agent, 6, L.T("PIN-Wechsel wird erzwungen"));
                var force = await agents.CallAsync(agent, (c, t) => c.ForcePinChange(new ForcePinChangeRequest { ExpectedSerialNumber = key.SerialNumber, Pin = pin }, t), TimeSpan.FromSeconds(30), ct);
                forced = force.Success;
                if (!force.Success) job.Notes.Add(L.F($"PIN-Wechsel konnte nicht erzwungen werden: {force.Error}. Im PIN-Brief steht deshalb „Bitte PIN selbst ändern“."));
            }

            // Inventar und Audit
            stage = "save";
            Step(job, agent, 7, L.T("Inventar wird aktualisiert"));
            var now = DateTimeOffset.UtcNow;
            var inv = existing;
            if (inv is null)
            {
                inv = new YubiKey { TenantId = tenantId, Source = KeySource.Platform, Status = KeyStatus.InStock };
                db.Keys.Add(inv);
            }
            else
            {
                if (inv.Status is KeyStatus.Active or KeyStatus.Locked) KeyService.SetStatus(inv, KeyStatus.InStock, L.T("Werksreset"));
                if (inv.TenantId is null)
                {
                    inv.TenantId = tenantId;
                    audit.Add(actor, AuditActions.KeyAssignedToTenant, tenantId, inv.Id, inv.SerialNumber?.ToString(), new { from = "partner_stock", context = "registration" });
                }
            }
            inv.SerialNumber = key.SerialNumber;
            inv.FirmwareVersion = key.FirmwareVersion;
            inv.Model = key.Model;
            inv.FormFactor = key.FormFactor;
            inv.AaGuid = cred.AaGuid ?? key.AaGuid;
            inv.IsFips = key.IsFips;
            inv.ProfileId = profile.Id;
            inv.PinChangeForced = forced;
            inv.Usage = KeyUsage.Personal;
            inv.Custodian = null;
            KeyService.SetStatus(inv, KeyStatus.Active);
            db.Registrations.Add(new KeyRegistration
            {
                TenantId = tenantId,
                KeyId = inv.Id,
                EntraUserId = user.Id,
                UserPrincipalName = user.UserPrincipalName,
                UserDisplayName = user.DisplayName,
                EntraMethodId = method.Id,
                DisplayName = method.DisplayName ?? displayName,
                AaGuid = method.AaGuid ?? inv.AaGuid,
                RegisteredAt = now,
                RegisteredById = actorUser.Id,
                RegisteredByName = actorUser.DisplayName,
                ProfileId = profile.Id,
                Source = KeySource.Platform,
                LastSeenInSyncAt = now,
            });
            foreach (var o in overridden)
                audit.Add(actor, AuditActions.KeyProfileOverride, tenantId, inv.Id, user.UserPrincipalName, new { code = o.Code.ToString(), o.Message, profile = profile.Name });
            audit.Add(actor, AuditActions.KeyRegistered, tenantId, inv.Id, user.UserPrincipalName, new
            {
                serial = key.SerialNumber, firmware = key.FirmwareVersion, model = key.Model, profile = profile.Name,
                pinStrategy = profile.PinStrategy.ToString(), pinChangeForced = forced, reset, overrides = overridden.Select(o => o.Code.ToString()),
                entraMethodId = method.Id, agent = agent.MachineName,
            });
            await TodoService.CompleteForUserAsync(db, audit, tenantId, user.Id, inv.Id, actor, ct);
            await db.SaveChangesAsync(ct);
            job.KeyId = inv.Id;
            job.SerialNumber = key.SerialNumber;
            job.PinChangeForced = forced;

            var settings = await db.Installation.AsNoTracking().FirstAsync(ct);
            var brand = await new Setup.BrandingService(db).ResolveAsync(tenantId, ct);
            using var brandScope = Theme.Use(brand);
            var pdf = PinLetterPdf.Create(new PinLetterData(
                brand.CompanyName ?? settings.OrganizationName, tenant.DisplayName, user.DisplayName ?? user.UserPrincipalName, user.UserPrincipalName, pin,
                key.Model ?? "YubiKey", key.SerialNumber, forced, DateTime.Now,
                InfoUrl: PinLetterPdf.InfoUrl(await PinLetterPdf.BaseUrlAsync(db, ylmOptions.Value, ct: ct), tenantId)));
            job.PinLetterId = letters.Add(actorId, pdf, L.F($"PIN-Brief {user.UserPrincipalName} {key.SerialNumber}.pdf"),
                job.BatchItemId is null ? null : PinLetterStore.BatchLifetime);
            job.Step = L.T("Registrierung abgeschlossen");
            job.Prompt = L.T("Key abziehen und zusammen mit dem PIN-Brief übergeben.");
            succeeded = true;
            if (agent is not null) agent.BusyWith = null;
            if (job.BatchItemId is null) job.State = JobState.Succeeded;
            agents.Progress(agent, new AgentProgress(L.T("Registrierung abgeschlossen"), L.F($"{user.DisplayName}: Key kann abgezogen werden."), 7, 7, Done: true));
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
            if (ex is not ApiException) logger.LogError(ex, "Registration failed at {Stage}", stage);
            if (stage is "credential" or "graph" or "force_pin_change" or "save")
                message += L.T(" Auf dem Key ist bereits eine PIN gesetzt; für einen neuen Versuch mit Werksreset registrieren.");
            succeeded = false;
            if (agent is not null) agent.BusyWith = null; // vor dem Endzustand freigeben, sonst meldet ein sofortiger Folgeauftrag agent_busy
            job.State = JobState.Failed;
            job.Error = message;
            job.ErrorCode = code;
            try
            {
                db.ChangeTracker.Clear();
                if (actorUser is not null)
                    await audit.WriteAsync(AuditService.User(actorUser), AuditActions.KeyRegistrationFailed, tenantId, null, req.EntraUserId, new { stage, code, message });
            }
            catch (Exception auditEx) { logger.LogError(auditEx, "Could not audit failed registration"); }
            if (agent is not null) agents.Progress(agent, new AgentProgress(L.T("Registrierung fehlgeschlagen"), message, Failed: true));
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            if (job.BatchItemId is { } itemId)
            {
                try { await BatchService.FinishItemAsync(db, audit, itemId, job, succeeded, actorUser); }
                catch (Exception batchEx) { logger.LogError(batchEx, "Could not update batch item {Item}", itemId); }
                if (succeeded) job.State = JobState.Succeeded;
            }
            if (agent is not null) agent.BusyWith = null;
        }
    }

    /// <summary>Zurücksetzen: removes passkeys in Entra, resets FIDO2 on the key, back to stock.</summary>
    public OperationJob StartReset(YubiKey key, PlatformUser actor, Guid agentId, bool toPartnerStock, JobStore jobs, IServiceScopeFactory scopes, Guid? removalApprovalId = null)
    {
        var job = jobs.Add(new OperationJob { UserId = actor.Id, TenantId = key.TenantId, Kind = "reset", TotalSteps = 3, KeyId = key.Id });
        var keyId = key.Id;
        _ = Task.Run(async () =>
        {
            using var scope = scopes.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<RegistrationService>();
            await svc.RunResetAsync(job, keyId, actor.Id, agentId, toPartnerStock, removalApprovalId);
        });
        return job;
    }

    internal async Task RunResetAsync(OperationJob job, Guid keyId, Guid actorId, Guid agentId, bool toPartnerStock = false, Guid? removalApprovalId = null)
    {
        ConnectedAgent? agent = null;
        try
        {
            var actorUser = await db.PlatformUsers.FirstAsync(u => u.Id == actorId);
            var actor = AuditService.User(actorUser);
            var key = await db.Keys.Include(k => k.Registrations).FirstAsync(k => k.Id == keyId);
            KeyLifecycle.EnsureTransition(key.Status, KeyStatus.InStock);
            // agent erst nach dem Belegen setzen: Eine Absage wegen agent_busy darf den laufenden Vorgang nicht freigeben.
            var claim = agents.Require(agentId, actorId);
            if (claim.BusyWith is not null) throw ApiException.Conflict("agent_busy", L.T("Der Agent führt bereits einen Vorgang aus."));
            claim.BusyWith = L.T("Zurücksetzen");
            agent = claim;
            Step(job, agent, 1, L.T("Key wird geprüft"));
            var info = await agents.ReadKeyAsync(agent) ?? throw ApiException.Conflict("no_key", L.T("Am Agent ist kein YubiKey eingesteckt."));
            if (key.SerialNumber is null || info.SerialNumber != key.SerialNumber)
                throw ApiException.Conflict("wrong_key", L.F($"Es steckt Key {info.SerialNumber}, erwartet wird {key.SerialNumber}."));
            Step(job, agent, 2, L.T("Passkeys in Entra werden entfernt"));
            // Vier-Augen (Breakglass): beim Start geprüft, verbraucht, sobald die Passkeys in Entra entfernt sind.
            if (removalApprovalId is { } approvalId)
                await ApprovalService.RequireRemovalAsync(db, key, actorId, RemovalAction.Reset, null, approvalId);
            await keys.RemoveEntraRegistrationsAsync(key, L.T("Zurückgesetzt"), actor);
            if (removalApprovalId is { } usedId && await db.Approvals.FirstOrDefaultAsync(a => a.Id == usedId) is { } used)
            {
                ApprovalService.MarkRemovalUsed(used, actor, audit);
                await db.SaveChangesAsync();
            }
            Step(job, agent, 3, L.T("Werksreset der FIDO2-Funktion"), L.T("Bitte den Key ab- und wieder anstecken und danach berühren."));
            var result = await agents.CallAsync(agent, (c, t) => c.ResetFido(new ResetRequest { ExpectedSerialNumber = key.SerialNumber }, t), TimeSpan.FromSeconds(150));
            if (!result.Success) throw ApiException.Conflict(result.ErrorCode ?? "reset_failed", L.F($"Werksreset fehlgeschlagen: {result.Error}"));
            KeyService.SetStatus(key, KeyStatus.InStock, L.T("Zurückgesetzt"));
            key.PinChangeForced = null;
            key.ProfileId = null;
            audit.Add(actor, AuditActions.KeyReset, key.TenantId, key.Id, key.SerialNumber?.ToString(), new { agent = agent.MachineName, toPartnerStock });
            if (toPartnerStock) KeyService.MoveToPartnerStock(key, actor, audit);
            await db.SaveChangesAsync();
            job.Step = toPartnerStock ? L.T("Key zurückgesetzt und im Partnerlager") : L.T("Key zurückgesetzt und im Tenant-Lager bereit");
            job.Prompt = null;
            if (agent is not null) agent.BusyWith = null; // vor dem Endzustand freigeben, sonst meldet ein sofortiger Folgeauftrag agent_busy
            job.State = JobState.Succeeded;
            agents.Progress(agent, new AgentProgress(L.T("Key zurückgesetzt"), L.T("Der Key ist wieder im Lager."), 3, 3, Done: true));
        }
        catch (Exception ex)
        {
            if (agent is not null) agent.BusyWith = null; // vor dem Endzustand freigeben, sonst meldet ein sofortiger Folgeauftrag agent_busy
            job.State = JobState.Failed;
            job.Error = ex is ApiException or DomainException or GraphException ? ex.Message : L.F($"Unerwarteter Fehler: {ex.Message}");
            job.ErrorCode = (ex as ApiException)?.Code ?? (ex as DomainException)?.Code;
            if (ex is not ApiException) logger.LogError(ex, "Reset failed");
            if (agent is not null) agents.Progress(agent, new AgentProgress(L.T("Zurücksetzen fehlgeschlagen"), job.Error, Failed: true));
        }
        finally
        {
            job.FinishedAt = DateTimeOffset.UtcNow;
            if (agent is not null) agent.BusyWith = null;
        }
    }
}
