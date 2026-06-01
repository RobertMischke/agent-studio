using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// The Runner ↔ Server lease API under <c>/api/runner/lease</c> (Step 6 of the
/// task-execution-and-log architecture). This is the boundary that lets the
/// server be the single lease authority while a Runner — in-process today, on
/// another machine tomorrow — acquires/heartbeats/releases a task lease over
/// HTTP before spawning a CLI on it.
///
/// <para>
/// The endpoints are thin glue over the unit-tested lease primitive
/// (<see cref="PickupLockFile"/>): they resolve a task key to its folder, build
/// the caller's <see cref="PickupLockOwner"/> from the request body, and map the
/// lock outcome onto the wire <see cref="LeaseResponse"/>. The TTL/heartbeat
/// semantics (a lapsed remote lease becomes reclaimable) live in the primitive.
/// </para>
/// </summary>
public static class LeaseEndpoints
{
    public static void MapLeaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/lease");

        group.MapPost("/acquire", (LeaseAcquireRequest req, ITaskScanner scanner, PickupLockFile locks) =>
        {
            var folder = ResolveFolder(scanner, req.TaskKey);
            if (folder is null)
                return Results.NotFound(new LeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));

            var owner = new PickupLockOwner
            {
                Pid = req.Pid,
                Hostname = req.Hostname,
                Role = req.Role,
                BackendName = req.BackendName,
                BackendPort = req.BackendPort ?? 0,
                ProjectName = req.ProjectName,
                JobId = req.TaskKey
            };

            var outcome = locks.TryAcquire(folder, owner, out var existing);
            var granted = outcome != LockAcquireOutcome.ForeignHeld;
            // On grant the authoritative record is the freshly-written one; on a
            // foreign hold it is the existing owner the caller lost the race to.
            var info = granted ? locks.Peek(folder) : existing;
            return Results.Ok(new LeaseResponse(outcome.ToString(), granted, ToDto(info)));
        });

        group.MapPost("/renew", (LeaseRenewRequest req, ITaskScanner scanner, PickupLockFile locks) =>
        {
            var folder = ResolveFolder(scanner, req.TaskKey);
            if (folder is null)
                return Results.NotFound(new LeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));

            var owner = OwnerFor(req.Pid, req.Hostname, req.BackendName, req.TaskKey);
            var ok = locks.Renew(folder, owner);
            return Results.Ok(new LeaseResponse(ok ? "Renewed" : "NotOwner", ok, ToDto(locks.Peek(folder))));
        });

        group.MapPost("/release", (LeaseReleaseRequest req, ITaskScanner scanner, PickupLockFile locks) =>
        {
            var folder = ResolveFolder(scanner, req.TaskKey);
            if (folder is null)
                return Results.NotFound(new LeaseResponse("TaskNotFound", false, null, $"No task '{req.TaskKey}'."));

            locks.Release(folder, OwnerFor(req.Pid, req.Hostname, req.BackendName, req.TaskKey));
            return Results.Ok(new LeaseResponse("Released", false, ToDto(locks.Peek(folder))));
        });

        group.MapGet("/{taskKey}", (string taskKey, ITaskScanner scanner, PickupLockFile locks) =>
        {
            var folder = ResolveFolder(scanner, taskKey);
            if (folder is null)
                return Results.NotFound(new LeaseResponse("TaskNotFound", false, null, $"No task '{taskKey}'."));
            var info = locks.Peek(folder);
            return Results.Ok(new LeaseResponse(info is null ? "Free" : "Held", false, ToDto(info)));
        });
    }

    private static PickupLockOwner OwnerFor(int pid, string hostname, string backendName, string taskKey) => new()
    {
        Pid = pid,
        Hostname = hostname,
        Role = "",
        BackendName = backendName,
        JobId = taskKey
    };

    private static string? ResolveFolder(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        var task = scanner.ScanAllJobs().FirstOrDefault(t =>
            string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(task?.FolderPath) ? null : task!.FolderPath;
    }

    private static LeaseInfoDto? ToDto(PickupLockInfo? info) => info is null
        ? null
        : new LeaseInfoDto(
            info.Hostname, info.Pid, info.BackendName, info.Role,
            info.ProjectName, info.AcquiredAt, info.ExpiresAt);
}
