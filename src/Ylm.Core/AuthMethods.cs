namespace Ylm.Core;

/// <summary>Wie stark eine Anmeldemethode schützt.</summary>
public enum MethodStrength
{
    /// <summary>Keine MFA (Kennwort, E-Mail für SSPR, Sicherheitsfragen, TAP).</summary>
    None = 0,
    /// <summary>Klassische MFA (Authenticator, SMS/Anruf, OTP).</summary>
    Mfa = 1,
    /// <summary>Phishing-resistent (Passkey, Windows Hello for Business, Platform Credential).</summary>
    PhishingResistant = 2,
}

/// <summary>Stand eines Benutzers im Passkey-Rollout, vom Ziel zurück zum Anfang.</summary>
public enum RolloutStage
{
    /// <summary>Keine MFA-Methode registriert.</summary>
    NoMfa = 0,
    /// <summary>Nur klassische MFA (z. B. Authenticator-Push, SMS).</summary>
    ClassicMfa = 1,
    /// <summary>Phishing-resistent, aber ohne Sicherheitsschlüssel (z. B. Windows Hello, Passkey im Authenticator).</summary>
    OtherPasskey = 2,
    /// <summary>Passkey auf einem Sicherheitsschlüssel (FIDO2), das Ziel des Rollouts.</summary>
    SecurityKey = 3,
}

public sealed record AuthMethodInfo(string Name, string Label, MethodStrength Strength, bool IsPasskey, bool IsSecurityKey);

/// <summary>
/// Methodennamen aus dem Entra-Bericht „userRegistrationDetails“ (methodsRegistered). Die direkte Abfrage pro Benutzer
/// (Fallback ohne Entra ID P1) wird auf dieselben Namen abgebildet.
/// </summary>
public static class AuthMethodCatalog
{
    private static readonly AuthMethodInfo[] All =
    [
        new("passKeyDeviceBound", "Passkey (Sicherheitsschlüssel)", MethodStrength.PhishingResistant, true, true),
        new("fido2", "Passkey (Sicherheitsschlüssel)", MethodStrength.PhishingResistant, true, true),
        new("passKeyDeviceBoundAuthenticator", "Passkey im Authenticator", MethodStrength.PhishingResistant, true, false),
        new("passKeyDeviceBoundWindowsHello", "Passkey in Windows Hello", MethodStrength.PhishingResistant, true, false),
        new("passKeySynced", "Synchronisierter Passkey", MethodStrength.PhishingResistant, true, false),
        new("windowsHelloForBusiness", "Windows Hello for Business", MethodStrength.PhishingResistant, false, false),
        new("macOsSecureEnclaveKey", "macOS Platform Credential", MethodStrength.PhishingResistant, false, false),
        new("microsoftAuthenticatorPasswordless", "Authenticator (kennwortlos)", MethodStrength.Mfa, false, false),
        new("microsoftAuthenticatorPush", "Authenticator-App", MethodStrength.Mfa, false, false),
        new("softwareOneTimePasscode", "Software-OTP", MethodStrength.Mfa, false, false),
        new("hardwareOneTimePasscode", "Hardware-OTP-Token", MethodStrength.Mfa, false, false),
        new("mobilePhone", "SMS / Anruf (Mobil)", MethodStrength.Mfa, false, false),
        new("alternateMobilePhone", "Anruf (Zweitnummer)", MethodStrength.Mfa, false, false),
        new("officePhone", "Anruf (Büro)", MethodStrength.Mfa, false, false),
        new("externalAuthMethod", "Externe MFA", MethodStrength.Mfa, false, false),
        new("temporaryAccessPass", "Befristeter Zugriffspass (TAP)", MethodStrength.None, false, false),
        new("email", "E-Mail (nur SSPR)", MethodStrength.None, false, false),
        new("securityQuestion", "Sicherheitsfragen", MethodStrength.None, false, false),
        new("password", "Kennwort", MethodStrength.None, false, false),
    ];

    private static readonly Dictionary<string, AuthMethodInfo> ByName = All.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AuthMethodInfo> Known => All;

    public static AuthMethodInfo Get(string name) =>
        ByName.TryGetValue(name, out var m) ? m : new AuthMethodInfo(name, name, MethodStrength.None, false, false);

    public static RolloutStage StageOf(IEnumerable<string> methods)
    {
        var infos = methods.Select(Get).ToList();
        if (infos.Any(m => m.IsSecurityKey)) return RolloutStage.SecurityKey;
        if (infos.Any(m => m.Strength == MethodStrength.PhishingResistant)) return RolloutStage.OtherPasskey;
        if (infos.Any(m => m.Strength == MethodStrength.Mfa)) return RolloutStage.ClassicMfa;
        return RolloutStage.NoMfa;
    }

    /// <summary>
    /// Bildet den Typ einer Methode aus /users/{id}/authentication/methods auf den Berichtsnamen ab.
    /// <paramref name="kind"/> ist der Typ ohne Präfix und Suffix (z. B. „phone“), <paramref name="phoneType"/> nur bei Telefon.
    /// </summary>
    public static string ReportNameFor(string kind, string? phoneType = null) => kind switch
    {
        "password" => "password",
        "phone" => phoneType switch { "alternateMobile" => "alternateMobilePhone", "office" => "officePhone", _ => "mobilePhone" },
        "microsoftAuthenticator" => "microsoftAuthenticatorPush",
        "fido2" => "passKeyDeviceBound",
        "softwareOath" => "softwareOneTimePasscode",
        "hardwareOath" => "hardwareOneTimePasscode",
        "email" => "email",
        "windowsHelloForBusiness" => "windowsHelloForBusiness",
        "temporaryAccessPass" => "temporaryAccessPass",
        "platformCredential" => "macOsSecureEnclaveKey",
        "externalAuthentication" => "externalAuthMethod",
        _ => kind,
    };

    /// <summary>Methodentypen, die auf der Rollout-Seite entfernt werden dürfen (Graph-Pfad pro Typ).</summary>
    public static readonly IReadOnlyDictionary<string, string> RemovablePaths = new Dictionary<string, string>
    {
        ["phone"] = "phoneMethods",
        ["microsoftAuthenticator"] = "microsoftAuthenticatorMethods",
        ["softwareOath"] = "softwareOathMethods",
        ["email"] = "emailMethods",
        ["windowsHelloForBusiness"] = "windowsHelloForBusinessMethods",
        ["temporaryAccessPass"] = "temporaryAccessPassMethods",
        ["platformCredential"] = "platformCredentialMethods",
        ["externalAuthentication"] = "externalAuthenticationMethods",
    };

    /// <summary>Kennwort lässt sich nicht entfernen; Passkeys auf Sicherheitsschlüsseln laufen über das Inventar (Key sperren).</summary>
    public static bool IsRemovable(string kind) => RemovablePaths.ContainsKey(kind);
}
