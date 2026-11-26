using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsChildSession;

internal static class JobProcess
{
    public static async Task<TaskResult> Run(TaskSpec spec, TaskResult result, Action<TaskResult> started, CancellationToken cancel)
    {
        var timer = Stopwatch.StartNew();
        if (cancel.IsCancellationRequested)
            return Finish(result, "cancelled");
        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception();
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
            throw new Win32Exception();
        using var stdout = File.OpenHandle(result.StdoutPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Inheritable);
        using var stderr = File.OpenHandle(result.StderrPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Inheritable);
        if (!Directory.Exists(spec.Cwd))
            throw new DirectoryNotFoundException($"Working directory does not exist: {spec.Cwd}");
        if (!File.Exists(spec.Program))
            throw new FileNotFoundException($"Executable does not exist: {spec.Program}");
        using var stdin = OpenNull();

        var info = StartProcess(spec.Program, spec.Arguments, spec.Cwd, stdin, stdout, stderr, job, suspended: true);
        using var process = new SafeProcessHandle(info.Process, ownsHandle: true);
        using var thread = new SafeWaitHandle(info.Thread, ownsHandle: true);
        var processSession = WindowsSessions.ForProcess(info.ProcessId);
        if (processSession != result.ChildSessionId || processSession == result.ParentSessionId)
            throw new InvalidOperationException($"Suspended process is in session {processSession}; expected child session {result.ChildSessionId}.");
        result = result with { ProcessSessionId = processSession, Status = "running", UpdatedAt = DateTimeOffset.UtcNow };
        started(result);
        var timeout = TimeSpan.FromSeconds(spec.TimeoutSeconds);
        if (!cancel.IsCancellationRequested && timer.Elapsed < timeout && ResumeThread(thread) == uint.MaxValue)
            throw new Win32Exception();

        while (ActiveProcesses(job) != 0)
        {
            if (cancel.IsCancellationRequested || timer.Elapsed >= timeout)
            {
                var status = cancel.IsCancellationRequested ? "cancelled" : "timed_out";
                if (!TerminateJobObject(job, status == "cancelled" ? 1223u : 1460u))
                    throw new Win32Exception();
                var cleanup = Stopwatch.StartNew();
                while (ActiveProcesses(job) != 0)
                {
                    if (cleanup.Elapsed > TimeSpan.FromSeconds(10))
                        throw new InvalidOperationException("Could not confirm termination of the task process tree.");
                    await Task.Delay(50).ConfigureAwait(false);
                }
                return Finish(result, status, ReadExitCode(process));
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        var exitCode = ReadExitCode(process);
        return Finish(result, exitCode == 0 ? "completed" : "process_failed", exitCode);
    }

    public static void StartHost(string executable)
    {
        // The host persists after the launcher exits. Inherit only NUL, including when the CLI is piped.
        using var device = OpenNull();
        var info = StartProcess(executable, ["host"], Path.GetDirectoryName(executable)!,
            device, device, device, null, suspended: false);
        using var process = new SafeProcessHandle(info.Process, ownsHandle: true);
        using var thread = new SafeWaitHandle(info.Thread, ownsHandle: true);
    }

    private static TaskResult Finish(TaskResult result, string status, int? exitCode = null) =>
        result with { Status = status, ExitCode = exitCode, UpdatedAt = DateTimeOffset.UtcNow };

    private static int ReadExitCode(SafeProcessHandle process)
    {
        if (!GetExitCodeProcess(process, out var code))
            throw new Win32Exception();
        return unchecked((int)code);
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var counts, Marshal.SizeOf<Accounting>(), IntPtr.Zero))
            throw new Win32Exception();
        return counts.ActiveProcesses;
    }

    private static SafeFileHandle OpenNull()
    {
        var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Inherit = 1 };
        var handle = CreateFile("NUL", 0xC0000000, 3, ref security, 3, 0, IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;
        var error = new Win32Exception();
        handle.Dispose();
        throw error;
    }

    private static unsafe ProcessInfo StartProcess(string program, string[] arguments, string cwd,
        SafeFileHandle stdin, SafeFileHandle stdout, SafeFileHandle stderr, SafeFileHandle? job, bool suspended)
    {
        var attributeCount = job is null ? 1 : 2;
        var size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
        if (size == IntPtr.Zero)
            throw new Win32Exception();
        var list = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(list, attributeCount, 0, ref size))
                throw new Win32Exception();
            try
            {
                var handles = stackalloc nint[] { stdin.DangerousGetHandle(), stdout.DangerousGetHandle(), stderr.DangerousGetHandle() };
                var handleCount = ReferenceEquals(stdin, stdout) ? 1 : 3;
                if (!UpdateProcThreadAttribute(list, 0, 0x20002, (nint)handles, handleCount * IntPtr.Size, 0, 0))
                    throw new Win32Exception();
                var jobHandle = job?.DangerousGetHandle() ?? 0;
                if (job is not null &&
                    !UpdateProcThreadAttribute(list, 0, 0x2000D, (nint)(&jobHandle), IntPtr.Size, 0, 0))
                    throw new Win32Exception();
                var startup = new StartupInfoEx
                {
                    Info = new StartupInfo
                    {
                        Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, // STARTF_USESTDHANDLES
                        StdIn = handles[0], StdOut = handles[1], StdErr = handles[2]
                    },
                    Attributes = list
                };
                var commandLine = new StringBuilder(WindowsArguments.Quote(program));
                foreach (var arg in arguments)
                    commandLine.Append(' ').Append(WindowsArguments.Quote(arg));
                if (commandLine.Length >= 32767)
                    throw new ArgumentException("The Windows command line exceeds 32766 UTF-16 code units.");
                const uint extendedStartupNoWindow = 0x00080000 | 0x08000000;
                var flags = extendedStartupNoWindow | (suspended ? 0x00000004u : 0);
                if (!CreateProcess(program, commandLine, 0, 0, true, flags, 0, cwd, ref startup, out var process))
                    throw new Win32Exception();
                return process;
            }
            finally
            {
                DeleteProcThreadAttributeList(list);
                GC.KeepAlive(job);
                GC.KeepAlive(stdin);
                GC.KeepAlive(stdout);
                GC.KeepAlive(stderr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int Length; public nint Descriptor; public int Inherit; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long UserTime, KernelTime, PeriodUserTime, PeriodKernelTime;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public nint ReservedBytes, StdIn, StdOut, StdErr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string file, uint access, uint share, ref SecurityAttributes security,
        uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, out Accounting counts, int size, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nint size,
        nint previous, nint returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string application, StringBuilder commandLine, nint processAttributes,
        nint threadAttributes, bool inherit, uint flags, nint env, string cwd, ref StartupInfoEx startup, out ProcessInfo process);
}

internal static class WindowsArguments
{
    public static string Quote(string value)
    {
        // Double backslashes immediately before a quote, including the closing quote.
        var quoted = new StringBuilder().Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
