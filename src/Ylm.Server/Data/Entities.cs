using Ylm.Core;

namespace Ylm.Server.Data;

public sealed class InstallationSettings
{
    public int Id { get; set; } = 1;
    public string OrganizationName { get; set; } = "";
    public string? PublicBaseUrl { get; set; }
    public Guid? HomeTenantId { get; set; }
    public Guid? ClientId { get; set; }
    public string? CertificateThumbprint { get; set; }
    public DateTimeOffset? CertificateExpiresAt { get; set; }
    /// <summary>Objekt-ID der App-Registrierung im Heim-Tenant (für die automatische Zertifikatserneuerung).</summary>
    public string? ApplicationObjectId { get; set; }
    /// <summary>Letzter Versuch, das App-Zertifikat automatisch zu erneuern, und dessen Fehler.</summary>
    public DateTimeOffset? CertificateRenewalAttemptAt { get; set; }
    public string? CertificateRenewalError { get; set; }
    /// <summary>Wann zuletzt vor dem Ablauf gewarnt wurde (Audit-Log, Webhooks, Mail-Alarme).</summary>
    public DateTimeOffset? CertificateWarnedAt { get; set; }
    public string? BootstrapOwnerUpn { get; set; }
    public DateTimeOffset? SetupCompletedAt { get; set; }
    public bool IsSetupComplete => SetupCompletedAt is not null;
    /// <summary>Anmeldung mit Entra-Konten aus dem Heim-Tenant erlaubt (Standard). Lokale Konten gehen immer.</summary>
    public bool EntraSsoEnabled { get; set; } = true;
    /// <summary>Vorgabe für automatische Agent-Updates (Globale Einstellungen).</summary>
    public Ylm.Contracts.AgentUpdatePolicy AgentUpdatePolicy { get; set; }
}

/// <summary>Wie sich ein Plattform-Benutzer anmeldet.</summary>
public enum UserLoginKind
{
    /// <summary>Entra-Konto aus dem Heim-Tenant (SSO).</summary>
    Entra = 0,
    /// <summary>Lokales Konto, Anmeldung mit Passkey bzw. YubiKey direkt an der Plattform.</summary>
    Local = 1,
    /// <summary>Notfallkonto mit Passwort und TOTP, z. B. wenn Entra nicht erreichbar ist.</summary>
    Emergency = 2,
}

/// <summary>Passkey (WebAuthn) eines lokalen Benutzers für die Anmeldung an der Plattform.</summary>
public sealed class UserPasskey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlatformUserId { get; set; }
    public PlatformUser? PlatformUser { get; set; }
    public byte[] CredentialId { get; set; } = [];
    public byte[] PublicKey { get; set; } = [];
    public long SignCount { get; set; }
    public Guid Aaguid { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
}

public sealed class PlatformUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? EntraObjectId { get; set; }
    public string Upn { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public PlatformRole Role { get; set; }
    /// <summary>Eigene Rolle, wenn <see cref="Role"/> = Custom.</summary>
    public Guid? CustomRoleId { get; set; }
    public CustomRole? CustomRole { get; set; }
    /// <summary>When false the user only sees the tenants in <see cref="TenantAssignments"/>.</summary>
    public bool AllTenants { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<PlatformUserTenant> TenantAssignments { get; set; } = [];

    public UserLoginKind LoginKind { get; set; } = UserLoginKind.Entra;
    /// <summary>WebAuthn user handle (zufällig, nicht die Benutzer-ID).</summary>
    public byte[]? WebAuthnUserHandle { get; set; }
    public List<UserPasskey> Passkeys { get; set; } = [];
    /// <summary>Notfallkonto: PBKDF2-Hash des Passworts.</summary>
    public string? PasswordHash { get; set; }
    /// <summary>Notfallkonto: TOTP-Geheimnis, mit Data Protection verschlüsselt.</summary>
    public string? TotpSecretProtected { get; set; }
    /// <summary>Zuletzt verwendeter TOTP-Zeitschritt (verhindert Wiederverwendung eines Codes).</summary>
    public long? TotpLastStep { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>Einmal-Link zum Einrichten des Kontos (Passkey bzw. Passwort + TOTP), als SHA-256-Hash.</summary>
    public string? SetupTokenHash { get; set; }
    public DateTimeOffset? SetupTokenExpiresAt { get; set; }
}

/// <summary>Einmal-Notfallcode eines Owners (Anmeldung, wenn Entra oder Passkeys ausfallen). Nur der SHA-256-Hash wird gespeichert.</summary>
public sealed class RecoveryCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlatformUserId { get; set; }
    public PlatformUser? PlatformUser { get; set; }
    public string CodeHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Selbst definierte Rolle aus einzelnen Berechtigungen.</summary>
public sealed class CustomRole
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Bitmaske aus <see cref="Ylm.Core.Permission"/>.</summary>
    public long Permissions { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlatformUserTenant
{
    public Guid PlatformUserId { get; set; }
    public Guid TenantId { get; set; }
}

public enum TenantStatus { PendingConsent = 0, Active = 1, Offboarded = 2 }

public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EntraTenantId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? DefaultDomain { get; set; }
    public TenantStatus Status { get; set; } = TenantStatus.Active;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ConsentedAt { get; set; }
    public string? ConsentedBy { get; set; }
    /// <summary>Target audience: members of these groups should get a YubiKey. Empty = all users.</summary>
    public List<GroupRef> TargetIncludeGroups { get; set; } = [];
    /// <summary>Members of these groups are not part of the target audience (e.g. service accounts).</summary>
    public List<GroupRef> TargetExcludeGroups { get; set; } = [];
    public int SyncIntervalMinutes { get; set; } = 60;
    public DateTimeOffset? LastSyncAt { get; set; }
    public bool? LastSyncSucceeded { get; set; }
    public string? LastSyncMessage { get; set; }
    public int? TargetUserCount { get; set; }
    public DateTimeOffset? LastCheckAt { get; set; }
    public string? LastChecksJson { get; set; }
    /// <summary>AAGUID allow list state from the last check, used to block incompatible registrations early.</summary>
    public string? PolicySnapshotJson { get; set; }
    public DateTimeOffset? OffboardedAt { get; set; }
    public DateTimeOffset? DataDeletionDueAt { get; set; }
    /// <summary>Vier-Augen-Freigabe für Breakglass-Registrierungen (planning 6.3, standardmäßig aus).</summary>
    public bool RequireBreakglassApproval { get; set; }
    /// <summary>
    /// Vier-Augen beim Entfernen: Sperren, Zurücksetzen und Konto entfernen bei aktiven Breakglass-Keys nur mit Freigabe eines
    /// zweiten Admins. Standardmäßig an, damit niemand allein einen Notfallzugang löschen kann.
    /// </summary>
    public bool RequireBreakglassRemovalApproval { get; set; } = true;
    /// <summary>Konten mit Entra-Admin-Rolle schützen (TAP, Methoden entfernen, Registrierung nur mit „Geschützte Konten bearbeiten“).</summary>
    public bool ProtectAdmins { get; set; } = true;
    /// <summary>Mitglieder dieser Gruppen sind ebenfalls geschützt (z. B. Breakglass, Geschäftsführung).</summary>
    public List<GroupRef> ProtectedGroups { get; set; } = [];

    // ---- Lebenszyklus-Erinnerungen (optional, standardmäßig alle aus) ----
    /// <summary>Keys melden, die laut Entra länger als <see cref="UnusedKeyDays"/> Tage nicht benutzt wurden.</summary>
    public bool RemindUnusedKeys { get; set; }
    public int UnusedKeyDays { get; set; } = 180;
    /// <summary>Personen melden, die nur einen einzigen persönlichen Key haben (kein Ersatz-Key).</summary>
    public bool RemindNoSpareKey { get; set; }
    /// <summary>Aktive Keys melden, deren Konto in Entra deaktiviert oder gelöscht ist (ausgeschiedene Personen).</summary>
    public bool RemindLeavers { get; set; }
    /// <summary>Gesperrte oder ausgemusterte Keys melden, deren Passkey in Entra noch angemeldet ist.</summary>
    public bool RemindRetiredWithPasskey { get; set; }
    /// <summary>Für Personen ohne Ersatz-Key automatisch ein ToDo anlegen.</summary>
    public bool ReminderCreateTodos { get; set; }

    public bool AnyReminderEnabled => RemindUnusedKeys || RemindNoSpareKey || RemindLeavers || RemindRetiredWithPasskey;
}

public sealed class YubiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Null = Partnerlager (keinem Kunden-Tenant zugeordnet).</summary>
    public Guid? TenantId { get; set; }
    public int? SerialNumber { get; set; }
    public string? FirmwareVersion { get; set; }
    public string? Model { get; set; }
    public string? FormFactor { get; set; }
    public Guid? AaGuid { get; set; }
    public bool IsFips { get; set; }
    public KeyStatus Status { get; set; } = KeyStatus.InStock;
    public LockReason? LockReason { get; set; }
    public string? StatusNote { get; set; }
    public KeySource Source { get; set; }
    public List<string> Tags { get; set; } = [];
    public Dictionary<string, string?> CustomFields { get; set; } = [];
    public string? Notes { get; set; }
    public Guid? ProfileId { get; set; }
    public bool? PinChangeForced { get; set; }
    /// <summary>Regelfall ein Key pro Person; geteilte Keys und Breakglass-Keys tragen mehrere Konten (V1.1).</summary>
    public KeyUsage Usage { get; set; } = KeyUsage.Personal;
    /// <summary>Verwahrer eines geteilten Keys bzw. Breakglass-Keys, z. B. Abteilungsleiter. Pflicht bei Mehrfachnutzung.</summary>
    public string? Custodian { get; set; }
    /// <summary>Zeitpunkt des Nacherfassens (Seriennummer und Firmware eines selbst registrierten Keys per Agent ausgelesen).</summary>
    public DateTimeOffset? IdentifiedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StatusChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<KeyRegistration> Registrations { get; set; } = [];
    public Tenant? Tenant { get; set; }
}

/// <summary>Wie ein Key genutzt wird (planning 5.3 „Ein Key, mehrere Registrierungen“).</summary>
public enum KeyUsage
{
    /// <summary>Ein Key, eine Person (Regelfall).</summary>
    Personal = 0,
    /// <summary>Geteilter Key, z. B. für mehrere Produktionsmitarbeiter, beim Verwahrer.</summary>
    Shared = 1,
    /// <summary>Notfallzugang (Breakglass-Konten); optional mit Vier-Augen-Freigabe pro Tenant.</summary>
    Breakglass = 2,
}

public sealed class KeyRegistration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid KeyId { get; set; }
    public string EntraUserId { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string? UserDisplayName { get; set; }
    public string EntraMethodId { get; set; } = "";
    public string? DisplayName { get; set; }
    public Guid? AaGuid { get; set; }
    public DateTimeOffset RegisteredAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? RegisteredById { get; set; }
    public string? RegisteredByName { get; set; }
    public Guid? ProfileId { get; set; }
    public KeySource Source { get; set; }
    public DateTimeOffset? LastSeenInSyncAt { get; set; }
    /// <summary>Letzte Anmeldung mit diesem Passkey laut Entra (lastUsedDateTime); null = Entra liefert keinen Wert.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
    /// <summary>Konto in Entra aktiviert? Nur erfasst, wenn die Erinnerung „Ausgeschiedene Personen“ an ist; null = unbekannt.</summary>
    public bool? UserAccountEnabled { get; set; }
    public DateTimeOffset? RemovedAt { get; set; }
    public string? RemovedReason { get; set; }
    public YubiKey? Key { get; set; }
}

public sealed class RegistrationProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Null = applies to all tenants of the organization.</summary>
    public Guid? TenantId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public PinStrategy PinStrategy { get; set; } = PinStrategy.Random;
    public int PinLength { get; set; } = 8;
    /// <summary>Encrypted with ASP.NET Data Protection. Never returned to clients.</summary>
    public string? FixedPinProtected { get; set; }
    public bool ForcePinChange { get; set; } = true;
    public bool FactoryResetBeforeRegistration { get; set; }
    public string DisplayNameTemplate { get; set; } = "YubiKey {Seriennummer}";
    public List<string> AllowedModels { get; set; } = [];
    public string? MinimumFirmware { get; set; }
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ProfileSettings ToSettings() => new()
    {
        PinStrategy = PinStrategy,
        PinLength = PinLength,
        ForcePinChange = ForcePinChange,
        FactoryResetBeforeRegistration = FactoryResetBeforeRegistration,
        DisplayNameTemplate = DisplayNameTemplate,
        AllowedModels = AllowedModels,
        MinimumFirmware = MinimumFirmware,
    };
}

public sealed class CustomFieldDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Null = global field on platform level, inherited by every tenant.</summary>
    public Guid? TenantId { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public CustomFieldType Type { get; set; }
    public bool Required { get; set; }
    public List<string> Options { get; set; } = [];
    public int SortOrder { get; set; }

    public CustomFieldSpec ToSpec() => new(Key, Label, Type, Required, Options);

    public CustomFieldDef ToDef() => new(Id, ToSpec(), SortOrder);
}

/// <summary>A tenant's change to an inherited global field: disabled, or label/required/options changed.</summary>
public sealed class CustomFieldOverride
{
    public Guid TenantId { get; set; }
    public Guid FieldId { get; set; }
    public bool Disabled { get; set; }
    public string? Label { get; set; }
    public bool? Required { get; set; }
    public List<string>? Options { get; set; }

    public CustomFieldOverrideSpec ToSpec() => new(Disabled, Label, Required, Options);
}

/// <summary>Group level: members of an Entra group get a specific registration profile suggested.</summary>
public sealed class GroupProfileAssignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public Guid ProfileId { get; set; }
    /// <summary>Lower wins when a person is member of several assigned groups.</summary>
    public int Priority { get; set; }
}

public enum ActorType { User = 0, Agent = 1, System = 2, Api = 3 }

public sealed class AuditEntry
{
    public long Id { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public ActorType ActorType { get; set; }
    public Guid? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string Action { get; set; } = "";
    public Guid? TenantId { get; set; }
    public Guid? KeyId { get; set; }
    public string? Target { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public string PreviousHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PlatformUserId { get; set; }
    public string MachineName { get; set; } = "";
    public string? AgentVersion { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Wirksame Auto-Update-Einstellung, wie der Agent sie meldet (null: Agent vor 0.5).</summary>
    public bool? AutoUpdate { get; set; }
    /// <summary>Fehler des letzten Updateversuchs, null wenn in Ordnung.</summary>
    public string? UpdateError { get; set; }
    public PlatformUser? PlatformUser { get; set; }
}

public sealed class PairingCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string CodeHash { get; set; } = "";
    public Guid PlatformUserId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>API key, managed on platform level. Valid for all tenants or only for the listed ones, with its own rights (ApiScopes).</summary>
public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool AllTenants { get; set; }
    public List<Guid> TenantIds { get; set; } = [];
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Rechte des Schlüssels, z. B. inventory.read oder todos.write (siehe ApiScopes). Gelten für alle Tenants des Schlüssels.</summary>
    public List<string> Scopes { get; set; } = [];
}

public enum BatchStatus { Open = 0, Completed = 1, Cancelled = 2 }

public enum BatchItemStatus { Pending = 0, Running = 1, Done = 2, Failed = 3, Skipped = 4 }

/// <summary>Stapelregistrierung (V1.1): a list of people who get a key one after the other at one agent.</summary>
public sealed class RegistrationBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Where the list came from, e.g. "Gruppe: Vertrieb", "CSV", "Auswahl".</summary>
    public string? Source { get; set; }
    /// <summary>Profile for people without a group profile and without an explicit profile.</summary>
    public Guid DefaultProfileId { get; set; }
    public BatchStatus Status { get; set; } = BatchStatus.Open;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedById { get; set; }
    public string? CreatedByName { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public List<RegistrationBatchItem> Items { get; set; } = [];
}

public sealed class RegistrationBatchItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public int Position { get; set; }
    public string EntraUserId { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string? UserDisplayName { get; set; }
    public string? Department { get; set; }
    /// <summary>Explicit profile for this person; null = batch default.</summary>
    public Guid? ProfileId { get; set; }
    /// <summary>Name of the Entra group the profile was suggested from (shown in the list).</summary>
    public string? ProfileGroupName { get; set; }
    /// <summary>Optional fixed key (from CSV). Only this key is registered for the person.</summary>
    public int? ExpectedSerialNumber { get; set; }
    public BatchItemStatus Status { get; set; } = BatchItemStatus.Pending;
    public Guid? JobId { get; set; }
    public Guid? KeyId { get; set; }
    public int? SerialNumber { get; set; }
    public bool? PinChangeForced { get; set; }
    /// <summary>Reference to the PIN letter held in memory only (PinLetterStore); the PIN itself is never stored.</summary>
    public Guid? PinLetterId { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedByName { get; set; }
}

public enum ApprovalStatus { Pending = 0, Approved = 1, Rejected = 2, Used = 3, Cancelled = 4 }

/// <summary>Wofür eine Vier-Augen-Freigabe gilt.</summary>
public enum ApprovalKind
{
    /// <summary>Breakglass-Key registrieren.</summary>
    Registration = 0,
    /// <summary>Aktiven Breakglass-Key sperren, zurücksetzen oder ein Konto davon entfernen.</summary>
    KeyRemoval = 1,
}

/// <summary>Entfernende Aktion an einem Breakglass-Key, die eine Freigabe braucht.</summary>
public enum RemovalAction
{
    Lock = 0,
    Reset = 1,
    RemoveAccount = 2,
}

/// <summary>Vier-Augen-Freigabe (V2): ein zweiter Admin gibt eine Breakglass-Registrierung frei, bevor sie laufen darf.</summary>
public sealed class RegistrationApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public KeyUsage Usage { get; set; } = KeyUsage.Breakglass;
    public ApprovalKind Kind { get; set; } = ApprovalKind.Registration;
    /// <summary>Nur bei <see cref="ApprovalKind.KeyRemoval"/>: welche Aktion freigegeben wird.</summary>
    public RemovalAction? RemovalAction { get; set; }
    /// <summary>Nur bei <see cref="Data.RemovalAction.RemoveAccount"/>: die Registrierung, die entfernt werden darf.</summary>
    public Guid? RegistrationId { get; set; }
    /// <summary>Registrierung: die betroffenen Konten (Entra-Objekt-IDs). Entfernen: die Konten, die beim Antrag auf dem Key waren.</summary>
    public List<string> EntraUserIds { get; set; } = [];
    public List<string> UserPrincipalNames { get; set; } = [];
    public string? Custodian { get; set; }
    public string Reason { get; set; } = "";
    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public Guid RequestedById { get; set; }
    public string RequestedByName { get; set; } = "";
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? DecidedById { get; set; }
    public string? DecidedByName { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }
    /// <summary>Eine Freigabe gilt begrenzt (Standard 24 Stunden) und nur für einen Lauf.</summary>
    public DateTimeOffset? ValidUntil { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    /// <summary>Registrierung: der Key, auf den registriert wurde (nach Verwendung). Entfernen: der betroffene Key (ab Antrag).</summary>
    public Guid? KeyId { get; set; }
}

public enum TodoStatus { Open = 0, Done = 1, Cancelled = 2 }

/// <summary>ToDo-Liste (V2): Ticketsystem oder Admin meldet eine Person, die einen Key bekommen soll.</summary>
public sealed class RegistrationTodo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string EntraUserId { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string? UserDisplayName { get; set; }
    public string? Department { get; set; }
    public string? TicketId { get; set; }
    public string? TicketUrl { get; set; }
    public string? Note { get; set; }
    public Guid? ProfileId { get; set; }
    public DateOnly? DueDate { get; set; }
    /// <summary>Herkunft, z. B. „API: Ticketsystem“ oder „Manuell“.</summary>
    public string Source { get; set; } = "";
    public Guid? ApiKeyId { get; set; }
    public TodoStatus Status { get; set; } = TodoStatus.Open;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedByName { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? CompletedByName { get; set; }
    public Guid? KeyId { get; set; }
    public string? CancelReason { get; set; }
}

/// <summary>Webhook (V2): Ereignisse aus dem Audit-Log werden signiert an eine URL geschickt.</summary>
public enum WebhookChannel { Webhook = 0, Mail = 1 }

/// <summary>Empfänger von Ereignissen: ein Webhook (HTTP POST) oder ein Mail-Alarm (über den SMTP-Server der Mail-Berichte).</summary>
public sealed class WebhookSubscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public WebhookChannel Channel { get; set; } = WebhookChannel.Webhook;
    /// <summary>Nur bei Channel = Webhook.</summary>
    public string Url { get; set; } = "";
    /// <summary>Nur bei Channel = Mail.</summary>
    public List<string> Recipients { get; set; } = [];
    /// <summary>Geheimnis für die HMAC-Signatur, mit Data Protection verschlüsselt.</summary>
    public string SecretProtected { get; set; } = "";
    /// <summary>Ereignistypen; leer = alle.</summary>
    public List<string> Events { get; set; } = [];
    public bool AllTenants { get; set; } = true;
    public List<Guid> TenantIds { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public bool? LastDeliverySucceeded { get; set; }
    public string? LastError { get; set; }
}

public enum DeliveryStatus { Pending = 0, Delivered = 1, Failed = 2 }

public sealed class WebhookDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubscriptionId { get; set; }
    public long? AuditSequence { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
}

/// <summary>Fortschritt der Webhook-Auswertung im Audit-Log, damit kein Ereignis verloren geht oder doppelt geht.</summary>
public sealed class IntegrationState
{
    public int Id { get; set; } = 1;
    public long WebhookAuditCursor { get; set; }
}

public enum SmtpSecurity { None = 0, StartTls = 1, SslOnConnect = 2 }

/// <summary>Mailserver für Mail-Berichte (V2).</summary>
public sealed class SmtpSettings
{
    public int Id { get; set; } = 1;
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public string? Username { get; set; }
    public string? PasswordProtected { get; set; }
    public string? FromAddress { get; set; }
    public string? FromName { get; set; }
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public enum ReportFrequency { Daily = 0, Weekly = 1, Monthly = 2 }

/// <summary>Geplanter Mail-Bericht (V2): Dashboard als PDF an feste Empfänger.</summary>
public sealed class ReportSchedule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<string> Recipients { get; set; } = [];
    public bool AllTenants { get; set; } = true;
    public List<Guid> TenantIds { get; set; } = [];
    public ReportFrequency Frequency { get; set; } = ReportFrequency.Weekly;
    /// <summary>0 = Sonntag … 6 = Samstag (wöchentlich).</summary>
    public int DayOfWeek { get; set; } = 1;
    /// <summary>1 … 28 (monatlich).</summary>
    public int DayOfMonth { get; set; } = 1;
    /// <summary>Uhrzeit in der Zeitzone <see cref="TimeZone"/>.</summary>
    public int Hour { get; set; } = 7;
    public string TimeZone { get; set; } = "Europe/Berlin";
    public bool IncludeCsv { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }
    public bool? LastSucceeded { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Erscheinungsbild der erzeugten Dokumente (PIN-Briefe, Berichte, Exporte).
/// TenantId null = Vorgabe des Partners für alle Kunden; ein Eintrag pro Tenant überschreibt einzelne Felder (null = vom Partner übernehmen).
/// </summary>
public sealed class Branding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string? CompanyName { get; set; }
    /// <summary>Akzentfarbe als #rrggbb.</summary>
    public string? AccentColor { get; set; }
    /// <summary>Logo als PNG oder JPEG.</summary>
    public byte[]? Logo { get; set; }
    public string? LogoContentType { get; set; }
    /// <summary>Kontakt für Rückfragen, z. B. „IT-Service · 0123 456 · it@firma.de“.</summary>
    public string? Contact { get; set; }
    public string? Footer { get; set; }
    /// <summary>Eigene Texte des PIN-Briefs als JSON (Schlüssel aus <see cref="Pdf.LetterTemplate.Fields"/>); fehlende übernehmen die Vorgabe.</summary>
    public string? LetterTexts { get; set; }
    /// <summary>Aufbau des PIN-Briefs aus dem Brief-Editor als JSON (<see cref="Pdf.LetterLayout"/>); leer heißt: übernehmen (Tenant → Partner → Standard).</summary>
    public string? LetterLayout { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Art einer Lebenszyklus-Erinnerung.</summary>
public enum ReminderKind
{
    /// <summary>Key laut Entra lange nicht benutzt.</summary>
    UnusedKey = 0,
    /// <summary>Person hat nur einen persönlichen Key.</summary>
    NoSpareKey = 1,
    /// <summary>Konto deaktiviert oder gelöscht, Key aber noch aktiv.</summary>
    Leaver = 2,
    /// <summary>Key gesperrt oder ausgemustert, Passkey in Entra aber noch vorhanden.</summary>
    RetiredWithPasskey = 3,
}

/// <summary>
/// Merkt sich je Erinnerung, seit wann sie besteht und ob ein Admin sie ausgeblendet hat.
/// So wird jede Erinnerung nur einmal gemeldet (Audit-Log, Webhooks, Mail-Alarme).
/// </summary>
public sealed class ReminderState
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public ReminderKind Kind { get; set; }
    /// <summary>Worum es geht: Key-ID, Entra-Objekt-ID der Person oder beides („key:user“).</summary>
    public string Subject { get; set; } = "";
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DismissedAt { get; set; }
    public string? DismissedByName { get; set; }
    public string? DismissNote { get; set; }
    public Guid? TodoId { get; set; }
}
