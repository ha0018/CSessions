using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace WindowsChildSession;

internal static class WindowsSessions
{
    public static uint CurrentId => ForProcess((uint)Environment.ProcessId);

    public static string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User!.Value;
        }
    }

    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static uint ForProcess(uint processId)
    {
        if (!ProcessIdToSessionId(processId, out var id))
            throw new Win32Exception();
        return id;
    }

    public static bool Enabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled))
            throw new Win32Exception();
        return enabled;
    }

    public static uint? ChildId()
    {
        if (WTSGetChildSessionId(out var id) && id != uint.MaxValue)
            return id;
        return null;
    }

    public static bool IsSignedIn(uint sessionId)
    {
        const int wtsUserName = 5;
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, wtsUserName, out var buffer, out _))
            return false;
        try { return !string.IsNullOrEmpty(Marshal.PtrToStringUni(buffer)); }
        finally { WTSFreeMemory(buffer); }
    }

    public static void LogOffChild()
    {
        const int errorNotFound = 1168;
        if (!WTSGetChildSessionId(out var child))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == errorNotFound)
                return;
            throw new Win32Exception(error);
        }
        if (child == uint.MaxValue)
            return;
        if (child == 0 || child == CurrentId)
            throw new InvalidOperationException($"Invalid child session ID {child}.");
        if (!WTSLogoffSession(IntPtr.Zero, child, wait: false))
            throw new Win32Exception();
    }

    public static void SetEnabled(bool value)
    {
        if (!IsAdministrator)
            throw new InvalidOperationException("This installation command requires an elevated PowerShell.");
        if (Enabled() != value && !WTSEnableChildSessions(value))
            throw new Win32Exception();
    }

    public static void RequireInteractiveUser()
    {
        if (CurrentId == 0 || !Environment.UserInteractive || IsAdministrator)
            throw new InvalidOperationException("Start this command as the ordinary user in an interactive Windows session.");
    }

    public static string Edition()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return $"{key?.GetValue("EditionID")} {key?.GetValue("DisplayVersion")} " +
            $"{key?.GetValue("CurrentBuild")}.{key?.GetValue("UBR")}";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSIsChildSessionsEnabled(out bool enabled);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(nint server, uint sessionId, bool wait);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnableChildSessions(bool enabled);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(nint server, uint sessionId, int kind,
        out nint buffer, out uint size);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(nint buffer);
}
