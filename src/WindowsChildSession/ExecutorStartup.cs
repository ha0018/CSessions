using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WindowsChildSession;

internal static class ExecutorStartup
{
    private const int InteractiveToken = 3;
    private const int UseSessionId = 4;
    private static string Name => "WindowsChildSession-Executor-" + WindowsSessions.UserSid;

    public static void Install()
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the executable.");
        if (!string.Equals(Path.GetFileName(path), "WindowsChildSession.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use the published WindowsChildSession.exe to install the executor.");
        using var user = WindowsIdentity.GetCurrent();
        dynamic service = Connect();
        dynamic task = service.NewTask(0);
        task.RegistrationInfo.Description = "Starts the Windows Child Session executor in the selected signed-in session on demand.";
        task.Principal.UserId = user.Name;
        task.Principal.LogonType = InteractiveToken;
        task.Principal.RunLevel = 0;
        task.Settings.AllowDemandStart = true;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.MultipleInstances = 0; // Parallel starts are checked by the per-session pipe.
        dynamic action = task.Actions.Create(0);
        action.Path = path;
        action.WorkingDirectory = Path.GetDirectoryName(path);
        action.Arguments = "executor $(Arg0) $(Arg1) $(Arg2)";
        dynamic folder = service.GetFolder(@"\");
        folder.RegisterTaskDefinition(Name, task, 6, user.Name, null, InteractiveToken, null);
    }

    public static void Uninstall()
    {
        dynamic folder = Connect().GetFolder(@"\");
        try
        {
            dynamic task = folder.GetTask(Name);
            task.Stop(0);
            folder.DeleteTask(Name, 0);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            // Already removed.
        }
    }

    public static void Launch(uint parent, uint child, Guid attempt)
    {
        dynamic folder = Connect().GetFolder(@"\");
        dynamic task;
        try { task = folder.GetTask(Name); }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002))
        {
            throw new InvalidOperationException("The scheduled executor is not installed. Run install-executor or start it manually inside the child desktop.", ex);
        }
        string[] parameters = [parent.ToString(), child.ToString(), attempt.ToString("D")];
        task.RunEx(parameters, UseSessionId, checked((int)child), "");
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    public static string FailurePath(Guid attempt) => Path.Combine(TaskStore.Root, $"executor-{attempt:N}.error");
}
