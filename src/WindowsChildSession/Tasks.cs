using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowsChildSession;

internal sealed record TaskSpec(Guid TaskId, string Cwd, string Program, string[] Arguments, int TimeoutSeconds)
{
    public void Validate()
    {
        if (TaskId == Guid.Empty || TimeoutSeconds is < 1 or > 86400 || Arguments is null ||
            Arguments.Any(arg => arg is null || arg.Contains('\0')))
            throw new ArgumentException("Provide a taskId UUID, an arguments array and a timeoutSeconds value from 1 to 86400.");
        if (string.IsNullOrEmpty(Cwd) || !Path.IsPathFullyQualified(Cwd) || Cwd.Contains('\0') ||
            string.IsNullOrEmpty(Program) || !Path.IsPathFullyQualified(Program) || Program.Contains('\0') ||
            !new[] { ".exe", ".com" }.Contains(Path.GetExtension(Program), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Provide absolute paths for cwd and program. Use an executable (.exe or .com) as program.");
    }

    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Cli.Json)));
}

internal sealed record TaskResult(Guid TaskId, string Status, uint ParentSessionId, uint? ChildSessionId,
    uint? ProcessSessionId, int? ExitCode, string StdoutPath, string StderrPath,
    DateTimeOffset UpdatedAt, string? Error = null);

internal sealed record TaskRecord(TaskResult Result, string Fingerprint, int OwnerPid, long OwnerStarted)
{
    private static readonly long StartTime = CurrentStartTime();

    public static TaskRecord Owned(TaskResult result, string fingerprint) =>
        new(result, fingerprint, Environment.ProcessId, StartTime);

    public TaskResult Observe()
    {
        if (Result.Status == "dispatching")
            return Result with { Status = "unknown", Error = "Dispatch was recorded. Query this task ID again for a worker result." };
        if (Result.Status is not ("preparing" or "running"))
            return Result;
        try
        {
            using var owner = Process.GetProcessById(OwnerPid);
            if (owner.StartTime.ToUniversalTime().Ticks == OwnerStarted && !owner.HasExited)
                return Result;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The recorded process no longer owns this execution.
        }
        return Result with { Status = "unknown", Error = "The owning process ended before saving a final result." };
    }

    private static long CurrentStartTime()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }
}

internal static class TaskStore
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsChildSession");

    public static string DirectoryFor(Guid id) => Path.Combine(Root, "tasks", id.ToString("D"));

    public static TaskRecord? Read(Guid id)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(DirectoryFor(id), "result.json"),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<TaskRecord>(stream, Cli.Json)
                ?? throw new InvalidDataException("Task record is empty.");
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return null;
        }
    }

    public static void Write(TaskRecord record, bool create = false)
    {
        var directory = Directory.CreateDirectory(DirectoryFor(record.Result.TaskId)).FullName;
        var path = Path.Combine(directory, "result.json");
        var temp = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, record, Cli.Json);
                stream.Flush(flushToDisk: true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: !create);
                    break;
                }
                catch (Exception ex) when (!create && attempt < 16 &&
                    (ex is IOException or UnauthorizedAccessException) && (ex.HResult & 0xFFFF) is 5 or 32 or 33)
                {
                    // Windows can reject replacement while another process has the result file open.
                    Thread.Sleep(15 * (attempt + 1));
                }
            }
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public static FileStream? TryLease(string name)
    {
        Directory.CreateDirectory(Root);
        try
        {
            return new FileStream(Path.Combine(Root, name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }

    public static FileStream? AcquireHostLease(uint session) => WithHostLease(session, lease => lease);

    public static bool HostIsAbsent(uint session) => WithHostLease(session, lease =>
    {
        lease?.Dispose();
        return lease is not null;
    });

    // Release a probe's temporary lease before a concurrent host attempts startup.
    private static T WithHostLease<T>(uint session, Func<FileStream?, T> action)
    {
        using var gate = new Mutex(false, $"Local\\WindowsChildSession.{WindowsSessions.UserSid}.host-lease.{session}");
        try
        {
            if (!gate.WaitOne(TimeSpan.FromSeconds(1)))
                throw new TimeoutException("Could not verify ownership of the host session lock.");
        }
        catch (AbandonedMutexException) { /* The previous caller exited while opening the file. */ }
        try { return action(TryLease($"host-{session}")); }
        finally { gate.ReleaseMutex(); }
    }
}

internal sealed record ServiceStatus(uint ParentSessionId, bool ChildSessionsEnabled, uint? ChildSessionId,
    string Connection, bool ExecutorReady, Guid? ActiveTaskId, string? Error = null);

internal sealed record PipeRequest(string Action, TaskSpec? Task = null, Guid? TaskId = null);

internal sealed record PipeReply(string Status, TaskResult? Task = null, ServiceStatus? Service = null,
    Guid? ActiveTaskId = null, string? Error = null);
