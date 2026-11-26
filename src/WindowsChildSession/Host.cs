using System.Runtime.InteropServices;

namespace WindowsChildSession;

internal sealed class Host(RdpWindow window, uint parent)
{
    private readonly object gate = new();
    private readonly SemaphoreSlim connectionGate = new(1);
    private CancellationTokenSource? preparingCancel;
    private Guid? preparing;
    private string? connectionError;
    private uint lastChild;

    public static void Serve()
    {
        WindowsSessions.RequireInteractiveUser();
        var parent = WindowsSessions.CurrentId;
        using var singleton = TaskStore.TryLease($"host-{parent}");
        if (singleton is null)
            return;
        using var pipe = LocalPipe.OpenServer(LocalPipe.HostName(parent));
        using var stop = new CancellationTokenSource();
        ApplicationConfiguration.Initialize();
        using var window = new RdpWindow();
        var host = new Host(window, parent);
        Task? server = null;
        window.Shown += (_, _) =>
        {
            server = Task.Run(async () =>
            {
                try { await LocalPipe.Serve(pipe, parent, host.Handle, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(TaskStore.Root, $"host-{parent}.error"), ex.Message);
                    if (!stop.IsCancellationRequested)
                        await window.InvokeAsync(window.Close, stop.Token);
                }
            });
        };
        Application.Run(window);
        stop.Cancel();
        // Closing the window terminates the host. The child owns every accepted process tree.
        if (server is { IsCompleted: true, IsFaulted: true })
            server.GetAwaiter().GetResult();
    }

    private async Task<PipeReply> Handle(PipeRequest request)
    {
        switch (request.Action)
        {
            case "status":
                return await Status().ConfigureAwait(false);
            case "connect":
                Volatile.Write(ref connectionError, null);
                _ = Task.Run(ConnectForInspection);
                return new PipeReply("connecting");
            case "disconnect":
                await window.InvokeAsync(window.Disconnect);
                return new PipeReply("disconnected");
            case "stop":
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200).ConfigureAwait(false);
                    await window.InvokeAsync(window.Close);
                });
                return new PipeReply("stopping");
            case "run" when request.Task is { } task:
                return Submit(task);
            case "cancel" when request.TaskId is { } id:
                return await Cancel(id).ConfigureAwait(false);
            default:
                return new PipeReply("error", Error: "Unknown host command.");
        }
    }

    private PipeReply Submit(TaskSpec spec)
    {
        spec.Validate();
        var fingerprint = spec.Fingerprint();
        lock (gate)
        {
            if (TaskStore.Read(spec.TaskId) is { } existing)
                return existing.Fingerprint == fingerprint
                    ? new PipeReply("existing", Task: existing.Observe())
                    : new PipeReply("conflict", Error: "This task ID is already associated with a different request.");
            var lease = TaskStore.TryLease("preparing");
            if (lease is null)
                return new PipeReply("busy", ActiveTaskId: preparing, Error: "The host is already preparing a task.");
            try
            {
                var directory = TaskStore.DirectoryFor(spec.TaskId);
                var result = new TaskResult(spec.TaskId, "preparing", parent, null, null, null,
                    Path.Combine(directory, "stdout.log"), Path.Combine(directory, "stderr.log"), DateTimeOffset.UtcNow);
                var record = TaskRecord.Owned(result, fingerprint);
                TaskStore.Write(record, create: true);
                var cancel = new CancellationTokenSource();
                preparing = spec.TaskId;
                preparingCancel = cancel;
                _ = Task.Run(() => Dispatch(spec, record, lease, cancel));
                return new PipeReply("accepted", Task: result);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
    }

    private async Task Dispatch(TaskSpec spec, TaskRecord record, FileStream lease, CancellationTokenSource cancel)
    {
        var result = record.Result;
        var sent = false;
        try
        {
            var child = await EnsureChild(cancel.Token).ConfigureAwait(false);
            result = result with { ChildSessionId = child };
            var worker = await EnsureExecutor(child, cancel.Token).ConfigureAwait(false);
            if (worker.ActiveTaskId is { } active)
                throw new InvalidOperationException($"Task {active} is still active in child session {child}.");
            cancel.Token.ThrowIfCancellationRequested();
            if (WindowsSessions.ChildId() != child)
                throw new InvalidOperationException("Windows changed the child session during executor startup.");

            result = result with { Status = "dispatching", UpdatedAt = DateTimeOffset.UtcNow };
            TaskStore.Write(record with { Result = result });
            sent = true;
            var reply = await LocalPipe.Call(LocalPipe.ExecutorName(child), child, new PipeRequest("run", spec))
                .ConfigureAwait(false);
            if (reply.Status is "dispatch_rejected" or "busy")
            {
                sent = false;
                throw new InvalidOperationException(reply.Error);
            }
            if (reply.Status != "accepted")
                throw new IOException(reply.Error ?? "The executor did not confirm dispatch.");
            if (cancel.IsCancellationRequested)
                await LocalPipe.Call(LocalPipe.ExecutorName(child), child, new PipeRequest("cancel", TaskId: spec.TaskId))
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!sent)
            {
                var cancelled = cancel.IsCancellationRequested;
                result = result with
                {
                    Status = cancelled ? "cancelled" : "start_failed",
                    Error = cancelled ? null : ex.Message,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                try { TaskStore.Write(record with { Result = result }); }
                catch (Exception io) { Environment.FailFast("Could not persist the task start result.", io); }
            }
            Volatile.Write(ref connectionError, cancel.IsCancellationRequested ? null : ex.Message);
        }
        finally
        {
            lock (gate)
            {
                preparing = null;
                preparingCancel = null;
                lease.Dispose();
                cancel.Dispose();
            }
        }
    }

    private async Task<PipeReply> Cancel(Guid id)
    {
        lock (gate)
        {
            if (preparing == id)
            {
                preparingCancel!.Cancel();
                return new PipeReply("cancelling", ActiveTaskId: id);
            }
        }
        var record = TaskStore.Read(id);
        if (record is null)
            return new PipeReply("not_found", Error: "No task has this ID.");
        var result = record.Observe();
        if (result.ParentSessionId != parent)
            return new PipeReply("error", Error: $"Cancel from the task's parent session {result.ParentSessionId}.");
        if (result.Status is not ("preparing" or "running" or "unknown") || result.ChildSessionId is not { } child)
            return new PipeReply(result.Status, Task: result);
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(1400));
            var acknowledgement = await LocalPipe.Call(LocalPipe.ExecutorName(child), child,
                new PipeRequest("cancel", TaskId: id), limit.Token).ConfigureAwait(false);
            if (acknowledgement.Status == "cancelling")
                return acknowledgement with { Task = result };
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return new PipeReply("cancel_unconfirmed", Task: result, Error: "Could not contact the task's executor.");
        }
        result = TaskStore.Read(id)!.Observe();
        return new PipeReply(result.Status, Task: result);
    }

    private async Task<PipeReply> Status()
    {
        var child = WindowsSessions.ChildId();
        var connection = await window.InvokeAsync(() => window.ConnectionState);
        var connected = connection == 1 && child is > 0 && child != parent && WindowsSessions.IsSignedIn(child.Value);
        var workerSession = child ?? Volatile.Read(ref lastChild);
        var worker = workerSession > 0 && workerSession != parent
            ? await QueryExecutor(workerSession, default).ConfigureAwait(false) : null;
        Guid? active;
        lock (gate) { active = preparing ?? worker?.ActiveTaskId; }
        var state = connected ? "connected" : connection == 0 ? "disconnected" : "connecting";
        return new PipeReply("ok", Service: new ServiceStatus(parent, WindowsSessions.Enabled(), child, state,
            worker?.Status == "ready", active, Volatile.Read(ref connectionError)));
    }

    private async Task ConnectForInspection()
    {
        try { await EnsureChild(default).ConfigureAwait(false); }
        catch (Exception ex) { Volatile.Write(ref connectionError, ex.Message); }
    }

    private async Task<uint> EnsureChild(CancellationToken token)
    {
        await connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref connectionError, null);
            await window.InvokeAsync(window.StartConnection, token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            try
            {
                while (true)
                {
                    var state = await window.InvokeAsync(() => window.ConnectionState, deadline.Token);
                    var child = WindowsSessions.ChildId();
                    if (state == 1 && child is > 0 && child != parent && WindowsSessions.IsSignedIn(child.Value))
                    {
                        Volatile.Write(ref lastChild, child.Value);
                        await window.InvokeAsync(() => window.SetMessage($"Windows child session {child}"), deadline.Token);
                        return child.Value;
                    }
                    if (state == 0)
                        throw new InvalidOperationException(await window.InvokeAsync(window.DisconnectReason, deadline.Token));
                    await Task.Delay(250, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("The child session did not finish signing in. Check the Windows Child Session window.");
            }
        }
        finally
        {
            connectionGate.Release();
        }
    }

    private async Task<PipeReply> EnsureExecutor(uint child, CancellationToken token)
    {
        if (await QueryExecutor(child, token).ConfigureAwait(false) is { Status: "ready" } existing)
            return existing;
        var attempt = Guid.NewGuid();
        var failurePath = ExecutorStartup.FailurePath(attempt);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(25));
            var launched = false;
            string? schedulingError = null;
            try
            {
                while (true)
                {
                    if (await QueryExecutor(child, deadline.Token).ConfigureAwait(false) is { Status: "ready" } worker)
                        return worker;
                    if (WindowsSessions.ChildId() != child)
                        throw new InvalidOperationException($"Windows disconnected child session {child} before the executor was ready.");
                    if (!launched)
                    {
                        try
                        {
                            ExecutorStartup.Launch(parent, child, attempt);
                            launched = true;
                        }
                        catch (COMException ex) { schedulingError = ex.Message; }
                    }
                    if (File.Exists(failurePath))
                        throw new InvalidOperationException(await File.ReadAllTextAsync(failurePath, deadline.Token));
                    await Task.Delay(500, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException($"No verified executor answered in child session {child}. " +
                    (schedulingError ?? "Check the scheduled task or start the executor inside the child desktop."));
            }
        }
        finally
        {
            File.Delete(failurePath);
        }
    }

    private static async Task<PipeReply?> QueryExecutor(uint child, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMilliseconds(450));
        try
        {
            return await LocalPipe.Call(LocalPipe.ExecutorName(child), child, new PipeRequest("status"), limit.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (IOException) { return null; }
    }
}
