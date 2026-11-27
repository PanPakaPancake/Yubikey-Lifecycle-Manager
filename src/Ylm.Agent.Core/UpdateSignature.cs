using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace Ylm.Agent.Core;

/// <summary>Authenticode-Signatur einer Datei: gültig (Windows-Vertrauenskette) und Name des Unterzeichners.</summary>
public sealed record SignatureInfo(bool Valid, string? Subject)
{
    public static readonly SignatureInfo None = new(false, null);
}

/// <summary>
/// Regel für Agent-Updates: Ist der laufende Agent signiert, muss das Update gültig und vom selben Unterzeichner
/// signiert sein (der Name im Zertifikat zählt, nicht das Zertifikat selbst, weil es regelmäßig erneuert wird). Ein unsignierter Agent
/// nimmt unsignierte Updates weiter an, damit der Umstieg klappt, außer Agent:RequireSignedUpdates ist gesetzt.
/// Agent:TrustedSigner erlaubt zusätzlich einen bestimmten Unterzeichner (z. B. beim Wechsel des Zertifikats).
/// </summary>
public static class UpdateSignaturePolicy
{
    /// <returns>null, wenn das Update installiert werden darf, sonst der Grund für die Ablehnung.</returns>
    public static string? Check(SignatureInfo self, SignatureInfo update, bool requireSigned, string? trustedSigner)
    {
        var mustBeSigned = requireSigned || self.Valid || !string.IsNullOrWhiteSpace(trustedSigner);
        if (!mustBeSigned) return null;
        if (!update.Valid || string.IsNullOrEmpty(update.Subject))
            return "Das Update ist nicht gültig signiert. Abgebrochen.";
        var allowed = new[] { self.Valid ? self.Subject : null, trustedSigner }
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(Normalize).ToList();
        if (allowed.Count == 0 || allowed.Contains(Normalize(update.Subject)))
            return null;
        return $"Das Update ist von einem anderen Unterzeichner signiert ({update.Subject}). Abgebrochen.";
    }

    /// <summary>UpgradeCode des Agent-MSI (installer/Package.wxs). Muss bei jeder Version gleich bleiben.</summary>
    public const string AgentUpgradeCode = "6B0B4E2C-6F7A-4E0F-9C1C-3D1F4C7A2E51";

    /// <summary>
    /// Zweite Prüfung für signierte Updates: Das MSI muss der Agent-Installer sein. Nötig, weil SignPath Foundation
    /// viele Open-Source-Projekte mit demselben Zertifikatsnamen signiert. Der gleiche Unterzeichner allein beweist
    /// also nicht, dass es unser Paket ist.
    /// </summary>
    /// <returns>null, wenn der UpgradeCode passt, sonst der Grund für die Ablehnung.</returns>
    public static string? CheckProduct(string? upgradeCode) =>
        Guid.TryParse(upgradeCode, out var code) && code == Guid.Parse(AgentUpgradeCode)
            ? null
            : $"Das Update ist kein Paket des Agents (UpgradeCode {upgradeCode ?? "fehlt"}). Abgebrochen.";

    /// <summary>Distinguished Name ohne Unterschiede bei Leerzeichen und Groß-/Kleinschreibung.</summary>
    public static string Normalize(string? dn) =>
        string.Join(",", (dn ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}

/// <summary>Liest eine Eigenschaft aus der Property-Tabelle eines MSI (nur lesend, ohne Installation).</summary>
[SupportedOSPlatform("windows")]
public static class MsiPackage
{
    public static string? ReadProperty(string path, string property)
    {
        if (MsiOpenDatabaseW(path, IntPtr.Zero, out var db) != 0) return null;
        try
        {
            if (MsiDatabaseOpenViewW(db, $"SELECT `Value` FROM `Property` WHERE `Property` = '{property.Replace("'", "")}'", out var view) != 0) return null;
            try
            {
                if (MsiViewExecute(view, IntPtr.Zero) != 0) return null;
                if (MsiViewFetch(view, out var record) != 0) return null;
                try
                {
                    var size = 0u;
                    var empty = new char[1];
                    var rc = MsiRecordGetStringW(record, 1, empty, ref size);
                    if (rc != 0 && rc != 234) return null; // 234 = ERROR_MORE_DATA
                    var buffer = new char[size + 1];
                    size++;
                    if (MsiRecordGetStringW(record, 1, buffer, ref size) != 0) return null;
                    return new string(buffer, 0, (int)size);
                }
                finally { MsiCloseHandle(record); }
            }
            finally { MsiCloseHandle(view); }
        }
        finally { MsiCloseHandle(db); }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr handle);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewFetch(IntPtr view, out IntPtr record);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordGetStringW(IntPtr record, uint field, [Out] char[] value, ref uint size);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(IntPtr handle);
}

/// <summary>Prüft Authenticode-Signaturen mit WinVerifyTrust (ohne Sperrlistenabruf, damit es auch offline klappt).</summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    public static SignatureInfo Inspect(string path)
    {
        if (!File.Exists(path)) return SignatureInfo.None;
        var valid = Verify(path);
        string? subject = null;
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            subject = cert.Subject;
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or COMException)
        {
            // unsigniert
        }
        return new SignatureInfo(valid && subject is not null, subject);
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static bool Verify(string path)
    {
        var pathPtr = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = pathPtr };
        var filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,              // WTD_UI_NONE
                fdwRevocationChecks = 0,     // WTD_REVOKE_NONE
                dwUnionChoice = 1,           // WTD_CHOICE_FILE
                pFile = filePtr,
                dwStateAction = 0,           // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x00001000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);
}
