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

    /// <summary>Täglich auf GitHub nach einer neuen Version suchen.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>GitHub-Repository mit den Releases (owner/name).</summary>
    public string UpdateRepository { get; set; } = "PanPakaPancake/Yubikey-Lifecycle-Manager";

    /// <summary>GitHub-API (für GitHub Enterprise oder Tests änderbar).</summary>
    public string UpdateApiBaseUrl { get; set; } = "https://api.github.com";

    /// <summary>Ordner mit dem mitgelieferten Agent-MSI und version.txt (Standard: &lt;App&gt;/agent).</summary>
    public string? AgentPackageDirectory { get; set; }

    /// <summary>Adresse des Updater-Containers (z. B. http://updater:8090). Leer = kein Update per Klick.</summary>
    public string? UpdaterUrl { get; set; }

    /// <summary>Gemeinsames Geheimnis mit dem Updater-Container.</summary>
    public string? UpdaterToken { get; set; }

    /// <summary>
    /// Absender, deren X-Forwarded-For/-Proto/-Host-Header gelten (IP-Adressen oder Netze in CIDR-Schreibweise).
    /// Leer = Loopback und private Netze (Docker, Caddy, Reverse Proxy im internen Netz). "*" = allen vertrauen (alte Voreinstellung).
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    /// <summary>Run background jobs (sync, retention). Disabled in tests.</summary>
    public bool EnableBackgroundJobs { get; set; } = true;
}
