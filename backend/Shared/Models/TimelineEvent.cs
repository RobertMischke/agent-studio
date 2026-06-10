namespace AgentStudio.Shared;

/// <summary>
/// One row in <c>logs/timeline.jsonl</c>. The unified per-task ledger that
/// records every event in the task's lifetime so the operator never has to
/// stitch together <c>session-events.jsonl</c>, <c>pipeline-execution.json</c>,
/// <c>[orchestrator]</c>/<c>[supervisor]</c> chat lines, and lane moves to
/// answer "what happened to this card?". See ADR-0049.
///
/// <para>
/// The schema is deliberately small and the <see cref="Kind"/> enum is
/// deliberately closed. New event kinds are added by extending the enum +
/// a writer + a test in the same commit; no free-form <c>kind</c> string,
/// no <c>extras</c> bag. The point of the ledger is to enforce discipline,
/// not to be a sink for arbitrary observability.
/// </para>
///
/// <para>
/// This is a *derived* surface. Each event is mirrored by a producer that
/// already owns its own canonical record (the runner already writes
/// session-events.jsonl, the pipeline already writes pipeline-execution.json,
/// etc.). The timeline writer is a thin teeing call next to those producers
/// so the union ledger stays a single grep target.
/// </para>
/// </summary>
public sealed record TimelineEvent
{
    public DateTime Ts { get; init; }
    public string Kind { get; init; } = "";

    /// <summary>
    /// Coarse attribution. One of <c>agent</c>, <c>orchestrator</c>,
    /// <c>quality-loop</c>, <c>system</c>, or <c>human:&lt;email&gt;</c>.
    /// Drives the per-event glyph and the filter chips in the FE.
    /// </summary>
    public string Actor { get; init; } = "system";

    /// <summary>
    /// Optional run correlator. Populated for events that belong to one
    /// CLI invocation (<c>agent_run_started</c>, <c>agent_run_finished</c>,
    /// <c>post_step_*</c>). Today the value is the captured-session-id of
    /// the run when known, falling back to the started-marker timestamp so
    /// reissues stay distinguishable in the FE.
    /// </summary>
    public string? RunId { get; init; }

    /// <summary>
    /// Optional path (relative to the job folder) to the artefact this
    /// event produced or references - <c>status.md</c>, <c>aspect-*.md</c>,
    /// the decision note. The FE expands a row to show this artefact
    /// inline. Null when the event has no body of its own.
    /// </summary>
    public string? PayloadRef { get; init; }

    /// <summary>One-line human-readable description. Renders directly in the FE row.</summary>
    public string Summary { get; init; } = "";

    /// <summary>
    /// Optional terse details bag. Use sparingly - prefer extending the
    /// closed schema with a typed event kind when a producer needs more
    /// than a one-liner. The dictionary is a JSON object on the wire.
    /// </summary>
    public Dictionary<string, string>? Details { get; init; }
}

/// <summary>
/// The closed set of timeline event kinds. Persisted as the <c>kind</c>
/// string in <c>timeline.jsonl</c>. When a new kind lands, add it here,
/// add a writer that emits it, and add a test that asserts the wire shape.
///
/// The list is closed by convention: <see cref="TimelineEvent.Kind"/> is a
/// string on disk so the file remains greppable and forward-compatible
/// with a reader that runs after a kind has been removed, but every
/// producer in this repo writes one of these constants.
/// </summary>
public static class TimelineEventKinds
{
    /// <summary>Task prompt was created on disk.</summary>
    public const string PromptCreated = "prompt_created";
    /// <summary>One CLI invocation started (start / continue / recovery).</summary>
    public const string AgentRunStarted = "agent_run_started";
    /// <summary>
    /// ADR-0052: the parallel pick-gate admitted this task into a runner slot.
    /// <see cref="TimelineEvent.Summary"/> carries the occupancy
    /// ("slot N/M") and the <c>ParallelSlotPolicy</c> rationale, so the
    /// Timeline shows the pick decision + slot belegung the moment a task is
    /// picked. At MaxParallelism == 1 this is the single sequential slot.
    /// </summary>
    public const string RunnerSlotAdmission = "runner_slot_admission";
    /// <summary>
    /// ADR-0052 multi-system follow-up: the Task Server granted, rejected, or
    /// released the fenced integration lease that serializes direct merges into
    /// a project's integration branch across runner machines.
    /// </summary>
    public const string IntegrationLease = "integration_lease";
    /// <summary>The CLI invocation ended; <see cref="TimelineEvent.Summary"/> carries the outcome.</summary>
    public const string AgentRunFinished = "agent_run_finished";
    /// <summary>A pipeline pre-step started (ADR-0045).</summary>
    public const string PreStepStarted = "pre_step_started";
    /// <summary>A pipeline pre-step finished.</summary>
    public const string PreStepFinished = "pre_step_finished";
    /// <summary>A pipeline post-step started (the four aspect runs in the standard pipeline).</summary>
    public const string PostStepStarted = "post_step_started";
    /// <summary>A pipeline post-step finished.</summary>
    public const string PostStepFinished = "post_step_finished";
    /// <summary>
    /// The orchestrator could not decide unattended and asked a human to
    /// take the wheel. The original card is escalated to
    /// <c>5e-escalated</c> via the human-review escalation funnel (the
    /// retired <c>1b-needs-human-review</c> lane previously held these); no
    /// wrapper card is spawned (ADR-0049).
    /// </summary>
    public const string OrchestratorEscalated = "orchestrator_escalated";
    /// <summary>The orchestrator emitted a STEER block (see OrchestratorReplyParser).</summary>
    public const string OrchestratorSteered = "orchestrator_steered";
    /// <summary>
    /// The orchestrator's auto-review pass judged the run genuinely done
    /// and promoted it forward (to human review). The positive terminal of
    /// the completion loop (ADR-0049 / ASS-566): the counterpart to
    /// <see cref="QualityLoopReopened"/> ("go again") and
    /// <see cref="OrchestratorEscalated"/> ("hand to a human"). Emitting it
    /// lets the Overview/Timeline surfaces show the verdict that closed the
    /// loop rather than re-deriving it from the decision journal.
    /// </summary>
    public const string OrchestratorVerdictAccepted = "orchestrator_verdict_accepted";
    /// <summary>
    /// A downstream quality check (auto-review, completed-lane audit)
    /// disagreed with a previous <see cref="AgentRunFinished"/>
    /// "claimed=done" verdict and reissued the run.
    /// </summary>
    public const string QualityLoopReopened = "quality_loop_reopened";
    /// <summary>A human review verdict was recorded.</summary>
    public const string HumanReviewDecided = "human_review_decided";
    /// <summary>The task's lane changed (any move).</summary>
    public const string LaneChanged = "lane_changed";
    /// <summary>
    /// An epic's planning/decomposition run (way 3) authored a sub-task list
    /// and the runner created those sub-tasks under the epic. The "Epic
    /// decomposition" step in the timeline/pipeline; <see cref="TimelineEvent.Summary"/>
    /// carries the created count and <see cref="TimelineEvent.Details"/> the
    /// target lane. Emitted by the runner, not the deterministic endpoint.
    /// </summary>
    public const string EpicDecomposed = "epic_decomposed";
    /// <summary>
    /// Another card was folded into this one via the consolidation API
    /// (sibling task). The merge endpoint mirrors the secondary's
    /// timeline events into the primary's ledger and emits one
    /// <c>merged_in</c> summary entry alongside.
    /// </summary>
    public const string MergedIn = "merged_in";
    /// <summary>
    /// A read-only task (planning / research) left a non-empty working-tree
    /// diff at run end. Read-only modes are supposed to produce only a report
    /// and touch no source; the runner reports a dirty tree as a hard
    /// containment violation rather than auto-reverting it, so the operator
    /// decides what to do with the stray changes. <see cref="TimelineEvent.Summary"/>
    /// carries the changed-file count; <see cref="TimelineEvent.Details"/> the
    /// mode and the (capped) file list. Emitted by the runner at run-finish.
    /// </summary>
    public const string ReadOnlyContainmentViolation = "read_only_containment_violation";
}

/// <summary>
/// Conventional <see cref="TimelineEvent.Actor"/> values. Producers pass
/// these constants instead of free-form strings so the filter chips on
/// the FE Timeline tab line up reliably.
/// </summary>
public static class TimelineActors
{
    public const string Agent = "agent";
    public const string Orchestrator = "orchestrator";
    public const string QualityLoop = "quality-loop";
    public const string System = "system";
    public static string Human(string email) => string.IsNullOrWhiteSpace(email) ? "human" : $"human:{email}";
}
