using OrchestratorApi.Models;

namespace OrchestratorApi.Services.TaskAccess;

/// <summary>
/// Single point of access for every read, list, mutation, transition,
/// and change-subscription against task / job state. ADR-0024. The layer
/// hides the on-disk lane folder shape from outside callers: every
/// operation is keyed by <c>(jobId, watchPath)</c> or
/// <c>(projectName, lane)</c>, never by a raw filesystem path.
/// </summary>
/// <remarks>
/// <para>
/// Hard rule: no service, hosted service, endpoint, or test outside
/// <see cref="OrchestratorApi.Services.TaskAccess"/> or
/// <see cref="OrchestratorApi.Services.Jobs"/> may read or write the
/// on-disk job folders. Outside callers go through this interface.
/// </para>
/// <para>
/// Disk remains the source of truth on cold start. The in-memory index
/// is a view that can always be rebuilt by re-reading the watched
/// project folders. The implementation delegates to
/// <c>TaskScannerService</c> / <c>TaskMutationService</c> /
/// <c>TaskStateMachine</c> / <c>TaskTransitionService</c>; the
/// single-state-machine authority stays inside the layer.
/// </para>
/// </remarks>
public interface ITaskAccess
{
    JobInfo? FindJob(string jobId, string? watchPath = null);

    JobDetail? GetJobDetail(string jobId, string? watchPath = null);

    /// <summary>
    /// List jobs in <paramref name="lane"/> within the project identified
    /// by <paramref name="projectName"/> (matches
    /// <see cref="WatchPathEntry.Name"/>, case-insensitive).
    /// </summary>
    IReadOnlyList<JobInfo> ListByLane(string projectName, string lane);

    /// <summary>
    /// List jobs in <paramref name="lane"/> within the workspace identified
    /// by <paramref name="watchPath"/> (matches
    /// <see cref="WatchPathEntry.Path"/>, case-insensitive). Used by
    /// endpoints that already resolved a watch path and want to skip the
    /// project-name lookup.
    /// </summary>
    IReadOnlyList<JobInfo> ListByLaneInWorkspace(string watchPath, string lane);

    IReadOnlyList<JobInfo> ListByProject(string projectName);

    TaskAccessSnapshot Snapshot();

    Task<TaskMutationResult> MutateAsync(TaskMutationRequest request, CancellationToken ct = default);

    Task<TaskMutationResult> TransitionLaneAsync(TaskTransitionRequest request, CancellationToken ct = default);

    IDisposable Subscribe(string projectName, Action<TaskChange> callback);

    // --- Layer-internal escape hatches for Tier-3 consumers ---
    //
    // The methods below replace specific Path.Combine + Directory.Move /
    // Directory.Delete patterns in the migration-target consumers. They
    // keep the lane-folder shape inside the layer; callers address jobs
    // by (watchPath, lane, slug) typed strings, never by raw path.

    /// <summary>
    /// True when a job folder named <paramref name="slug"/> exists under
    /// <paramref name="lane"/> in <paramref name="watchPath"/>. Replaces
    /// <c>Directory.Exists(Path.Combine(watchPath, lane, slug))</c> at
    /// call sites that need a collision check before minting a new slug.
    /// </summary>
    bool SlugExistsInLane(string watchPath, string lane, string slug);

    /// <summary>
    /// List every immediate subfolder name under <paramref name="lane"/>
    /// in <paramref name="watchPath"/>, including folders without a
    /// <c>job.json</c>. Used by orphan-rescue paths that need to see
    /// folders the typed index has dropped because they are unparseable.
    /// </summary>
    IReadOnlyList<string> ListLaneFolderNames(string watchPath, string lane);

    /// <summary>
    /// Like <see cref="ListLaneFolderNames"/> but returns absolute
    /// folder paths instead of slug names. Orphan-rescue paths need
    /// to read files inside each folder (logs, job.json mtime); having
    /// the layer hand back the resolved path lets callers skip the
    /// lane-folder construction they would otherwise do.
    /// </summary>
    IReadOnlyList<LaneFolderRef> ListLaneFolders(string watchPath, string lane);

    /// <summary>
    /// Snapshot of every lane folder across every lane in
    /// <paramref name="watchPath"/>, including folders without
    /// <c>job.json</c>. Drives the queue-health endpoint without
    /// reaching into the filesystem from outside the layer.
    /// </summary>
    IReadOnlyList<LaneFolderEntry> ListAllLaneFolders(string watchPath);

    /// <summary>
    /// Return the absolute folder path for <paramref name="jobId"/>.
    /// Layer-internal so callers can append <c>logs/</c>,
    /// <c>attachments/</c>, or per-job placard files without
    /// constructing the lane path themselves.
    /// </summary>
    string? GetJobFolderPath(string jobId, string? watchPath = null);

    /// <summary>
    /// Move a stale folder (typically a <c>3-progress</c> orphan with no
    /// <c>job.json</c>) to <c>3a-failed-pickup</c> under
    /// <paramref name="destinationSlug"/>, optionally writing a
    /// <c>failed-pickup-reason.md</c> placard alongside it. The move
    /// goes through <see cref="OrchestratorApi.Services.Jobs.TaskStateMachine"/>
    /// inside the layer.
    /// </summary>
    TaskMutationResult MoveOrphanToFailedPickup(
        string watchPath,
        string sourceLane,
        string sourceSlug,
        string destinationSlug,
        string? reasonMarkdown);

    /// <summary>
    /// Best-effort delete of a lane subfolder (e.g. an empty post-move
    /// skeleton in <c>3-progress</c>). Wraps <c>Directory.Delete</c>
    /// inside the layer so the architecture test stays green at every
    /// other call site.
    /// </summary>
    TaskMutationResult DeleteLaneFolder(string watchPath, string lane, string slug);

    /// <summary>
    /// Write a small text file (placard, reason note) into a job folder
    /// addressed by <paramref name="jobId"/>. The file name is
    /// constrained to a single segment (no <c>..</c>, no path
    /// separators); the layer rejects unsafe values.
    /// </summary>
    bool WriteJobTextFile(string jobId, string? watchPath, string fileName, string content);
}
