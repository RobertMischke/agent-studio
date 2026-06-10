
namespace AgentStudio.Runner;

/// <summary>
/// Pure decision for Way 3's non-deterministic half: should a single CLI
/// invocation be treated as an epic planning / decomposition run rather than a
/// normal coding run? An epic card runs a planning step (the agent authors a
/// sub-task list) on a fresh start, but a user continue on an epic is the user
/// steering the existing plan, not a re-decomposition. Both halves of the
/// runner lifecycle gate on this same question - RunCliAsync (which swaps the
/// prompt template) and OnCliFinishedAsync (which parses the plan and creates
/// the sub-tasks) - so owning it in one named, testable place keeps the two
/// from drifting. No I/O, no DI (mirrors <see cref="RunCompletionPolicy"/>).
/// </summary>
public static class EpicRunPolicy
{
    /// <summary>
    /// True when the card is an epic and the invocation is a fresh start
    /// (ManualStart / AutoPickup), i.e. a planning/decomposition run. A
    /// <see cref="RunIntent.UserContinue"/> on an epic is steering, not
    /// planning, so it stays on the normal coding path.
    /// </summary>
    public static bool IsPlanningRun(string? kind, RunIntent intent) =>
        TaskKinds.IsEpic(kind) && intent != RunIntent.UserContinue;
}
