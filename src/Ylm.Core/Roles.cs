namespace Ylm.Core;

/// <summary>Platform roles as agreed in the planning (section 3).</summary>
public enum PlatformRole
{
    Auditor = 0,
    Helpdesk = 1,
    Operator = 2,
    Owner = 3,
}

[Flags]
public enum Permission : long
{
    None = 0,
    ReadInventory = 1 << 0,
    ReadReports = 1 << 1,
    ReadAudit = 1 << 2,
    LockKeys = 1 << 3,
    RegisterKeys = 1 << 4,
    ResetKeys = 1 << 5,
    ManageInventory = 1 << 6,
    ManageProfiles = 1 << 7,
    ManageTenants = 1 << 8,
    ManageUsers = 1 << 9,
    ManageSettings = 1 << 10,
    ManageApiKeys = 1 << 11,
    SyncTenants = 1 << 12,
}

public static class RolePermissions
{
    private const Permission Read = Permission.ReadInventory | Permission.ReadReports | Permission.ReadAudit;

    public static Permission For(PlatformRole role) => role switch
    {
        // Auditor: nur lesen, Reports.
        PlatformRole.Auditor => Read,
        // Helpdesk: Inventar lesen, Keys sperren, keine Neuregistrierung.
        PlatformRole.Helpdesk => Permission.ReadInventory | Permission.ReadReports | Permission.LockKeys,
        // Operator: Keys registrieren, sperren, zurücksetzen; Lager pflegen; synchronisieren.
        PlatformRole.Operator => Read | Permission.LockKeys | Permission.RegisterKeys | Permission.ResetKeys
                                 | Permission.ManageInventory | Permission.SyncTenants,
        // Owner: alles, inkl. Tenants, Profile, Rollen.
        PlatformRole.Owner => (Permission)~0L,
        _ => Permission.None,
    };

    public static bool Has(PlatformRole role, Permission permission) => (For(role) & permission) == permission;

    public static string DisplayName(PlatformRole role) => role switch
    {
        PlatformRole.Owner => "Owner",
        PlatformRole.Operator => "Operator",
        PlatformRole.Helpdesk => "Helpdesk",
        PlatformRole.Auditor => "Auditor",
        _ => role.ToString(),
    };
}
