namespace Ylm.Server.Infrastructure;

public sealed class YlmOptions
{
    public const string Section = "Ylm";

    /// <summary>Public base URL (https://ylm.example.com). Used for redirect URIs. Falls back to the setup value.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>"Entra" (default) or "Development" (local fake login, only in the Development environment).</summary>
    public string AuthMode { get; set; } = "Entra";

    /// <summary>Require a phishing-resistant sign-in (amr claim) for platform admins.</summary>
    public bool RequirePhishingResistantSignIn { get; set; } = true;

    /// <summary>amr values accepted as phishing resistant. Entra sends "fido" for FIDO2/passkeys.</summary>
    public string[] PhishingResistantAmrValues { get; set; } = ["fido"];

    /// <summary>Directory for secrets (certificate, keys). Mount as a volume in Docker.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>Default Entra sync interval in minutes for new tenants.</summary>
    public int DefaultSyncIntervalMinutes { get; set; } = 60;

    /// <summary>Days tenant data is kept after offboarding before it is deleted.</summary>
    public int OffboardingRetentionDays { get; set; } = 30;

    /// <summary>Extra YubiKey AAGUIDs to treat as YubiKeys in tenant checks.</summary>
    public string[] AdditionalYubiKeyAaguids { get; set; } = [];

    /// <summary>Graph base URL (overridable for tests).</summary>
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com";

    /// <summary>Login authority host (overridable for sovereign clouds).</summary>
    public string LoginAuthority { get; set; } = "https://login.microsoftonline.com";

    /// <summary>Run background jobs (sync, retention). Disabled in tests.</summary>
    public bool EnableBackgroundJobs { get; set; } = true;
}
