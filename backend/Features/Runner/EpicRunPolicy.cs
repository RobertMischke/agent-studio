
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

    /// <summary>
    /// Lane a finished planning run belongs in, given whether its plan was
    /// valid. A planning run is source-read-only: it produces child cards, so
    /// it never carries a Result-SHA. Without one no ReviewAttempt can ever be
    /// minted (<c>AttemptAuthorityService.CreateReviewAttempt</c> requires a
    /// non-empty <c>ExpectedResultSha</c> matching the run), so an Epic parked
    /// in <see cref="TaskStates.AutoReview"/> waits forever on a code review of
    /// a subject that does not exist - the card is <c>mode=coding</c>, so the
    /// report-only exception in the decision engine does not reach it either.
    /// A valid plan is a finished delivery whose evidence is the child set, and
    /// accepting a finished delivery is what <see cref="TaskStates.HumanReview"/>
    /// is for. An invalid plan is already recovered to
    /// <see cref="TaskStates.Backlog"/> by
    /// <see cref="EpicDecompositionLifecycle"/>, so naming the same lane here
    /// keeps the caller's move a no-op instead of dragging the Epic back out.
    /// </summary>
    public static string PlanningCompletionLane(bool decompositionValid) =>
        decompositionValid ? TaskStates.HumanReview : TaskStates.Backlog;
}
