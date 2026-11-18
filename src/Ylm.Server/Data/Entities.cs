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
    public string? BootstrapOwnerUpn { get; set; }
    public DateTimeOffset? SetupCompletedAt { get; set; }
    public bool IsSetupComplete => SetupCompletedAt is not null;
    /// <summary>Anmeldung mit Entra-Konten aus dem Heim-Tenant erlaubt (Standard). Lokale Konten gehen immer.</summary>
    public bool EntraSsoEnabled { get; set; } = true;
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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StatusChangedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<KeyRegistration> Registrations { get; set; } = [];
    public Tenant? Tenant { get; set; }
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

/// <summary>Read API key, managed on platform level. Valid for all tenants or only for the listed ones.</summary>
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
}
