using OrchestratorApi.Models;

namespace OrchestratorApi.Services.TaskAccess;

/// <summary>
/// Single point of access for every read, list, mutation, transition,
/// and change-subscription against task / job state. Phase 1 skeleton:
/// the surface lands with NotImplementedException stubs so consumers
/// can compile against the interface while the in-memory store and
/// disk reflection are implemented in the next phases. See
/// <c>docs/architecture-decisions.md</c> ADR-0024.
/// </summary>
/// <remarks>
/// <para>
/// Hard rule (enforced socially in phase 1, mechanically in phase 4):
/// no service, hosted service, endpoint, or test outside
/// <see cref="OrchestratorApi.Services.TaskAccess"/> may read or write
/// the on-disk job folders. Every consumer goes through this interface.
/// </para>
/// <para>
/// Disk remains the source of truth on cold start. The in-memory index
/// is a view that can always be rebuilt by re-reading the watched
/// project folders.
/// </para>
/// </remarks>
public interface ITaskAccess
{
    JobInfo? FindJob(string jobId, string? watchPath = null);

    JobDetail? GetJobDetail(string jobId, string? watchPath = null);

    IReadOnlyList<JobInfo> ListByLane(string projectName, string lane);

    IReadOnlyList<JobInfo> ListByProject(string projectName);

    TaskAccessSnapshot Snapshot();

    Task<TaskMutationResult> MutateAsync(TaskMutationRequest request, CancellationToken ct = default);

    Task<TaskMutationResult> TransitionLaneAsync(TaskTransitionRequest request, CancellationToken ct = default);

    IDisposable Subscribe(string projectName, Action<TaskChange> callback);
}
