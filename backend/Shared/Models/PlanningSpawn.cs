namespace AgentStudio.Shared;

/// <summary>
/// AGT-2069 — one follow-up card a planning task produced, projected for the
/// planning task's board card and detail header. Sourced read-time from the
/// source task's <c>.metadata/spawned-tasks.jsonl</c> spawn ledger (AGT-2028)
/// so the planning task can render "spawnt: AGT-xxxx" chips (AGT-2050
/// microcards) without a second lookup. Never persisted to <c>task.json</c>.
/// </summary>
public record PlanningSpawnRef
{
    /// <summary>Display key of the spawned follow-up card (e.g. <c>AGT-2067</c>), when resolvable.</summary>
    public string? TargetKey { get; init; }
    /// <summary>Slug id of the spawned card.</summary>
    public string? TargetJobId { get; init; }
    /// <summary>Human-facing project name the follow-up was created in.</summary>
    public string? TargetProject { get; init; }
    /// <summary>Short reason the follow-up was judged worth spawning.</summary>
    public string? Reason { get; init; }
    /// <summary>UTC instant the spawn was recorded.</summary>
    public DateTime At { get; init; }
}

/// <summary>
/// AGT-2069 — read-time spawn-visibility + spawn-contract projection for a
/// planning task (<c>mode == planning</c>). Answers two questions the operator
/// wants answered "total krass" on the card:
/// <list type="bullet">
/// <item>Did this planning run produce follow-up cards? (<see cref="Spawned"/>)</item>
/// <item>If not, was that a deliberate "no implementation" call?
/// (<see cref="NoFollowUpDeclared"/>)</item>
/// </list>
/// The spawn contract (<see cref="ContractSatisfied"/>) is the guard against the
/// AGT-1915 trap — a planning task approved plan-only with no work ever created.
/// Present only on planning tasks; null everywhere else. Never persisted to
/// <c>task.json</c>; folded on by <c>TaskEndpointHelpers.WithRuntime</c>.
/// </summary>
public record PlanningSpawnSummary
{
    /// <summary>Follow-up cards this planning task spawned (AGT-2028 ledger). Empty when none.</summary>
    public IReadOnlyList<PlanningSpawnRef> Spawned { get; init; } = [];

    /// <summary>Convenience count of <see cref="Spawned"/>.</summary>
    public int SpawnedCount => Spawned.Count;

    /// <summary>
    /// True when the operator explicitly declared "bewusst keine Umsetzung"
    /// (deliberately no follow-up work) for this planning task, recorded via
    /// <c>POST /api/tasks/{id}/planning-closure</c> into
    /// <c>.metadata/planning-closure.json</c>.
    /// </summary>
    public bool NoFollowUpDeclared { get; init; }

    /// <summary>Optional operator note captured with the no-follow-up declaration.</summary>
    public string? NoFollowUpReason { get; init; }

    /// <summary>UTC instant the no-follow-up declaration was made; null when none.</summary>
    public DateTime? DeclaredAt { get; init; }

    /// <summary>
    /// The spawn contract: a planning task is completable only when it either
    /// produced at least one follow-up card OR carries a deliberate
    /// no-follow-up declaration. See <see cref="PlanningCompletionGate"/>.
    /// </summary>
    public bool ContractSatisfied => PlanningCompletionGate.IsSatisfied(SpawnedCount, NoFollowUpDeclared);
}

/// <summary>
/// AGT-2069 — pure, dependency-free spawn-contract completion gate for planning
/// tasks. A planning run is only "done" once it has either spawned follow-up
/// cards (the Task-Spawner mechanic, AGT-2028) or the operator has explicitly
/// declared no implementation is intended. This is the load-bearing guard
/// against the AGT-1915 trap (plan-only approval with no work ever created).
///
/// <para>Lives in the Shared library so it is unit-testable without the web
/// host, and shared by the read-time projection
/// (<see cref="PlanningSpawnSummary.ContractSatisfied"/>), the accept-dialog
/// warning, and any callers that need the same yes/no answer.</para>
/// </summary>
public static class PlanningCompletionGate
{
    /// <summary>The gate applies only to planning-mode tasks; every other mode is ungated.</summary>
    public static bool Applies(string? mode) =>
        TaskModes.Normalize(mode) == TaskModes.Planning;

    /// <summary>
    /// The spawn contract is satisfied when at least one follow-up card was
    /// spawned OR the operator declared "no follow-up intended".
    /// </summary>
    public static bool IsSatisfied(int spawnedCount, bool noFollowUpDeclared) =>
        spawnedCount > 0 || noFollowUpDeclared;

    /// <summary>
    /// True when accepting <paramref name="mode"/> should surface the AGT-1915
    /// warning: it is a planning task and the contract is not yet satisfied.
    /// </summary>
    public static bool ShouldWarnOnAccept(string? mode, int spawnedCount, bool noFollowUpDeclared) =>
        Applies(mode) && !IsSatisfied(spawnedCount, noFollowUpDeclared);
}
