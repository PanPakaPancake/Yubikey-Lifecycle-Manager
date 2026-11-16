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
}

public sealed class PlatformUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? EntraObjectId { get; set; }
    public string Upn { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public PlatformRole Role { get; set; }
    /// <summary>When false the user only sees the tenants in <see cref="TenantAssignments"/>.</summary>
    public bool AllTenants { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<PlatformUserTenant> TenantAssignments { get; set; } = [];
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
    public string? TargetGroupId { get; set; }
    public string? TargetGroupName { get; set; }
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
    public Guid TenantId { get; set; }
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
    public Guid TenantId { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public CustomFieldType Type { get; set; }
    public bool Required { get; set; }
    public List<string> Options { get; set; } = [];
    public int SortOrder { get; set; }

    public CustomFieldSpec ToSpec() => new(Key, Label, Type, Required, Options);
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

public sealed class ApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? CreatedBy { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
