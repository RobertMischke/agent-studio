namespace AgentStudio.Tasks;

/// <summary>
/// The fenced Runner ↔ Server run-lease API under <c>/api/runner/lease</c>
/// (parallel-task-execution.md §8.2C; ADR-0060). The server is the single lease
/// authority: <c>acquire</c> mints a lease id + a monotonic fencing token per
/// task, <c>renew</c>/<c>release</c> must present the current token, and a stale
/// token — presented after a TTL takeover raised the fence — is rejected. That
/// rejection is the split-brain guard.
///
/// <para>
/// The endpoints are thin glue over the unit-tested lease authority
/// (<see cref="RunLeaseService"/>): they validate the task exists, stamp the
/// caller's <see cref="RunnerIdentity"/> onto a partial acquire request, and
/// return the service's <see cref="RunLeaseResponse"/> verbatim. This is the
/// productive successor to the disk-backed <c>.pickup-lock.json</c> lease
/// (ADR-0044, <see cref="PickupLockFile"/>), which stays the same-machine pickup
/// guard until the runner split (ADR-0059) cuts over.
/// </para>
/// </summary>
public static class LeaseEndpoints
{
    public static void MapLeaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/lease");

        group.MapPost("/acquire", (RunLeaseAcquireRequest req, ITaskScanner scanner, RunLeaseService leases, RunnerIdentity identity) =>
        {
            if (!TaskExists(scanner, req.TaskKey))
                return Results.NotFound(new RunLeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));
            return Results.Ok(leases.TryAcquire(StampIdentity(req, identity)));
        });

        group.MapPost("/renew", (RunLeaseHeartbeatRequest req, RunLeaseService leases) =>
            Results.Ok(leases.Renew(req)));

        group.MapPost("/release", (RunLeaseReleaseRequest req, RunLeaseService leases) =>
            Results.Ok(leases.Release(req)));

        group.MapGet("/{taskKey}", (string taskKey, RunLeaseService leases) =>
            Results.Ok(leases.Peek(taskKey)));
    }

    /// <summary>
    /// Fill a partial acquire request with this backend's runner identity so a
    /// local caller need only name the task; a remote runner supplies its own
    /// identity and those values win. Keeps the previously-unused lease API
    /// productive for the in-process runner without forcing every caller to
    /// re-derive host/pid/backend.
    /// </summary>
    private static RunLeaseAcquireRequest StampIdentity(RunLeaseAcquireRequest req, RunnerIdentity identity) => req with
    {
        RunnerId = string.IsNullOrWhiteSpace(req.RunnerId) ? identity.RunnerId : req.RunnerId,
        RunnerName = string.IsNullOrWhiteSpace(req.RunnerName) ? identity.RunnerName : req.RunnerName,
        Hostname = string.IsNullOrWhiteSpace(req.Hostname) ? identity.Hostname : req.Hostname,
        BackendName = string.IsNullOrWhiteSpace(req.BackendName) ? identity.BackendName : req.BackendName,
        Pid = req.Pid == 0 ? Environment.ProcessId : req.Pid,
    };

    private static bool TaskExists(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return false;
        return scanner.ScanAllJobs().Any(t =>
            string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase));
    }
}
