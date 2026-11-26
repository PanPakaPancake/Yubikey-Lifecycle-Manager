using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Ylm.Agent.Core;

namespace Ylm.Agent;

/// <summary>
/// Schützt das Agent-Token mit DPAPI (Maschinenbereich). Auf manchen PCs lehnt DPAPI das ab
/// (CryptographicException 0x80071770, ERROR_ENCRYPTION_FAILED; beobachtet unter Windows 11 mit Smart App Control).
/// Dann bleibt das Token unverschlüsselt in agent.json. Die Datei ist ohnehin nur für SYSTEM und Administratoren lesbar
/// (<see cref="RestrictToSystemAndAdmins"/>); das schützt mehr als DPAPI im Maschinenbereich allein,
/// das jeder Prozess auf dem PC entschlüsseln kann.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector(ILogger<DpapiSecretProtector> logger) : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Ylm.Agent.Token.v1");
    internal const string PlainPrefix = "plain:";

    public string Protect(string value)
    {
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (CryptographicException ex)
        {
            logger.LogWarning("DPAPI nicht verfügbar (0x{HResult:X8}: {Message}). Token wird ohne DPAPI gespeichert, " +
                              "die Datei ist nur für SYSTEM und Administratoren lesbar.", ex.HResult, ex.Message);
            return PlainPrefix + value;
        }
    }

    public string Unprotect(string value) => value.StartsWith(PlainPrefix, StringComparison.Ordinal)
        ? value[PlainPrefix.Length..]
        : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.LocalMachine));

    /// <summary>Nur SYSTEM und Administratoren dürfen die Datei lesen und schreiben (keine geerbten Rechte von ProgramData).</summary>
    public static void RestrictToSystemAndAdmins(string file)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
}
