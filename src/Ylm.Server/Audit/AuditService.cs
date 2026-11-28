using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ylm.Core;
using Ylm.Server.Data;

namespace Ylm.Server.Audit;

public static class AuditActions
{
    public const string SetupCompleted = "setup.completed";
    public const string SetupAppProvisioned = "setup.app_provisioned";
    public const string Login = "auth.login";
    public const string LoginDenied = "auth.login_denied";
    public const string UserCreated = "user.created";
    public const string UserUpdated = "user.updated";
    public const string UserSetupLinkCreated = "user.setup_link_created";
    public const string UserPasskeyAdded = "user.passkey_added";
    public const string UserPasskeyRemoved = "user.passkey_removed";
    public const string RecoveryCodesCreated = "auth.recovery_codes_created";
    public const string RecoveryCodeUsed = "auth.recovery_code_used";
    public const string UpdateStarted = "platform.update_started";
    public const string HostingChanged = "platform.hosting_changed";
    public const string RedirectUrisAdded = "platform.redirect_uris_added";
    public const string LoginSettingsChanged = "settings.login_changed";
    public const string AgentUpdatePolicyChanged = "settings.agent_update_policy_changed";
    public const string AgentUpdateRequested = "agent.update_requested";
    public const string BrandingChanged = "settings.branding_changed";
    public const string TenantOnboarded = "tenant.onboarded";
    public const string TenantUpdated = "tenant.updated";
    /// <summary>Schutz geändert: Vier-Augen für Breakglass, geschützte Admins oder Schutzgruppen (eigenes Ereignis für Alarme).</summary>
    public const string TenantProtectionChanged = "tenant.protection_changed";
    public const string TenantChecked = "tenant.checked";
    public const string TenantPolicyFixed = "tenant.policy_fixed";
    public const string TenantOffboarded = "tenant.offboarded";
    public const string TenantDataDeleted = "tenant.data_deleted";
    public const string SyncCompleted = "sync.completed";
    public const string SyncFailed = "sync.failed";
    public const string RegistrationImported = "sync.registration_imported";
    public const string RegistrationRemovedInEntra = "sync.registration_removed";
    public const string KeyStockIntake = "key.stock_intake";
    public const string KeyImported = "key.imported";
    public const string KeyUpdated = "key.updated";
    public const string KeyRegistered = "key.registered";
    public const string KeyRegistrationFailed = "key.registration_failed";
    public const string KeyProfileOverride = "key.profile_override";
    public const string KeyLocked = "key.locked";
    public const string KeyReset = "key.reset";
    public const string KeyRetired = "key.retired";
    public const string KeyDeleted = "key.deleted";
    public const string KeyMovedToPartnerStock = "key.moved_to_partner_stock";
    public const string KeyAssignedToTenant = "key.assigned_to_tenant";
    public const string RoleCreated = "role.created";
    public const string RoleUpdated = "role.updated";
    public const string RoleDeleted = "role.deleted";
    public const string ProfileCreated = "profile.created";
    public const string ProfileUpdated = "profile.updated";
    public const string ProfileDeleted = "profile.deleted";
    public const string FieldsUpdated = "fields.updated";
    public const string AgentPaired = "agent.paired";
    public const string AgentRevoked = "agent.revoked";
    public const string ApiKeyCreated = "apikey.created";
    public const string ApiKeyRevoked = "apikey.revoked";
    public const string ApiKeyScopeChanged = "apikey.scope_changed";
    public const string GroupProfilesUpdated = "groupprofiles.updated";
    public const string ExportCreated = "export.created";
    public const string PinLetterDownloaded = "pin_letter.downloaded";
    public const string BatchCreated = "batch.created";
    public const string BatchChanged = "batch.changed";
    public const string BatchCompleted = "batch.completed";
    public const string BatchCancelled = "batch.cancelled";
    public const string KeyCustodianChanged = "key.custodian_changed";
    public const string RegistrationRemoved = "registration.removed";
    public const string KeyIdentified = "key.identified";
    public const string ApprovalRequested = "approval.requested";
    public const string ApprovalApproved = "approval.approved";
    public const string ApprovalRejected = "approval.rejected";
    public const string ApprovalCancelled = "approval.cancelled";
    public const string ApprovalUsed = "approval.used";
    public const string TodoCreated = "todo.created";
    public const string TodoCompleted = "todo.completed";
    public const string TodoCancelled = "todo.cancelled";
    public const string WebhookCreated = "webhook.created";
    public const string WebhookUpdated = "webhook.updated";
    public const string WebhookDeleted = "webhook.deleted";
    public const string SmtpChanged = "settings.smtp_changed";
    public const string ReportScheduleChanged = "report_schedule.changed";
    public const string ReportSent = "report.sent";
    public const string ReportFailed = "report.failed";
    public const string AuthMethodRemoved = "authmethod.removed";
    public const string TapCreated = "authmethod.tap_created";
    public const string TapPolicyEnabled = "tenant.tap_policy_enabled";
    public const string AppCertificateExpiring = "platform.certificate_expiring";
    public const string AppCertificateRenewed = "platform.certificate_renewed";
    public const string AppCertificateRenewalFailed = "platform.certificate_renewal_failed";
    public const string ReminderUnusedKeys = "reminder.unused_keys";
    public const string ReminderNoSpareKey = "reminder.no_spare_key";
    public const string ReminderLeavers = "reminder.leavers";
    public const string ReminderRetiredWithPasskey = "reminder.retired_with_passkey";
    public const string ReminderDismissed = "reminder.dismissed";
    public const string ReminderRestored = "reminder.restored";
}

public sealed record AuditActor(ActorType Type, Guid? Id, string? Name);

/// <summary>Adds audit entries to the current unit of work; they are chained on SaveChanges.</summary>
public sealed class AuditService(YlmDbContext db, IHttpContextAccessor http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AuditActor User(PlatformUser u) => new(ActorType.User, u.Id, $"{u.DisplayName} <{u.Upn}>");
    public static readonly AuditActor System = new(ActorType.System, null, "System");

    public AuditEntry Add(AuditActor actor, string action, Guid? tenantId = null, Guid? keyId = null, string? target = null, object? details = null)
    {
        var entry = new AuditEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            ActorType = actor.Type,
            ActorId = actor.Id,
            ActorName = actor.Name,
            Action = action,
            TenantId = tenantId,
            KeyId = keyId,
            Target = target,
            Details = details is null ? null : JsonSerializer.Serialize(details, Json),
            IpAddress = http.HttpContext?.Connection.RemoteIpAddress?.ToString(),
        };
        db.AuditEntries.Add(entry);
        return entry;
    }

    public async Task WriteAsync(AuditActor actor, string action, Guid? tenantId = null, Guid? keyId = null, string? target = null, object? details = null)
    {
        Add(actor, action, tenantId, keyId, target, details);
        await db.SaveChangesAsync();
    }

    /// <summary>Verifies the complete hash chain. Returns the first broken sequence or null.</summary>
    public async Task<(bool Valid, long Checked, long? BrokenAt)> VerifyAsync(CancellationToken ct = default)
    {
        var prev = AuditChain.Genesis;
        long count = 0, expectedSeq = 0;
        await foreach (var a in db.AuditEntries.AsNoTracking().OrderBy(a => a.Sequence).AsAsyncEnumerable().WithCancellation(ct))
        {
            count++;
            expectedSeq++;
            var hash = AuditChain.ComputeHash(prev, a.Sequence, a.Timestamp, a.ActorName, a.Action, a.TenantId, a.KeyId, a.Target, a.Details);
            if (a.Sequence != expectedSeq || a.PreviousHash != prev || a.Hash != hash) return (false, count, a.Sequence);
            prev = a.Hash;
        }
        return (true, count, null);
    }
}
