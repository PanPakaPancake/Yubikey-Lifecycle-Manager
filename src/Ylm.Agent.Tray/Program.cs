namespace Ylm.Agent.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, @"Local\YubikeyLifecycleManager.Tray", out var first);
        if (!first) return; // one tray per user session
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
