namespace WindowsChildSession;

internal sealed class Executor
{
    private Guid? activeId;
    private CancellationTokenSource? activeCancel;
    private readonly object gate = new();

    public static void Serve(uint parent, uint expectedChild)
    {
        WindowsSessions.RequireInteractiveUser();
        var actual = WindowsSessions.CurrentId;
        if (parent == 0 || actual == parent || actual != expectedChild)
            throw new InvalidOperationException($"Executor session mismatch: actual {actual}, parent {parent}, expected child {expectedChild}.");

        using var singleton = TaskStore.TryLease($"executor-{actual}");
        if (singleton is null)
            return;
        var executor = new Executor();
        using var pipe = LocalPipe.OpenServer(LocalPipe.ExecutorName(actual));
        LocalPipe.Serve(pipe, parent, data => Task.FromResult(executor.Handle(parent, actual, data)), default)
            .GetAwaiter().GetResult();
    }

    private PipeReply Handle(uint parent, uint child, PipeRequest request)
    {
        lock (gate)
        {
            switch (request.Action)
            {
                case "status":
                    return new PipeReply("ready", ActiveTaskId: activeId);
                case "cancel":
                    if (activeId is null || activeId != request.TaskId)
                        return new PipeReply("not_active");
                    activeCancel!.Cancel();
                    return new PipeReply("cancelling", ActiveTaskId: activeId);
                case "run" when request.Task is { } spec:
                    spec.Validate();
                    var record = TaskStore.Read(spec.TaskId);
                    if (record is null || record.Result.Status != "dispatching" ||
                        record.Result.ParentSessionId != parent || record.Result.ChildSessionId != child ||
                        record.Fingerprint != spec.Fingerprint())
                        return new PipeReply("dispatch_rejected", Error: "The persisted dispatch does not match this executor.");
                    var lease = TaskStore.TryLease("running");
                    if (lease is null)
                        return new PipeReply("busy", Error: "Another task still has a live process tree.");
                    var cancel = new CancellationTokenSource();
                    activeId = spec.TaskId;
                    activeCancel = cancel;
                    _ = Task.Run(() => Run(spec, record, lease, cancel));
                    return new PipeReply("accepted", ActiveTaskId: activeId);
                default:
                    return new PipeReply("error", Error: "Unknown executor command.");
            }
        }
    }

    private async Task Run(TaskSpec spec, TaskRecord record, FileStream lease, CancellationTokenSource cancel)
    {
        var current = record.Result;
        try
        {
            TaskResult result;
            try
            {
                result = await JobProcess.Run(spec, current, running =>
                {
                    current = running;
                    TaskStore.Write(TaskRecord.Owned(running, record.Fingerprint));
                }, cancel.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = current with
                {
                    Status = current.ProcessSessionId.HasValue ? "unknown" : "start_failed",
                    Error = ex.Message,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
            }
            lock (gate)
            {
                TaskStore.Write(TaskRecord.Owned(result, record.Fingerprint));
                activeId = null;
                activeCancel = null;
                lease.Dispose();
                cancel.Dispose();
            }
        }
        catch (Exception ex)
        {
            // Exiting releases the job and makes a stale running record observable as unknown.
            Environment.FailFast("Could not persist the task result.", ex);
        }
    }
}
