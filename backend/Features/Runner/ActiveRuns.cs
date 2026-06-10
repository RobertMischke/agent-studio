using System.Collections.Concurrent;

namespace AgentStudio.Runner;

/// <summary>
/// One in-flight coding run's claim state. At <c>MaxParallelism == 1</c> there
/// is at most one of these; the registry (<see cref="ActiveRuns"/>) generalizes
/// to N slots (ADR-0052 slice 2) without the caller re-deriving run state.
///
/// <para>
/// Construction is two-stage: <see cref="CliType"/> is only known once the
/// planner has resolved the CLI, so it is settable and stamped right after the
/// claim. The remaining fields are fixed at claim time.
/// </para>
/// </summary>
internal sealed class ActiveRun
{
    public required string JobId { get; init; }
    /// <summary>CLI driving this run; null between claim and CLI resolution.</summary>
    public string? CliType { get; set; }
    public RunIntent Intent { get; init; }
    public string? Followup { get; init; }
    public RunPlan? Plan { get; init; }
    public int ReissueAttempt { get; init; }

    /// <summary>
    /// ADR-0052 slice 2: parallelisability facts (exclusive / predicted scope /
    /// read-only) so <see cref="ParallelSlotPolicy"/> can prove this run disjoint
    /// from a candidate before a second slot is admitted. Default = unknown scope.
    /// </summary>
    public TaskParallelism Parallelism { get; set; } = TaskParallelism.Default;

    /// <summary>
    /// Isolated git worktree this run executes in when in a parallel slot
    /// (MaxParallelism &gt; 1). Null for the sequential (max==1) path, which runs
    /// in the project's main checkout exactly as before.
    /// </summary>
    public string? WorktreePath { get; set; }

    /// <summary>The ephemeral <c>task/&lt;id&gt;</c> branch backing the worktree, when isolated.</summary>
    public string? Branch { get; set; }

    /// <summary>
    /// True when this run RE-USED an existing task worktree (resume / reissue /
    /// recovery) rather than freshly cutting one. A recorded CLI session may only
    /// be resumed when its cwd matches this run's working directory: a reused
    /// worktree carries the same cwd the session was born in, so resume is safe; a
    /// fresh cut means the prior session lived in a different directory (the old
    /// main checkout) and must NOT be resumed (it would hang).
    /// </summary>
    public bool WorktreeReused { get; set; }

    /// <summary>
    /// Main-checkout git status captured immediately before an isolated run
    /// starts. Used as a containment guard: worktree runs may mutate their
    /// worktree only, never the shared checkout.
    /// </summary>
    public string? MainCheckoutStatusBefore { get; set; }

    /// <summary>True when this run is isolated in its own worktree (parallel slot).</summary>
    public bool IsWorktreeRun => !string.IsNullOrEmpty(WorktreePath);
}

/// <summary>
/// Single source of truth for active coding-run slots + the admit latch. This
/// replaces the former scattered <c>_active*</c> scalar fields on
/// <c>ProjectRunner</c> so that going from one slot to N is a localized change
/// HERE rather than across ~30 call sites (ADR-0001 latch → ADR-0052 slots).
///
/// <para>
/// At <c>MaxParallelism == 1</c> the registry holds at most one entry and every
/// helper behaves byte-for-byte like the old single-field latch:
/// <see cref="HasFreeSlot"/>(1) ⇔ <c>_activeJobId == null</c>,
/// <see cref="Count"/> ⇔ <c>(_activeJobId != null ? 1 : 0)</c>,
/// <see cref="Single"/> ⇔ the one active run.
/// </para>
/// </summary>
internal sealed class ActiveRuns
{
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new(StringComparer.Ordinal);

    /// <summary>Number of occupied slots.</summary>
    public int Count => _runs.Count;

    /// <summary>True when another run may be admitted under <paramref name="maxParallelism"/>.</summary>
    public bool HasFreeSlot(int maxParallelism) => _runs.Count < ParallelSlotPolicy.ClampMax(maxParallelism);

    public bool Contains(string jobId) => _runs.ContainsKey(jobId);

    public ActiveRun? Get(string jobId) => _runs.TryGetValue(jobId, out var r) ? r : null;

    public bool TryGet(string jobId, out ActiveRun? run) => _runs.TryGetValue(jobId, out run);

    /// <summary>
    /// The single active run, or null when idle. Valid usage while
    /// <c>MaxParallelism == 1</c>; multi-slot callers use <see cref="Snapshot"/>
    /// or address a run by job id.
    /// </summary>
    public ActiveRun? Single => _runs.Values.FirstOrDefault();

    /// <summary>Job id of the single active run, or null when idle.</summary>
    public string? SingleJobId => _runs.Keys.FirstOrDefault();

    /// <summary>Snapshot of all active runs (stable copy for iteration).</summary>
    public IReadOnlyCollection<ActiveRun> Snapshot() => _runs.Values.ToArray();

    /// <summary>
    /// The currently-running tasks as <see cref="RunningTask"/> facts for
    /// <see cref="ParallelSlotPolicy.Decide"/> — lets the pick-gate prove a
    /// candidate disjoint from everything already in a slot.
    /// </summary>
    public IReadOnlyList<RunningTask> RunningTasks()
        => _runs.Values.Select(r => new RunningTask(r.JobId, r.Parallelism ?? TaskParallelism.Default)).ToArray();

    /// <summary>Find the active run whose job key matches, or null.</summary>
    public ActiveRun? ByJobKey(Func<string, string> jobKeyOf, string jobKey)
    {
        foreach (var r in _runs.Values)
            if (string.Equals(jobKeyOf(r.JobId), jobKey, StringComparison.Ordinal)) return r;
        return null;
    }

    /// <summary>
    /// Claim a slot for <paramref name="run"/>. Returns false when a run with the
    /// same job id is already claimed (defensive; the pick-gate already enforces
    /// free-slot before calling).
    /// </summary>
    public bool TryClaim(ActiveRun run) => _runs.TryAdd(run.JobId, run);

    /// <summary>Release the slot held by <paramref name="jobId"/>; returns the removed run or null.</summary>
    public ActiveRun? Release(string jobId) => _runs.TryRemove(jobId, out var r) ? r : null;
}
