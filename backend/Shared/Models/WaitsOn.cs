namespace AgentStudio.Shared;

/// <summary>
/// AGT-2029 — read-time projection of a task's <b>waits-on</b> state, derived
/// from its <see cref="TaskReferences.DependsOn"/> edges against the whole
/// workspace (all projects, all lanes including the terminal 7-archive lane).
///
/// <para>"Waits-on" is the operator-facing name for the existing F34
/// <c>dependsOn</c> relation: a task waits on the tasks it depends on, and a
/// dependency is <b>fulfilled</b> once its target task reaches
/// <c>6-completed</c> or <c>7-archive</c>. This is the field the operator asked
/// for in AGT-2029 ("an jedem Task eine Information, auf die ich warte -
/// cross-project - auf der Karte sichtbar"); rather than duplicate the
/// dependsOn list, model, endpoint, validator and cross-project index that
/// already exist, this projection gives the existing relation scheduler teeth
/// and a card-renderable status.</para>
///
/// <para>Never persisted to <c>task.json</c>. Computed by the endpoint read
/// overlay (per card / detail) and, independently, by the runner pickup gate
/// (see <c>ProjectRunner</c>). Both go through <see cref="WaitsOnEvaluator"/> so
/// the card the operator sees and the scheduler decision stay in agreement.</para>
/// </summary>
public record WaitsOnStatus
{
    /// <summary>One entry per (de-duplicated, non-self) dependsOn key, in edge order.</summary>
    public List<WaitsOnItem> Items { get; init; } = [];

    /// <summary>
    /// True when at least one dependency is not yet fulfilled - because its
    /// target is unresolved (unknown / not yet created) or has not reached
    /// <c>6-completed</c>/<c>7-archive</c>. A blocked <c>2-ready</c> card is not
    /// auto-picked; it stays visibly "waiting" on the board rather than being
    /// silently skipped.
    /// </summary>
    public bool Blocked { get; init; }

    /// <summary>
    /// True when this task sits on a dependsOn cycle (A waits on B waits on A):
    /// a configuration error that can never be fulfilled. The runner reports it
    /// (structured log + this flag drives an error chip on the card) and skips
    /// the card instead of deadlocking. Cycles among existing keys are also
    /// rejected on write; a cycle that only closes once a not-yet-created key
    /// appears is caught here at runtime.
    /// </summary>
    public bool CycleDetected { get; init; }

    /// <summary>No dependsOn edges at all - the card renders no dependency chip.</summary>
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// One resolved (or unresolved) waits-on dependency. Carries enough of the
/// target task for the card chip to render its state and route to it without a
/// second lookup - including for targets in lanes the board snapshot omits
/// (e.g. an archived target), which is why resolution happens server-side over
/// an archive-inclusive snapshot rather than in the browser.
/// </summary>
public record WaitsOnItem
{
    /// <summary>The dependency key exactly as stored (e.g. <c>CAR-3</c>).</summary>
    public string Key { get; init; } = "";

    /// <summary>
    /// True when <see cref="Key"/> matched a real task in the workspace. False
    /// for an unknown key - a typo or a target the operator intends to create
    /// later (allowed on write as a warning, not a hard failure).
    /// </summary>
    public bool Resolved { get; init; }

    /// <summary>True when the target is resolved AND in <c>6-completed</c> or <c>7-archive</c>.</summary>
    public bool Fulfilled { get; init; }

    /// <summary>Target task's folder id (for navigation); null when unresolved.</summary>
    public string? TargetJobId { get; init; }

    /// <summary>Target task's short title (for the chip tooltip); null when unresolved.</summary>
    public string? TargetTitle { get; init; }

    /// <summary>Target task's lane state; null when unresolved.</summary>
    public string? TargetState { get; init; }

    /// <summary>Target task's watch path (for navigation); null when unresolved.</summary>
    public string? TargetWatchPath { get; init; }
}

/// <summary>
/// Pure, dependency-free evaluation of a task's waits-on state. Lives in the
/// Shared library so it is unit-testable without the web host and is shared by
/// the endpoint overlay and the runner pickup gate. Cross-project resolution is
/// implicit: the caller supplies a whole-workspace key map, and keys
/// (<c>&lt;ShortCode&gt;-&lt;seq&gt;</c>) are globally unique across projects.
/// </summary>
public static class WaitsOnEvaluator
{
    private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>A dependency is fulfilled once its target reaches completed or archive.</summary>
    public static bool IsFulfilledState(string? state) =>
        string.Equals(state, TaskStates.Completed, StringComparison.Ordinal)
        || string.Equals(state, TaskStates.Archive, StringComparison.Ordinal);

    /// <summary>
    /// Evaluates <paramref name="job"/>'s dependsOn edges.
    /// </summary>
    /// <param name="job">The task whose waits-on state is computed.</param>
    /// <param name="byKey">
    /// Whole-workspace key → task map (all projects, all lanes incl. archive),
    /// case-insensitive. Targets absent from this map are reported unresolved.
    /// </param>
    /// <param name="dependsOnGraph">
    /// key → its dependsOn targets, for cycle detection. Case-insensitive.
    /// </param>
    public static WaitsOnStatus Evaluate(
        TaskInfo job,
        IReadOnlyDictionary<string, TaskInfo> byKey,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOnGraph)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(byKey);
        ArgumentNullException.ThrowIfNull(dependsOnGraph);

        var self = (job.Key ?? "").Trim();
        var deps = job.References?.DependsOn ?? [];
        var items = new List<WaitsOnItem>();
        var seen = new HashSet<string>(KeyComparer);
        var blocked = false;

        foreach (var raw in deps)
        {
            var key = (raw ?? "").Trim();
            if (key.Length == 0) continue;
            // A self-edge can never gate the task and is rejected on write; skip
            // defensively so a stale self-edge on disk cannot self-block.
            if (self.Length > 0 && KeyComparer.Equals(key, self)) continue;
            if (!seen.Add(key)) continue;

            byKey.TryGetValue(key, out var target);
            var resolved = target != null;
            var fulfilled = resolved && IsFulfilledState(target!.State);
            if (!fulfilled) blocked = true;

            items.Add(new WaitsOnItem
            {
                Key = key,
                Resolved = resolved,
                Fulfilled = fulfilled,
                TargetJobId = target?.Id,
                TargetTitle = target?.Title,
                TargetState = target?.State,
                TargetWatchPath = target?.WatchPath,
            });
        }

        var cycle = SitsOnCycle(self, dependsOnGraph);
        return new WaitsOnStatus { Items = items, Blocked = blocked, CycleDetected = cycle };
    }

    /// <summary>
    /// True when <paramref name="start"/> can reach itself through dependsOn
    /// edges (a cycle passing through it). O(V+E) DFS; pre-existing cycles that
    /// do not pass through <paramref name="start"/> are pruned so traversal
    /// always terminates.
    /// </summary>
    public static bool SitsOnCycle(
        string? start,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> dependsOnGraph)
    {
        var self = (start ?? "").Trim();
        if (self.Length == 0) return false;
        if (!dependsOnGraph.TryGetValue(self, out var seedEdges) || seedEdges.Count == 0)
            return false;

        var onStack = new HashSet<string>(KeyComparer);
        var done = new HashSet<string>(KeyComparer);

        IEnumerable<string> Edges(string node) =>
            dependsOnGraph.TryGetValue(node, out var e) ? e : Array.Empty<string>();

        bool Dfs(string node)
        {
            onStack.Add(node);
            foreach (var next in Edges(node))
            {
                var n = (next ?? "").Trim();
                if (n.Length == 0) continue;
                if (KeyComparer.Equals(n, self)) return true;
                if (onStack.Contains(n) || done.Contains(n)) continue;
                if (Dfs(n)) return true;
            }
            onStack.Remove(node);
            done.Add(node);
            return false;
        }

        return Dfs(self);
    }
}
