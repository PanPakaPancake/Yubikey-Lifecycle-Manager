using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ylm.Agent;

/// <summary>
/// Startet die Tray-App aus dem Dienst (LocalSystem, Sitzung 0) in der Sitzung eines angemeldeten Benutzers,
/// mit dessen Token und Umgebung. Nötig, damit die Tray-App nach einem Update wieder da ist.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayLauncher
{
    public static void StartInSession(uint sessionId, string exe, string arguments)
    {
        if (!WTSQueryUserToken(sessionId, out var userToken)) throw new Win32Exception(Marshal.GetLastWin32Error(), "WTSQueryUserToken");
        var primary = IntPtr.Zero;
        var environment = IntPtr.Zero;
        try
        {
            if (!DuplicateTokenEx(userToken, TokenAllAccess, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx");
            if (!CreateEnvironmentBlock(out environment, primary, false)) environment = IntPtr.Zero;
            var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>(), lpDesktop = @"winsta0\default" };
            // CreateProcessW darf die Kommandozeile verändern, daher ein beschreibbarer Puffer.
            var commandLine = new System.Text.StringBuilder($"\"{exe}\" {arguments}".TrimEnd());
            if (!CreateProcessAsUser(primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment | CreateNoWindow, environment, Path.GetDirectoryName(exe), ref si, out var pi))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser");
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
        finally
        {
            if (environment != IntPtr.Zero) DestroyEnvironmentBlock(environment);
            if (primary != IntPtr.Zero) CloseHandle(primary);
            CloseHandle(userToken);
        }
    }

    private const uint TokenAllAccess = 0xF01FF;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string? applicationName, System.Text.StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
