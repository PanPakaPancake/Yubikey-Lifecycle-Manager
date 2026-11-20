namespace Ylm.Agent.Tray;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Der Autostart-Eintrag (HKLM\...\Run) gilt für alle Benutzer; wer "Mit Windows starten" abgewählt hat, bekommt kein Tray-Symbol.
        if (args.Contains("--autostart") && !TraySettings.AutoStart) return;
        ApplicationConfiguration.Initialize();
        // Vorschaubilder für Pull Requests und Doku (läuft in der CI auf dem Windows-Runner).
        if (args.Length >= 2 && args[0] == "--screenshots")
        {
            Screenshots.Run(args[1]);
            return;
        }
        using var mutex = new Mutex(true, @"Local\YubikeyLifecycleManager.Tray", out var first);
        if (!first) return; // one tray per user session
        Theme.Load();
        Application.Run(new TrayContext(justUpdated: args.Contains("--updated")));
    }
}
