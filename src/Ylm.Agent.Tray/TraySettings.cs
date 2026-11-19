using Microsoft.Win32;

namespace Ylm.Agent.Tray;

/// <summary>Einstellungen pro Benutzer (HKCU\Software\YubikeyLifecycleManager\Tray).</summary>
internal static class TraySettings
{
    private const string KeyPath = @"Software\YubikeyLifecycleManager\Tray";

    /// <summary>"Mit Windows starten": Tray-Symbol bei der Anmeldung. Standard an.</summary>
    public static bool AutoStart
    {
        get => Read("AutoStart", 1) != 0;
        set => Write("AutoStart", value ? 1 : 0);
    }

    /// <summary>Hinweise (Sprechblasen) anzeigen, solange der Agent nicht mit dem Server verbunden ist. Standard an.</summary>
    public static bool Notifications
    {
        get => Read("Notifications", 1) != 0;
        set => Write("Notifications", value ? 1 : 0);
    }

    private static int Read(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(name) is int v ? v : fallback;
        }
        catch (Exception) { return fallback; }
    }

    private static void Write(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    /// <summary>Ist der angemeldete Benutzer lokaler Administrator (auch ohne erhöhte Rechte)? Dieselbe Prüfung macht der Dienst.</summary>
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)
                   || identity.Claims.Any(c => c.Type == System.Security.Claims.ClaimTypes.DenyOnlySid && c.Value == "S-1-5-32-544");
        }
        catch (Exception) { return false; }
    }
}
