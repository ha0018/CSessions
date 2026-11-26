using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WindowsChildSession;

internal static class Cli
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [STAThread]
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            switch (args)
            {
                case ["probe"]:
                    return Output(new
                    {
                        status = "completed",
                        processSessionId = WindowsSessions.CurrentId,
                        childSessionId = WindowsSessions.ChildId(),
                        childSessionsEnabled = WindowsSessions.Enabled(),
                        edition = WindowsSessions.Edition(),
                        architecture = RuntimeInformation.ProcessArchitecture.ToString()
                    });
                case ["feature", "enable"]:
                case ["feature", "disable"]:
                    WindowsSessions.SetEnabled(args[1] == "enable");
                    return Output(new { status = "completed", childSessionsEnabled = WindowsSessions.Enabled() });
                case ["install-executor"]:
                    WindowsSessions.RequireInteractiveUser();
                    ExecutorStartup.Install();
                    return Output(new { status = "installed" });
                case ["uninstall-executor"]:
                    WindowsSessions.RequireInteractiveUser();
                    ExecutorStartup.Uninstall();
                    return Output(new { status = "uninstalled" });
                case ["host"]:
                    Host.Serve();
                    return 0;
                case ["executor", var parent, var child]:
                    Executor.Serve(uint.Parse(parent), uint.Parse(child));
                    return 0;
                case ["executor", var parent, var child, var attempt]:
                    var boot = Guid.Parse(attempt);
                    try { Executor.Serve(uint.Parse(parent), uint.Parse(child)); }
                    catch (Exception error)
                    {
                        Directory.CreateDirectory(TaskStore.Root);
                        File.WriteAllText(ExecutorStartup.FailurePath(boot), error.Message);
                        throw;
                    }
                    return 0;
                case ["help"]:
                case ["--help"]:
                    return Output(new
                    {
                        commands = new[] { "status [taskId]", "run --request <file> [--detach]", "cancel <taskId>",
                            "connect", "disconnect", "stop", "probe", "install-executor", "uninstall-executor",
                            "feature enable|disable", "executor <parentSessionId> <childSessionId>" }
                    });
                default:
                    return Execute(args).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            if (args is ["host"])
            {
                Directory.CreateDirectory(TaskStore.Root);
                File.WriteAllText(HostErrorPath(WindowsSessions.CurrentId), ex.Message);
            }
            return Output(new { status = "error", error = ex.Message, hresult = $"0x{ex.HResult:X8}" }, 1);
        }
    }

    private static async Task<int> Execute(string[] args)
    {
        var parent = WindowsSessions.CurrentId;
        switch (args)
        {
            case ["status"]:
                var hostStatus = await ContactHost(parent, "status");
                if (hostStatus.Service is { } service)
                    return Output(service);
                var absent = hostStatus.Status == "host_not_running";
                return Output(new ServiceStatus(parent, WindowsSessions.Enabled(), WindowsSessions.ChildId(),
                    absent ? "host_not_running" : "unknown", false, null, absent ? ReadHostError(parent) : hostStatus.Error),
                    absent ? 0 : 1);
            case ["status", var id]:
                return ReadResult(Guid.Parse(id));
            case ["run", "--request", var file]:
            case ["run", "--request", var fileWithDetach, "--detach"]:
                WindowsSessions.RequireInteractiveUser();
                using (var stream = File.OpenRead(args[2]))
                {
                    if (stream.Length > 128 * 1024)
                        throw new ArgumentException("The request is larger than 128 KiB.");
                    var spec = JsonSerializer.Deserialize<TaskSpec>(stream, Json)
                        ?? throw new ArgumentException("The request is empty.");
                    spec.Validate();
                    return await Run(parent, spec, args.Length == 4);
                }
            case ["cancel", var id]:
                WindowsSessions.RequireInteractiveUser();
                var task = Guid.Parse(id);
                var saved = TaskStore.Read(task);
                if (saved is null)
                    return ReadResult(task);
                var result = saved.Observe();
                if (result.Status is not ("preparing" or "running" or "unknown"))
                    return Output(result, 0);
                if (result.ParentSessionId != parent)
                    return Output(new PipeReply("error", Task: result, Error: $"Cancel from parent session {result.ParentSessionId}."), 1);
                await StartHost(parent);
                var cancellation = await LocalPipe.Call(LocalPipe.HostName(parent), parent, new PipeRequest("cancel", TaskId: task));
                return Output(cancellation, cancellation.Status is "error" or "not_found" or "cancel_unconfirmed" ? 1 : 0);
            case ["connect"]:
                WindowsSessions.RequireInteractiveUser();
                await StartHost(parent);
                await LocalPipe.Call(LocalPipe.HostName(parent), parent, new PipeRequest("connect"));
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(82))
                {
                    var state = (await TryContact(parent, "status"))?.Service;
                    if (state is not null && (state.Connection == "connected" || state.Error is not null))
                        return Output(state, state.Connection == "connected" ? 0 : 1);
                    await Task.Delay(500);
                }
                return Output(new PipeReply("connection_failed", Error: "The child session did not finish signing in."), 1);
            case ["disconnect"]:
            case ["stop"]:
                WindowsSessions.RequireInteractiveUser();
                var reply = await ContactHost(parent, args[0]);
                var expected = args[0] == "stop" ? "stopping" : "disconnected";
                return Output(reply, reply.Status == expected || reply.Status == "host_not_running" ? 0 : 1);
            default:
                return Output(new { status = "invalid_arguments", error = "Run WindowsChildSession.exe --help for usage." }, 2);
        }
    }

    private static async Task<int> Run(uint parent, TaskSpec spec, bool detach)
    {
        var existing = TaskStore.Read(spec.TaskId);
        if (existing is not null && existing.Fingerprint != spec.Fingerprint())
            return Output(new { status = "conflict", spec.TaskId, error = "This task ID belongs to a different request." }, 1);
        if (existing is null)
        {
            await StartHost(parent);
            PipeReply reply;
            try
            {
                reply = await LocalPipe.Call(LocalPipe.HostName(parent), parent, new PipeRequest("run", spec));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                var saved = TaskStore.Read(spec.TaskId);
                if (saved is null)
                    return Output(new { status = "unknown", spec.TaskId, error = "Host delivery could not be confirmed. Query this task ID." }, 1);
                if (saved.Fingerprint != spec.Fingerprint())
                    return Output(new { status = "conflict", spec.TaskId, error = "This task ID belongs to a different request." }, 1);
                reply = new PipeReply("existing", saved.Observe());
            }
            if (reply.Task is null)
                return Output(new { reply.Status, spec.TaskId, reply.ActiveTaskId, reply.Error }, 1);
        }
        if (detach)
            return ReadResult(spec.TaskId);

        using var interrupted = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            interrupted.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            while (true)
            {
                var record = TaskStore.Read(spec.TaskId) ?? throw new IOException("The task record disappeared.");
                var result = record.Observe();
                var transferring = record.Result.Status == "dispatching" &&
                    DateTimeOffset.UtcNow - record.Result.UpdatedAt < TimeSpan.FromSeconds(5);
                if (!transferring && result.Status is not ("preparing" or "running"))
                    return Output(result, ExitStatus(result.Status));
                await Task.Delay(250, interrupted.Token);
            }
        }
        catch (OperationCanceledException)
        {
            return Output(new { status = "wait_interrupted", spec.TaskId, error = "Query the same task ID for its result." }, 130);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int ReadResult(Guid id) => TaskStore.Read(id) is { } record
        ? Output(record.Observe())
        : Output(new { status = "not_found", taskId = id, error = "No task has this ID." }, 1);

    private static async Task StartHost(uint parent)
    {
        if (await TryContact(parent, "status") is { Status: "ok" })
            return;
        Directory.CreateDirectory(TaskStore.Root);
        File.Delete(HostErrorPath(parent));
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the executable.");
        JobProcess.StartHost(executable);
        var limit = Stopwatch.StartNew();
        while (limit.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (await TryContact(parent, "status") is { Status: "ok" })
                return;
            if (ReadHostError(parent) is { } error)
                throw new InvalidOperationException(error);
            await Task.Delay(200);
        }
        throw new TimeoutException("The parent host did not open its local pipe. Start from the signed-in Windows desktop.");
    }

    private static async Task<PipeReply?> TryContact(uint parent, string action)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(950));
        try { return await LocalPipe.Call(LocalPipe.HostName(parent), parent, new PipeRequest(action), limit.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { return null; }
    }

    private static async Task<PipeReply> ContactHost(uint parent, string action)
    {
        if (await TryContact(parent, action) is { } reply)
            return reply;
        return TaskStore.HostIsAbsent(parent)
            ? new PipeReply("host_not_running")
            : new PipeReply("host_unresponsive", Error: $"The host still holds its session lock. No reply confirmed '{action}'.");
    }

    private static string HostErrorPath(uint id) => Path.Combine(TaskStore.Root, $"host-{id}.error");

    private static string? ReadHostError(uint id) => File.Exists(HostErrorPath(id)) ? File.ReadAllText(HostErrorPath(id)) : null;

    private static int ExitStatus(string status) => status switch
    {
        "completed" => 0,
        "process_failed" => 10,
        "start_failed" => 11,
        "timed_out" => 12,
        "cancelled" => 13,
        _ => 14
    };

    private static int Output<T>(T value, int code = 0)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, Json));
        return code;
    }
}
