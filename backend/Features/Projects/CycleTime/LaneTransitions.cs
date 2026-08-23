namespace AgentStudio.Projects;

/// <summary>One lane change of a task, enriched with dwell, actor class, and cause.</summary>
public sealed record TaskLaneTransition(
    DateTime At,
    string From,
    string To,
    /// <summary><c>forward</c>, <c>backward</c>, or <c>lateral</c> (same level, e.g. Escalated to Human Review).</summary>
    string Direction,
    /// <summary>Seconds the task spent in <see cref="From"/> before this move; null when the stay start is unknown.</summary>
    double? DwellSeconds,
    /// <summary>Raw ledger actor (for example <c>remote-runner:agent-runner-01</c> or <c>human:operator</c>).</summary>
    string Actor,
    /// <summary><c>runner</c>, <c>review</c>, <c>human</c>, <c>orchestrator</c>, <c>system</c>, or <c>external</c>.</summary>
    string ActorKind,
    /// <summary>One of <see cref="TransitionCauses"/>.</summary>
    string Cause,
    /// <summary>Free-text detail from the ledger: quality-loop cause, integration outcome, operator reason.</summary>
    string? CauseDetail,
    string? AttemptId,
    /// <summary>
    /// For a backward move: seconds until the task next reached the level it
    /// fell from (or higher), i.e. the rework the bounce cost. Null when the
    /// task never got back there inside the recorded history.
    /// </summary>
    double? ReworkSeconds);

public static class TransitionDirections
{
    public const string Forward = "forward";
    public const string Backward = "backward";
    public const string Lateral = "lateral";
}

/// <summary>
/// Closed cause vocabulary. Backward causes answer "why did the task fall back";
/// forward causes name the normal pipeline hand-off that moved it on.
/// </summary>
public static class TransitionCauses
{
    // forward / lateral
    public const string Promoted = "promoted";
    public const string Claimed = "claimed";
    public const string Delivered = "delivered";
    public const string ExternalCompletion = "external-completion";
    public const string ReviewVerdict = "review-verdict";
    public const string Escalated = "escalated";
    public const string OperatorDecision = "operator-decision";
    public const string Accepted = "accepted";
    public const string Archived = "archived";
    public const string OperatorMove = "operator-move";
    public const string SystemMove = "system-move";

    // backward
    public const string GateFailure = "gate-failure";
    public const string QualityLoop = "quality-loop";
    public const string IntegrationRecovery = "integration-recovery";
    public const string ReviewInfrastructure = "review-infrastructure";
    public const string LeaseRecovery = "lease-recovery";
    public const string ClaimEnvironmentRetry = "claim-environment-retry";
    /// <summary>A runner or the system returned a claimed task to Ready/Preparation without a cause row (pick reverted, no run).</summary>
    public const string RunnerRequeue = "runner-requeue";
    public const string AcceptanceIntegrationFailed = "acceptance-integration-failed";
    public const string CompletedReopen = "completed-reopen";
    public const string EscalationRequeue = "escalation-requeue";
    public const string OperatorRequeue = "operator-requeue";
    public const string Unclassified = "unclassified";

    public static string Label(string cause) => cause switch
    {
        Promoted => "Promoted",
        Claimed => "Claimed by runner",
        Delivered => "Delivered to review",
        ExternalCompletion => "External completion",
        ReviewVerdict => "Review verdict",
        Escalated => "Escalated",
        OperatorDecision => "Operator decision",
        Accepted => "Accepted",
        Archived => "Archived",
        OperatorMove => "Operator move",
        SystemMove => "System move",
        GateFailure => "Build/test gate failed",
        QualityLoop => "Quality loop reopened",
        IntegrationRecovery => "Integration recovery round",
        ReviewInfrastructure => "Review infrastructure",
        LeaseRecovery => "Runner lease recovery",
        ClaimEnvironmentRetry => "Claim environment retry",
        RunnerRequeue => "Runner returned the task",
        AcceptanceIntegrationFailed => "Acceptance integration failed",
        CompletedReopen => "Reopened after completion",
        EscalationRequeue => "Requeued from escalation",
        OperatorRequeue => "Operator requeue",
        Unclassified => "Unclassified",
        _ => cause,
    };
}

public static class TransitionActorKinds
{
    public const string Runner = "runner";
    public const string Review = "review";
    public const string Human = "human";
    public const string Orchestrator = "orchestrator";
    public const string System = "system";
    public const string External = "external";
}

/// <summary>Lane ordering used for direction and rework. Sub-lanes share the level of their parent lane.</summary>
public static class LaneOrder
{
    public static readonly IReadOnlyList<string> Canonical =
    [
        TaskStates.Backlog, TaskStates.Preparation, TaskStates.OrchestratorPrep, TaskStates.Ready,
        TaskStates.Progress, TaskStates.FailedPickup, TaskStates.CodeNotComplete, TaskStates.AutoReview,
        TaskStates.HumanReview, TaskStates.Escalated, TaskStates.Completed, TaskStates.Archive,
    ];

    public static int Level(string? lane) => lane switch
    {
        TaskStates.Backlog => 0,
        TaskStates.Preparation or TaskStates.OrchestratorPrep => 1,
        TaskStates.Ready => 2,
        TaskStates.Progress or TaskStates.FailedPickup or TaskStates.CodeNotComplete => 3,
        TaskStates.AutoReview => 4,
        TaskStates.HumanReview or TaskStates.Escalated => 5,
        TaskStates.Completed => 6,
        TaskStates.Archive => 7,
        _ => -1,
    };

    public static int CanonicalIndex(string lane)
    {
        for (var i = 0; i < Canonical.Count; i++)
            if (string.Equals(Canonical[i], lane, StringComparison.Ordinal)) return i;
        return Canonical.Count;
    }
}

/// <summary>
/// Extracts the ordered lane transitions of a task from its ledger and classifies
/// every move. The classification reads the cause rows the platform writes next
/// to a lane change (quality-loop reopen, operator requeue, integration recovery,
/// review-attempt supersession) within a short window, then falls back to the
/// actor and the lane pair.
/// </summary>
public static class LaneTransitionExtractor
{
    private static readonly TimeSpan CauseWindow = TimeSpan.FromSeconds(120);

    public static IReadOnlyList<TaskLaneTransition> Extract(IReadOnlyList<TimelineEvent> sortedLedger, DateTime? createdAt)
    {
        var changes = new List<(int Index, TimelineEvent Event)>();
        for (var i = 0; i < sortedLedger.Count; i++)
            if (sortedLedger[i].Kind == TimelineEventKinds.LaneChanged) changes.Add((i, sortedLedger[i]));
        if (changes.Count == 0) return [];

        var result = new List<TaskLaneTransition>(changes.Count);
        DateTime? stayStart = createdAt;
        string? previousTo = null;
        for (var c = 0; c < changes.Count; c++)
        {
            var (index, evt) = changes[c];
            var from = Detail(evt, "from") ?? previousTo ?? string.Empty;
            var to = Detail(evt, "to") ?? string.Empty;
            var fromLevel = LaneOrder.Level(from);
            var toLevel = LaneOrder.Level(to);
            var direction = fromLevel < 0 || toLevel < 0 || fromLevel == toLevel
                ? TransitionDirections.Lateral
                : toLevel < fromLevel ? TransitionDirections.Backward : TransitionDirections.Forward;

            double? dwell = null;
            if (stayStart is not null)
            {
                // A stay start that is unknown because the previous row lacked
                // 'to' keeps dwell null instead of inventing a zero.
                dwell = Math.Max(0, (evt.Ts - stayStart.Value).TotalSeconds);
            }

            var near = Nearby(sortedLedger, index, evt.Ts);
            var (cause, detail) = Classify(evt, from, to, direction, near);
            var actor = evt.Actor ?? string.Empty;

            double? rework = null;
            if (direction == TransitionDirections.Backward)
            {
                for (var k = c + 1; k < changes.Count; k++)
                {
                    var later = changes[k].Event;
                    if (LaneOrder.Level(Detail(later, "to")) >= fromLevel)
                    {
                        rework = Math.Max(0, (later.Ts - evt.Ts).TotalSeconds);
                        break;
                    }
                }
            }

            result.Add(new TaskLaneTransition(
                evt.Ts, from, to, direction, dwell is null ? null : Math.Round(dwell.Value, 1), actor, ActorKind(actor),
                cause, detail, Detail(evt, "attemptId"), rework is null ? null : Math.Round(rework.Value, 1)));

            stayStart = evt.Ts;
            previousTo = Detail(evt, "to");
        }
        return result;
    }

    public static string ActorKind(string? actor)
    {
        if (string.IsNullOrWhiteSpace(actor)) return TransitionActorKinds.System;
        var head = actor.Split(':')[0].Trim().ToLowerInvariant();
        if (head.StartsWith("human", StringComparison.Ordinal)) return TransitionActorKinds.Human;
        if (head.StartsWith("remote-review", StringComparison.Ordinal)) return TransitionActorKinds.Review;
        if (head.StartsWith("remote-runner", StringComparison.Ordinal)
            || head.StartsWith("remote-claim", StringComparison.Ordinal)
            || head == "runner" || head == "agent")
            return TransitionActorKinds.Runner;
        if (head == "orchestrator" || head == "quality-loop") return TransitionActorKinds.Orchestrator;
        if (head == "external") return TransitionActorKinds.External;
        return TransitionActorKinds.System;
    }

    private static List<TimelineEvent> Nearby(IReadOnlyList<TimelineEvent> ledger, int index, DateTime at)
    {
        var near = new List<TimelineEvent>();
        for (var j = index - 1; j >= 0 && at - ledger[j].Ts <= CauseWindow; j--)
            if (ledger[j].Kind != TimelineEventKinds.LaneChanged) near.Add(ledger[j]);
        for (var j = index + 1; j < ledger.Count && ledger[j].Ts - at <= CauseWindow; j++)
            if (ledger[j].Kind != TimelineEventKinds.LaneChanged) near.Add(ledger[j]);
        return near.OrderBy(e => (e.Ts - at).Duration()).ToList();
    }

    private static (string Cause, string? Detail) Classify(
        TimelineEvent evt, string from, string to, string direction, List<TimelineEvent> near)
    {
        var actorKind = ActorKind(evt.Actor);
        var actorHead = (evt.Actor ?? string.Empty).Split(':')[0].ToLowerInvariant();
        var reason = Detail(evt, "reason");

        if (direction == TransitionDirections.Backward)
        {
            var reopened = near.FirstOrDefault(e => e.Kind == TimelineEventKinds.QualityLoopReopened);
            if (reopened is not null)
            {
                var cause = Detail(reopened, "cause") ?? "unknown";
                return string.Equals(cause, "build-test-gate-fail", StringComparison.OrdinalIgnoreCase)
                    ? (TransitionCauses.GateFailure, cause)
                    : (TransitionCauses.QualityLoop, cause);
            }
            var recovery = near.FirstOrDefault(e => e.Kind == TimelineEventKinds.IntegrationRecoveryQueued);
            var integrationFailure = near.FirstOrDefault(e => e.Kind == TimelineEventKinds.IntegrationFailed);
            if (recovery is not null || (integrationFailure is not null && from != TaskStates.Completed))
            {
                var outcome = Detail(integrationFailure, "outcome") ?? Detail(recovery, "outcome") ?? "recovery-queued";
                if (from == TaskStates.HumanReview || from == TaskStates.Escalated)
                {
                    // Operator requeue after a failed acceptance integration: keep
                    // the operator cause, carry the integration outcome as detail.
                    if (actorKind == TransitionActorKinds.Human || near.Any(e => e.Kind == TimelineEventKinds.OperatorRequeued))
                        return (from == TaskStates.Escalated ? TransitionCauses.EscalationRequeue : TransitionCauses.OperatorRequeue,
                            $"after integration {outcome}");
                }
                return (TransitionCauses.IntegrationRecovery, outcome);
            }
            if (near.Any(e => e.Kind is TimelineEventKinds.ReviewAttemptSuperseded
                    or TimelineEventKinds.ReviewInfrastructureRepeatDiagnosed)
                || (reason is not null && reason.Contains("ReviewInfra", StringComparison.OrdinalIgnoreCase)))
                return (TransitionCauses.ReviewInfrastructure, reason);
            if (actorHead.StartsWith("remote-runner-lease-recovery", StringComparison.Ordinal))
                return (TransitionCauses.LeaseRecovery, null);
            if (actorHead.StartsWith("remote-claim-environment-retry", StringComparison.Ordinal))
                return (TransitionCauses.ClaimEnvironmentRetry, null);
            if (from == TaskStates.Completed)
            {
                if (integrationFailure is not null && (to == TaskStates.HumanReview || to == TaskStates.Escalated))
                    return (TransitionCauses.AcceptanceIntegrationFailed, Detail(integrationFailure, "outcome"));
                return (TransitionCauses.CompletedReopen, reason);
            }
            var requeue = near.FirstOrDefault(e => e.Kind == TimelineEventKinds.OperatorRequeued);
            if (actorKind == TransitionActorKinds.Human || requeue is not null)
            {
                var detail = reason ?? Detail(requeue, "reason");
                if (from == TaskStates.Escalated) return (TransitionCauses.EscalationRequeue, detail);
                if (from == TaskStates.HumanReview || from == TaskStates.AutoReview) return (TransitionCauses.OperatorRequeue, detail);
                return (TransitionCauses.OperatorMove, detail);
            }
            if (LaneOrder.Level(from) == 3 && LaneOrder.Level(to) <= 2
                && actorKind is TransitionActorKinds.Runner or TransitionActorKinds.System)
            {
                // A claimed task handed back without any cause row: the pickup was
                // reverted or the run never materialized (AGENTS: pick-reverted-no-run).
                return (TransitionCauses.RunnerRequeue, evt.Actor);
            }
            return (TransitionCauses.Unclassified, evt.Actor);
        }

        // forward and lateral
        if (to == TaskStates.Progress || to == TaskStates.FailedPickup || to == TaskStates.CodeNotComplete)
            return actorKind == TransitionActorKinds.Human ? (TransitionCauses.OperatorMove, reason) : (TransitionCauses.Claimed, null);
        if (to == TaskStates.AutoReview && (from == TaskStates.Progress || from == TaskStates.Ready
                                            || from == TaskStates.FailedPickup || from == TaskStates.CodeNotComplete))
            return actorKind == TransitionActorKinds.Human ? (TransitionCauses.OperatorMove, reason) : (TransitionCauses.Delivered, null);
        if (to == TaskStates.Escalated)
            return (TransitionCauses.Escalated, near.FirstOrDefault(e => e.Kind == TimelineEventKinds.OrchestratorEscalated)?.Summary ?? reason);
        if (to == TaskStates.HumanReview)
        {
            if (near.Any(e => e.Kind == TimelineEventKinds.ExternalCompletion)) return (TransitionCauses.ExternalCompletion, null);
            if (from == TaskStates.Escalated) return (TransitionCauses.OperatorDecision, reason);
            if (actorKind is TransitionActorKinds.Review or TransitionActorKinds.System or TransitionActorKinds.Orchestrator
                && from == TaskStates.AutoReview)
            {
                var verdict = near.FirstOrDefault(e => e.Kind == TimelineEventKinds.IntegrationFailed
                                                       || e.Kind == TimelineEventKinds.IntegrationSucceeded);
                return (TransitionCauses.ReviewVerdict, Detail(verdict, "outcome"));
            }
            if (actorKind == TransitionActorKinds.Human) return (TransitionCauses.OperatorMove, reason);
            return (TransitionCauses.SystemMove, reason);
        }
        if (to == TaskStates.Completed) return (TransitionCauses.Accepted, reason);
        if (to == TaskStates.Archive) return (TransitionCauses.Archived, reason);
        if (to == TaskStates.Ready || to == TaskStates.Preparation || to == TaskStates.OrchestratorPrep)
            return actorKind == TransitionActorKinds.Human ? (TransitionCauses.Promoted, reason) : (TransitionCauses.SystemMove, reason);
        return actorKind == TransitionActorKinds.Human ? (TransitionCauses.OperatorMove, reason) : (TransitionCauses.SystemMove, reason);
    }

    private static string? Detail(TimelineEvent? e, string key)
    {
        if (e?.Details is null) return null;
        return e.Details.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    }
}
