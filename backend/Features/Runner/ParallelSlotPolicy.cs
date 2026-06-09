namespace OrchestratorApi.Services.Runner;

/// <summary>
/// ADR-0052 parallelisability facts the orchestrator-prep step stores on a task
/// and the runner's pick-gate reads when a slot frees. <see cref="Exclusive"/>
/// marks a too-big / cross-cutting task that must run alone (the rare
/// exception); <see cref="PredictedScope"/> is the set of repo-relative path
/// prefixes the task is expected to touch, used to prove two tasks are disjoint
/// before admitting them concurrently. An empty scope is treated as "unknown",
/// which the pick-gate handles conservatively (see <see cref="ParallelSlotPolicy"/>).
/// </summary>
public sealed record TaskParallelism(bool Exclusive, IReadOnlyList<string> PredictedScope)
{
    /// <summary>
    /// True for a read-only task mode (planning / research). A read-only task
    /// writes no files, so it has no predicted scope and can never collide with
    /// a running task - the pick-gate admits it as parallel-ok without any scope
    /// computation. It still counts against the slot budget like any other task.
    /// </summary>
    public bool ReadOnly { get; init; }

    /// <summary>Default: parallelisable, scope unknown.</summary>
    public static readonly TaskParallelism Default = new(false, Array.Empty<string>());

    /// <summary>A read-only (planning / research) task: never exclusive, no scope.</summary>
    public static readonly TaskParallelism ReadOnlyTask = new(false, Array.Empty<string>()) { ReadOnly = true };
}

/// <summary>A task currently occupying a runner slot, with its parallelism facts.</summary>
public sealed record RunningTask(string TaskId, TaskParallelism Parallelism);

public enum SlotDecision
{
    /// <summary>Admit the candidate into a free slot to run alongside the others.</summary>
    Admit,
    /// <summary>Hold the candidate; it must wait (no slot, conflict, or a running exclusive).</summary>
    Serialize,
    /// <summary>Admit the candidate but as the only task (it is exclusive and nothing else runs).</summary>
    RunExclusive,
}

/// <summary>Result of one pick-gate evaluation, with a human-readable rationale for the timeline.</summary>
public sealed record SlotAdmission(SlotDecision Decision, string Reason)
{
    public bool Admitted => Decision is SlotDecision.Admit or SlotDecision.RunExclusive;
}

/// <summary>
/// ADR-0052 §5 pick-gate + slot accounting, as pure logic. The runner owns the
/// N slots; this policy decides, deterministically, whether a candidate task may
/// take a free slot given what is already running. The single
/// <c>_activeJobId</c> latch of the sequential runner is the
/// <c>maxParallelism == 1</c> special case: one task is admitted, every further
/// candidate serializes until it drains - byte-for-byte today's behaviour.
///
/// <para>
/// Rules, in order: (1) no free slot -&gt; serialize; (2) an exclusive task is
/// running -&gt; serialize everyone; (3) the candidate is exclusive -&gt; run
/// alone if the project is idle, else wait for it to drain; (4) scope overlap
/// with any running task -&gt; serialize (no cross-talk); (5) otherwise admit as
/// parallel-ok. Scope comparison is path-prefix based and conservative: an
/// unknown (empty) scope cannot be proven disjoint, so it serializes rather than
/// risk two agents writing the same files.
/// </para>
/// </summary>
public static class ParallelSlotPolicy
{
    /// <summary>Clamp a configured parallelism to the valid <c>&gt;= 1</c> range.</summary>
    public static int ClampMax(int maxParallelism) => maxParallelism < 1 ? 1 : maxParallelism;

    /// <summary>How many slots are still free for new admissions.</summary>
    public static int FreeSlots(int maxParallelism, int occupied)
        => Math.Max(0, ClampMax(maxParallelism) - Math.Max(0, occupied));

    /// <summary>
    /// Decide whether <paramref name="candidate"/> may take a slot right now,
    /// given the <paramref name="running"/> tasks and the project's
    /// <paramref name="maxParallelism"/>.
    /// </summary>
    public static SlotAdmission Decide(
        string candidateId,
        TaskParallelism candidate,
        IReadOnlyList<RunningTask> running,
        int maxParallelism)
    {
        var max = ClampMax(maxParallelism);
        running ??= Array.Empty<RunningTask>();
        candidate ??= TaskParallelism.Default;

        if (running.Count >= max)
            return new SlotAdmission(SlotDecision.Serialize,
                $"no free slot ({running.Count}/{max} occupied)");

        var exclusiveRunning = running.FirstOrDefault(r => r.Parallelism.Exclusive);
        if (exclusiveRunning != null)
            return new SlotAdmission(SlotDecision.Serialize,
                $"exclusive task '{exclusiveRunning.TaskId}' is running");

        // Read-only modes (planning / research) write no files, so there is no
        // scope to prove disjoint - admit as parallel-ok and skip the scope loop
        // below. The free-slot check above already enforced the quota.
        if (candidate.ReadOnly)
            return new SlotAdmission(SlotDecision.Admit,
                "parallel-ok: read-only task (no file scope)");

        if (candidate.Exclusive)
        {
            return running.Count == 0
                ? new SlotAdmission(SlotDecision.RunExclusive, "exclusive: runs alone")
                : new SlotAdmission(SlotDecision.Serialize,
                    $"exclusive: waits for {running.Count} running task(s) to drain");
        }

        foreach (var r in running)
        {
            var conflict = FirstScopeConflict(candidate.PredictedScope, r.Parallelism.PredictedScope);
            if (conflict != null)
                return new SlotAdmission(SlotDecision.Serialize,
                    $"scope conflict with '{r.TaskId}' on '{conflict}'");
        }

        return new SlotAdmission(SlotDecision.Admit,
            running.Count == 0
                ? "parallel-ok: first slot"
                : $"parallel-ok: disjoint from {running.Count} running task(s)");
    }

    /// <summary>
    /// Returns the first overlapping path (or the sentinel <c>unknown-scope</c>)
    /// when two predicted scopes cannot be proven disjoint, else null. An empty
    /// scope on either side is unknown and conservatively conflicts.
    /// </summary>
    public static string? FirstScopeConflict(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a == null || a.Count == 0 || b == null || b.Count == 0) return "unknown-scope";
        foreach (var pa in a)
        {
            foreach (var pb in b)
            {
                if (PathsOverlap(pa, pb))
                    return NormalizePath(pa).Length <= NormalizePath(pb).Length ? NormalizePath(pa) : NormalizePath(pb);
            }
        }
        return null;
    }

    private static bool PathsOverlap(string x, string y)
    {
        var nx = NormalizePath(x);
        var ny = NormalizePath(y);
        if (nx.Length == 0 || ny.Length == 0) return true; // an empty entry is unknown
        return nx == ny
            || nx.StartsWith(ny + "/", StringComparison.Ordinal)
            || ny.StartsWith(nx + "/", StringComparison.Ordinal);
    }

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return string.Empty;
        return p.Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();
    }
}
