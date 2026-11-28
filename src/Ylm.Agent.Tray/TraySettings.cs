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

    /// <summary>Darstellung wie in der Weboberfläche: dunkel (Vorgabe), hell oder wie Windows.</summary>
    public static ThemeMode Theme
    {
        get => Read("Theme", 0) switch { 1 => ThemeMode.Light, 2 => ThemeMode.System, _ => ThemeMode.Dark };
        set => Write("Theme", (int)value);
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
}
