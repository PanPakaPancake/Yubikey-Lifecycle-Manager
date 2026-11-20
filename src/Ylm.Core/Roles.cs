namespace Ylm.Core;

/// <summary>Platform roles as agreed in the planning (section 3).</summary>
public enum PlatformRole
{
    Auditor = 0,
    Helpdesk = 1,
    Operator = 2,
    Owner = 3,
    /// <summary>Selbst definierte Rolle aus einzelnen Berechtigungen (siehe CustomRole).</summary>
    Custom = 4,
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
    RetireKeys = 1 << 13,
    ManagePartnerStock = 1 << 14,
    /// <summary>Vier-Augen: Breakglass-Registrierungen anderer Admins freigeben.</summary>
    ApproveRegistrations = 1 << 15,
    /// <summary>Passkey-Rollout: befristete Zugriffspässe (TAP) für Benutzer erzeugen.</summary>
    IssueTemporaryAccessPass = 1 << 16,
    /// <summary>Passkey-Rollout: andere Anmeldemethoden (SMS, Authenticator, OTP …) von Benutzern entfernen.</summary>
    RemoveAuthMethods = 1 << 17,
    /// <summary>Geschützte Konten (Entra-Admins, geschützte Gruppen): TAP, Methoden entfernen und Registrierung erlaubt.</summary>
    ManageProtectedAccounts = 1 << 18,
}

/// <summary>Beschreibung einer Berechtigung für die Rollenverwaltung.</summary>
public sealed record PermissionInfo(Permission Permission, string Group, string Label, string Description, bool OwnerOnly);

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
                                 | Permission.ManageInventory | Permission.RetireKeys | Permission.ManagePartnerStock | Permission.SyncTenants
                                 | Permission.IssueTemporaryAccessPass | Permission.RemoveAuthMethods,
        // Owner: alles, inkl. Tenants, Profile, Rollen.
        PlatformRole.Owner => (Permission)~0L,
        _ => Permission.None,
    };

    public static bool Has(PlatformRole role, Permission permission) => (For(role) & permission) == permission;

    /// <summary>
    /// Nur Owner: Benutzer und Rollen, Plattform-Einstellungen und globale API-Schlüssel.
    /// Eigene Rollen können diese Rechte nicht enthalten, damit sich niemand selbst hochstufen kann.
    /// </summary>
    public const Permission OwnerOnly = Permission.ManageUsers | Permission.ManageSettings | Permission.ManageApiKeys;

    /// <summary>Alle Berechtigungen, die in eigenen Rollen vergeben werden können.</summary>
    public static Permission Assignable => Catalog.Where(p => !p.OwnerOnly).Aggregate(Permission.None, (a, p) => a | p.Permission);

    /// <summary>Wirksame Rechte einer eigenen Rolle (Owner-Rechte werden immer entfernt).</summary>
    public static Permission ForCustom(long permissions) => (Permission)permissions & Assignable;

    public static IReadOnlyList<Permission> Split(Permission permissions) =>
        Catalog.Select(p => p.Permission).Where(p => (permissions & p) == p).ToList();

    public static readonly IReadOnlyList<PermissionInfo> Catalog =
    [
        new(Permission.ReadInventory, "Lesen", "Inventar ansehen", "Keys, Registrierungen und Tenants der eigenen Zuständigkeit sehen.", false),
        new(Permission.ReadReports, "Lesen", "Berichte und Dashboard", "Dashboard, Berichte und Exporte.", false),
        new(Permission.ReadAudit, "Lesen", "Audit-Log ansehen", "Audit-Log lesen und prüfen.", false),
        new(Permission.RegisterKeys, "Keys", "Keys registrieren", "Wizard: Key für eine Person registrieren (on Behalf).", false),
        new(Permission.LockKeys, "Keys", "Keys sperren", "Key sperren, z. B. bei Verlust; entfernt den Passkey in Entra.", false),
        new(Permission.ResetKeys, "Keys", "Keys zurücksetzen", "Werksreset über den Agent, Key geht zurück ins Lager.", false),
        new(Permission.RetireKeys, "Keys", "Keys ausmustern", "Key endgültig ausmustern.", false),
        new(Permission.ManageInventory, "Lager", "Tenant-Lager pflegen", "Wareneingang, Import, Tags, Zusatzfelder und Notizen im Tenant.", false),
        new(Permission.ApproveRegistrations, "Keys", "Breakglass freigeben", "Vier-Augen-Freigabe: Breakglass-Registrierungen anderer Admins freigeben oder ablehnen.", false),
        new(Permission.IssueTemporaryAccessPass, "Anmeldemethoden", "TAP erzeugen", "Passkey-Rollout: befristeten Zugriffspass (TAP) für Benutzer erzeugen. Der Code wird nur einmal angezeigt.", false),
        new(Permission.RemoveAuthMethods, "Anmeldemethoden", "Methoden entfernen", "Passkey-Rollout: andere Anmeldemethoden wie SMS, Authenticator oder OTP von Benutzern entfernen.", false),
        new(Permission.ManageProtectedAccounts, "Anmeldemethoden", "Geschützte Konten bearbeiten", "TAP, Methoden entfernen und Key-Registrierung auch für geschützte Konten (Entra-Admins und geschützte Gruppen des Tenants).", false),
        new(Permission.ManagePartnerStock, "Lager", "Partnerlager verwalten", "Keys im Partnerlager sehen, ins Partnerlager geben und Tenants zuweisen.", false),
        new(Permission.SyncTenants, "Tenants", "Synchronisieren", "Entra-Sync und Tenant-Prüfung anstoßen.", false),
        new(Permission.ManageTenants, "Tenants", "Tenants verwalten", "Tenants anbinden, Einstellungen, „Jetzt beheben“, Offboarding.", false),
        new(Permission.ManageProfiles, "Tenants", "Profile verwalten", "Registrierungsprofile und Zusatzfelder.", false),
        new(Permission.ManageUsers, "Partner", "Benutzer und Rollen", "Benutzer anlegen, Rollen vergeben und eigene Rollen definieren.", true),
        new(Permission.ManageApiKeys, "Partner", "API und Integrationen", "Globale API-Schlüssel, Webhooks und Mail-Berichte.", true),
        new(Permission.ManageSettings, "Partner", "Plattform-Einstellungen", "Einstellungen der Installation.", true),
    ];

    public static string DisplayName(PlatformRole role) => role switch
    {
        PlatformRole.Owner => "Owner",
        PlatformRole.Operator => "Operator",
        PlatformRole.Helpdesk => "Helpdesk",
        PlatformRole.Auditor => "Auditor",
        _ => role.ToString(),
    };
}
