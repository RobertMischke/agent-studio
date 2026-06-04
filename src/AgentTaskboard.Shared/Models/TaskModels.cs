namespace OrchestratorApi.Models;

public record TaskInfo
{
    public string Id { get; init; } = "";
    public string TaskKey { get; init; } = "";
    /// <summary>
    /// Stable Linear-style reference key minted from the project's prefix
    /// plus a monotonic counter (<c>ATP-130</c>, <c>RB-42</c>). Unique
    /// within one project; persisted as the <c>"key"</c> field in
    /// <c>job.json</c> and assigned at create time or by the F33 boot
    /// migration. Null when the job pre-dates the migration on a project
    /// that has not yet been swept; the UI falls back to <see cref="Id"/>
    /// in that window.
    /// </summary>
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public string State { get; init; } = "draft";
    public int Order { get; init; } = 999;
    public string Agent { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string WatchPath { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public DateTime LastActivity { get; init; }
    /// <summary>
    /// Wall-clock UTC instant the task most recently entered its current lane.
    /// Stamped at create time and on every lane move. Legacy tasks written
    /// before this field existed fall back to <see cref="LastActivity"/> at
    /// scan time, so this is never default(DateTime) for a scanned task.
    /// Drives the <c>lane-entry</c> sort (newest entry on top).
    /// </summary>
    public DateTime EnteredLaneAt { get; init; }
    /// <summary>
    /// Per-job orchestrator token totals, surfaced on the kanban card as a
    /// small "token bubble". Populated at endpoint-read time from the
    /// project's <c>orchestrator.jsonl</c>, filtered by this job's id; null
    /// when the job has had no orchestrator LLM activity yet.
    /// </summary>
    public TaskTokenSummary? TokenSummary { get; init; }
    /// <summary>Name passed to Copilot CLI via <c>--name</c> on first start; reused with <c>--resume</c> for follow-ups.</summary>
    public string? SessionName { get; init; }
    /// <summary>Preferred model for this job (e.g. <c>claude-sonnet-4.5</c>); passed via <c>--model</c> when supported.</summary>
    public string? Model { get; init; }
    /// <summary>Which CLI backend executes this job: <c>copilot</c>, <c>claude</c>, or <c>codex</c>. Defaults to <c>copilot</c>.</summary>
    public string? CliType { get; init; }
    /// <summary>
    /// Card kind: <c>task</c> (default, a runnable unit of work) or <c>epic</c>
    /// (a container grouping sub-tasks under one overarching goal). An epic is
    /// not code-executed itself; only its sub-tasks run through the pipeline.
    /// </summary>
    public string Kind { get; init; } = TaskKinds.Task;
    /// <summary>
    /// Parent epic id when this task is a sub-task of an epic, else null. Set at
    /// create time, by the post-hoc assign endpoint, or by an epic's
    /// decomposition run.
    /// </summary>
    public string? EpicId { get; init; }
    /// <summary>
    /// Execution mode (orthogonal to <see cref="Kind"/>): <c>coding</c> (default,
    /// mutates source) | <c>planning</c> | <c>research</c> (read-only, produce a
    /// report). See <see cref="TaskModes"/>.
    /// </summary>
    public string Mode { get; init; } = TaskModes.Coding;
    /// <summary>
    /// Whether the agent may use web search / fetch for this run. Default off for
    /// coding/planning, on for research (set at create time). See decision 2 in
    /// docs/research/planning-research-task-kinds-2026-05.md.
    /// </summary>
    public bool AllowWebAccess { get; init; }
    /// <summary>
    /// When <c>true</c>, this job uses its own dedicated session even if the project runner is
    /// configured for <see cref="SessionModes.ReuseProject"/>. Lets a one-off task isolate its
    /// context from the long-running project session.
    /// </summary>
    public bool? UseOwnSession { get; init; }
    /// <summary>Last token / cost summary parsed from CLI output (best-effort).</summary>
    public SessionUsage? LastUsage { get; init; }
    public CliExecution? Execution { get; init; }
    /// <summary>
    /// Auto-commit produced on the progress→review transition; null when no commit recorded.
    /// Kept for backwards compatibility - when <see cref="Commits"/> is non-empty this
    /// mirrors its last (newest) entry. Read paths should prefer <see cref="Commits"/>;
    /// legacy <c>job.json</c> files that only carry a singular <c>commit</c> object are
    /// migrated on the fly by the scanner so consumers can rely on either field.
    /// </summary>
    public TaskCommitInfo? Commit { get; init; }
    /// <summary>
    /// Ordered chain of commits attributed to this task across iterations
    /// (oldest -&gt; newest). Tasks regularly produce more than one commit:
    /// continue-mode adds a new commit on top of the original, crash-recovery
    /// leaves a recovery commit plus a follow-up, operator-driven steers
    /// often produce a separate commit. Backwards compatible with the
    /// singular <see cref="Commit"/> field: legacy <c>job.json</c> files
    /// without <c>commits</c> are surfaced as <c>[commit]</c> by the scanner.
    /// </summary>
    public List<TaskCommitInfo> Commits { get; init; } = [];
    /// <summary>
    /// Commits the deterministic attribution rule subtracted from this
    /// task's commit set (e.g. crash-recovery commits naming another
    /// task, submodule bumps, merge commits, operator-excluded entries).
    /// Persisted as <c>excludedCommits</c> in <c>job.json</c>. Empty on
    /// legacy job folders that pre-date the attribution step. Surfaced
    /// in the protocol-pane "Git view" under a "(N excluded)" expander
    /// so the operator can audit what was withheld and why.
    /// </summary>
    public List<TaskExcludedCommitInfo> ExcludedCommits { get; init; } = [];
    /// <summary>
    /// Client identity that owns this job. References
    /// <see cref="ClientIdentity.Id"/>. Defaults to
    /// <see cref="DefaultClientIdentity.Id"/> for legacy jobs whose
    /// <c>job.json</c> predates per-task attribution; the scanner
    /// rewrites the file with that value on first read so the field
    /// is non-null after migration.
    /// </summary>
    public string OwnerClientId { get; init; } = DefaultClientIdentity.Id;
    /// <summary>
    /// Count of commits attributed to this job. Derived strictly from
    /// <see cref="Commits"/> so it can never drift from the chain it is
    /// meant to summarize (ADR "Commit-Attribution-Regel"; the historic
    /// bug was a separately-computed session-events hint that read
    /// <c>commitCount: 1</c> while <c>commits: []</c>). <see cref="Commits"/>
    /// is the single source of truth; this is a convenience projection for
    /// the kanban card. Never persisted to <c>job.json</c>.
    /// </summary>
    public int CommitCount => Commits.Count;
    /// <summary>
    /// True when at least one run moved repo HEAD - a non-trivial
    /// <c>before..after</c> SHA range in <c>session-events.jsonl</c> - or an
    /// auto-commit was stamped. Derived cheaply by the scanner; never
    /// persisted. Lets the UI distinguish an analysis-only task (no activity
    /// -&gt; "no code changes") from a task where code landed but the
    /// attribution chain is still empty (activity present yet
    /// <see cref="Commits"/> and <see cref="ExcludedCommits"/> are both empty
    /// -&gt; "commit discovery pending / failed"). Without this signal the two
    /// cases are indistinguishable, which is exactly bug (3) from the task.
    /// </summary>
    public bool CodeActivityDetected { get; init; }
    /// <summary>
    /// Ordered history of CLI session ids used by this job (oldest → newest). Each
    /// successful resume of a forking CLI (Claude / Codex / Gemini) appends a new
    /// id. <see cref="SessionName"/> is always the chain's last entry.
    /// A recovery-continue (session lost, reconstructed from job folder) breaks
    /// the chain; the next captured id will start a new logical chain segment.
    /// </summary>
    public List<string> SessionChain { get; init; } = [];
    /// <summary>
    /// Saved user intent waiting for the auto-pickup loop to run. Populated
    /// when the user sends a follow-up to a job that is not the project's
    /// current active job; cleared once the runner consumes it. See
    /// <see cref="PendingIntent"/>.
    /// </summary>
    public PendingIntent? PendingIntent { get; init; }

    /// <summary>
    /// Snapshot of the auto-mode "stuck loop" counter for this job, populated
    /// at endpoint-read time from the in-memory state on the project's
    /// runner. Null when no loop is in flight (the common case). When set,
    /// the UI surfaces a "auto-loop N/M" badge so the user can see how
    /// many orchestrator decisions have been spent on this job before the
    /// circuit breaker stops the loop.
    /// </summary>
    public AutoLoopSnapshot? AutoLoop { get; init; }

    /// <summary>
    /// Live summary-generation state for jobs in 4-review. Set when the
    /// post-completion Haiku summarizer is currently running for this
    /// job; the UI shows an "auto-reviewing" pill so the user can see
    /// that the orchestrator is still working on the card after it
    /// landed in review, instead of treating an empty status.md as a
    /// dead card.
    /// </summary>
    public TaskSummaryState? SummaryState { get; init; }

    /// <summary>
    /// Latest runner-outcome issue found in <c>logs/cli-output.log</c>.
    /// Derived at read time from orchestrator log lines, not stored in
    /// <c>job.json</c>. The UI uses this to surface permission blocks,
    /// watchdog timeouts, missing terminal sentinels, and classifier
    /// ambiguity directly on the card and protocol header.
    /// </summary>
    public TaskOutcomeIssue? OutcomeIssue { get; init; }

    /// <summary>
    /// Latest orchestrator-review verdict for this job, populated at
    /// endpoint-read time from the per-project decision journal at
    /// <c>{workspace}/logs/decisions/{project}.jsonl</c>. Drives the
    /// 4-review kanban swim-lane subdivision (orchestrator-review vs
    /// human-review) and the workspace top-banner. Values: <c>pending</c>
    /// (unresolved sentinel, not yet acted on), <c>reissue</c>,
    /// <c>escalate</c>, <c>accept</c>. Null when no orchestrator
    /// decision exists, e.g. a 4-review card that completed cleanly with
    /// <c>[[TASK_DONE]]</c> awaiting human accept.
    /// </summary>
    public string? OrchestratorVerdict { get; init; }

    /// <summary>
    /// True when this job was created by an E2E spec / Playwright fixture and
    /// should be hidden from the default kanban response. Stored as
    /// <c>"fixture": true</c> in <c>job.json</c>. Endpoints filter fixtures
    /// out of <c>/api/tasks</c> and <c>/api/tasks/grouped</c> by default;
    /// <c>?includeFixtures=true</c> exposes them for debugging.
    /// </summary>
    public bool Fixture { get; init; }

    /// <summary>
    /// Optional lifecycle substate, read from the <c>"phase"</c> field in
    /// <c>job.json</c>. Drives the kanban board's lane projection in the
    /// expanded-lifecycle-lanes model (see
    /// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c>).
    /// Application-owned: agents must not write to this field. Values come
    /// from <see cref="LifecyclePhases"/> and are constrained per state by
    /// <see cref="LifecyclePhases.AllowedByState"/>. Null means "no explicit
    /// phase on disk"; the frontend then falls back to
    /// <see cref="LifecyclePhases.DefaultFor"/> to pick a default lane. This
    /// keeps existing job folders that predate the field rendering correctly
    /// without rewriting every <c>job.json</c>.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>
    /// Structural classification of the task. One of <see cref="TaskTypes.Bug"/>,
    /// <see cref="TaskTypes.Feature"/>, or <see cref="TaskTypes.Chore"/>
    /// (default for legacy and technical work). Stored in <c>job.json</c> as
    /// <c>"taskType"</c>. The kanban card renders a small chip; filters in
    /// the header narrow the board by type. Legacy <c>"user-story"</c> values
    /// on disk are silently normalised to <see cref="TaskTypes.Feature"/> on read.
    /// </summary>
    public string TaskType { get; init; } = TaskTypes.Chore;

    /// <summary>
    /// Lightweight tag identifiers attached to this job. The label and colour
    /// for each id come from the workspace-level <c>tags.json</c> registry
    /// served at <c>GET /api/tags</c>. Unknown ids (registry entries that
    /// were soft-deleted) render as a faint "ghost" chip on the card so the
    /// user can clear the stale reference. Stored in <c>job.json</c> as
    /// <c>"tags"</c>; absent or null on disk means an empty list.
    /// </summary>
    public List<string> Tags { get; init; } = [];

    /// <summary>
    /// F34: structured cross-references to other tasks, keyed by F33 stable
    /// keys (<c>ATP-19</c>). Four relation kinds — dependsOn / relatedTo /
    /// blockedBy / supersedes (see <see cref="TaskReferences"/>). Stored as the
    /// <c>"references"</c> object in <c>job.json</c>; the scanner surfaces an
    /// empty instance when the field is absent so consumers never see null.
    /// Set atomically through <c>PUT /api/tasks/{id}/references</c> after
    /// validation (keys must exist, no self-reference, dependsOn stays a DAG).
    /// </summary>
    public TaskReferences References { get; init; } = new();
}

public record TaskOutcomeIssue
{
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public string Severity { get; init; } = "Info";
    public string Summary { get; init; } = "";
    public DateTime? LastSeenAt { get; init; }
}

/// <summary>
/// String constants for <see cref="TaskInfo.TaskType"/>. Kept as constants (not
/// an enum) so the JSON wire format is the literal string and stable across
/// enum renames. The default is <see cref="Chore"/>: existing technical work
/// that predates the field migrates to the safe neutral category.
/// </summary>
public static class TaskTypes
{
    public const string Bug = "bug";
    public const string Feature = "feature";
    public const string Chore = "chore";

    /// <summary>Legacy on-disk value for <see cref="Feature"/>; silently mapped on read.</summary>
    public const string LegacyUserStory = "user-story";

    public static readonly string[] All = [Bug, Feature, Chore];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Chore;
        var v = value.Trim();
        // Migration: existing job.json files written as "user-story" map to
        // "feature" silently on read so no bulk re-write of disk is needed.
        if (string.Equals(v, LegacyUserStory, StringComparison.OrdinalIgnoreCase)) return Feature;
        foreach (var t in All)
            if (string.Equals(t, v, StringComparison.OrdinalIgnoreCase)) return t;
        return Chore;
    }
}

/// <summary>
/// String constants and helpers for the optional <c>phase</c> substate on
/// <see cref="TaskInfo"/>. The hybrid V1 model picked in
/// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c> keeps the
/// existing six folder-level states as the durable skeleton and adds this
/// optional substate so the orchestrator-driven lanes (Intake, Post
/// Processing) can be projected by the UI without a filesystem-state
/// explosion.
///
/// Phase values are stored in <c>job.json</c> as plain strings; the wire
/// format is the literal string so hand-edited JSON stays readable. The
/// richer lifecycle history (which intake checks ran, when each phase was
/// entered, last blocking reason) lives in the optional sidecar file
/// <c>lifecycle.json</c> described by <see cref="LifecycleSnapshot"/>.
/// </summary>
public static class LifecyclePhases
{
    // 2-ready substates: which seat in the Ready group a card sits in.
    public const string HumanReady = "human-ready";
    public const string IntakeRunning = "intake-running";
    public const string IntakeBlocked = "intake-blocked";
    /// <summary>
    /// Card passed orchestrator intake; the main coding runner is now allowed
    /// to pick it up. When per-project intake is enabled, the runner skips
    /// 2-ready cards that have not reached this phase. When intake is
    /// disabled (default), the gate is open regardless of phase.
    /// </summary>
    public const string IntakePassed = "intake-passed";

    // 3-progress substates: distinguishes "coding CLI is working" from
    // "post-processing pipeline (auto-commit, summary, future checks) is
    // working" without a new filesystem state.
    public const string ExecutionRunning = "execution-running";
    public const string ExecutionStalled = "execution-stalled";
    public const string PostProcessingRunning = "post-processing-running";
    public const string PostProcessingBlocked = "post-processing-blocked";
    public const string AwaitingReview = "awaiting-review";

    public static readonly string[] All =
    [
        HumanReady, IntakeRunning, IntakeBlocked, IntakePassed,
        ExecutionRunning, ExecutionStalled,
        PostProcessingRunning, PostProcessingBlocked, AwaitingReview
    ];

    /// <summary>
    /// The phases each filesystem state is allowed to carry. States not in
    /// this map (preparation, the orchestrator-prep lane, the two review
    /// lanes, completed, archive) carry no phase: the
    /// state already says enough. Keeping this small dictionary avoids a
    /// scatter of <c>switch</c> statements when the migration tests and
    /// future frontend lane projection both need to know "is this phase
    /// legal here".
    /// </summary>
    public static readonly Dictionary<string, string[]> AllowedByState = new()
    {
        [TaskStates.Ready] = [HumanReady, IntakeRunning, IntakeBlocked, IntakePassed],
        [TaskStates.Progress] = [ExecutionRunning, ExecutionStalled, PostProcessingRunning, PostProcessingBlocked, AwaitingReview],
    };

    /// <summary>
    /// Pure default-derivation for jobs whose <c>phase</c> is null on disk.
    /// Implements the compatibility contract from
    /// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c>
    /// section 10: a job with no <c>phase</c> renders in the default lane of
    /// its state. Returns null for states that carry no phase (preparation,
    /// the orchestrator-prep lane, the review lanes,
    /// completed, archive).
    /// </summary>
    public static string? DefaultFor(string state, string? executionStatus, TaskSummaryStatus summaryStatus)
    {
        return state switch
        {
            TaskStates.Ready => HumanReady,
            TaskStates.Progress when string.Equals(executionStatus, "running", StringComparison.OrdinalIgnoreCase) => ExecutionRunning,
            TaskStates.Progress when summaryStatus == TaskSummaryStatus.Generating => PostProcessingRunning,
            // Stopped / failed / unfinished runs still live in 3-progress;
            // the existing UI treats them as the execution lane today, so
            // the lane projection keeps that behavior under the new model.
            TaskStates.Progress => ExecutionRunning,
            _ => null,
        };
    }

    /// <summary>
    /// True when <paramref name="phase"/> is empty or in the allowed set for
    /// <paramref name="state"/>. Permissive on a null phase (the state's
    /// default lane covers it) and on unknown states (no constraint
    /// declared); strict only when both are populated.
    /// </summary>
    public static bool IsAllowed(string state, string? phase)
    {
        if (string.IsNullOrWhiteSpace(phase)) return true;
        if (!AllowedByState.TryGetValue(state, out var allowed)) return true;
        return allowed.Contains(phase);
    }
}

/// <summary>
/// Optional sidecar file written next to <c>job.json</c> as
/// <c>lifecycle.json</c>. Carries the richer phase history that does not
/// fit on the wire-level <see cref="TaskInfo.Phase"/> field: which intake
/// or post-processing checks were scheduled, when the current phase was
/// entered, and the last blocking reason if any.
///
/// This file is optional; absence means "default phase for the state, no
/// history". The follow-up tasks <c>ready-orchestrator-intake-lane</c>
/// and <c>post-processing-orchestrator-lane</c> populate it. The shape is
/// version-tagged so it can grow without breaking older readers.
/// </summary>
public record LifecycleSnapshot
{
    public int Version { get; init; } = 1;
    /// <summary>The current phase. Mirrors <see cref="TaskInfo.Phase"/>; the wire field is the source of truth.</summary>
    public string? Phase { get; init; }
    /// <summary>UTC time the current phase was entered.</summary>
    public DateTime? PhaseEnteredAt { get; init; }
    /// <summary>Free-form blocking reason when the phase is <see cref="LifecyclePhases.IntakeBlocked"/> or <see cref="LifecyclePhases.PostProcessingBlocked"/>.</summary>
    public string? BlockingReason { get; init; }
    /// <summary>Intake checks scheduled or run for this job, in pipeline order.</summary>
    public List<LifecycleCheck> IntakeChecks { get; init; } = [];
    /// <summary>Post-processing checks scheduled or run for this job, in pipeline order.</summary>
    public List<LifecycleCheck> PostProcessingChecks { get; init; } = [];
}

/// <summary>One scheduled or completed check inside a <see cref="LifecycleSnapshot"/>.</summary>
public record LifecycleCheck
{
    public string Name { get; init; } = "";
    /// <summary>One of: <c>pending</c>, <c>running</c>, <c>passed</c>, <c>failed</c>, <c>skipped</c>.</summary>
    public string Status { get; init; } = "pending";
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// Wire shape for <see cref="OrchestratorApi.Services.Runner.StuckLoopState"/>
/// served to the frontend. A separate record so the wire contract is
/// stable even if the in-memory record gains internal fields.
/// </summary>
public record AutoLoopSnapshot
{
    public int Iteration { get; init; }
    public int MaxIterations { get; init; }
    public long TokensUsed { get; init; }
    public long MaxTokens { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime LastAt { get; init; }
    public string? LastQuestion { get; init; }
    public string? LastReply { get; init; }
    public string? LastError { get; init; }
}

public record SessionUsage
{
    public DateTime At { get; init; }
    public string? Tokens { get; init; }
    public string? Changes { get; init; }
    public string? Requests { get; init; }
}

/// <summary>
/// Per-job token rollup attached to the kanban card. Covers orchestrator
/// LLM calls attributed to this job (via <c>OrchestratorLogEntry.JobId</c>).
/// The frontend renders a single colour-tiered "bubble" with the total,
/// and a hover popover with the breakdown plus per-call rows.
/// </summary>
public record TaskTokenSummary
{
    public int Calls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    /// <summary>Sum of all four token counts. Drives the bubble label.</summary>
    public long TotalTokens { get; init; }
    /// <summary>Most recent model used by an attributed orchestrator call. Null when no model was recorded.</summary>
    public string? LastModel { get; init; }
    /// <summary>Timestamp of the most recent attributed orchestrator entry. Null when never updated.</summary>
    public DateTime? LastUpdate { get; init; }
    /// <summary>Per-call rows for the popover, oldest first.</summary>
    public List<TaskTokenCall> Entries { get; init; } = [];
}

/// <summary>
/// One orchestrator LLM call attributed to a job. Used by the popover to
/// list per-run rows below the aggregate.
/// </summary>
public record TaskTokenCall
{
    public DateTime Ts { get; init; }
    public string? Model { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
}

/// <summary>
/// One row in <c>logs/session-events.jsonl</c>. Records every start / continue
/// / recovery so the user can see whether a follow-up actually loaded the
/// previous CLI session or had to reconstruct from files.
/// </summary>
public record SessionEvent
{
    public DateTime Ts { get; init; }
    /// <summary><c>start</c> | <c>continue</c> | <c>recovery</c></summary>
    public string Kind { get; init; } = "";
    public string? Cli { get; init; }
    /// <summary>Session id we attempted to resume (null on fresh start / recovery).</summary>
    public string? InputSessionId { get; init; }
    /// <summary>Session id the CLI emitted in this run (filled after the run starts streaming).</summary>
    public string? CapturedSessionId { get; init; }
    /// <summary>True when we passed <c>-r</c> and the CLI accepted it; false on fresh start / recovery / dropped session.</summary>
    public bool Resumed { get; init; }
    /// <summary>Human-readable note when <see cref="Resumed"/> is false (e.g. <c>no session recorded</c>, <c>incompatible session id</c>).</summary>
    public string? Reason { get; init; }
    /// <summary>
    /// HEAD SHA of the project's git working tree captured immediately
    /// before this run's CLI started. Combined with <see cref="HeadShaAfter"/>
    /// this gives a deterministic SHA range for "commits made during this
    /// run" (<c>git rev-list HeadShaBefore..HeadShaAfter</c>) - the
    /// wall-clock window we used to derive commits from is a best-effort
    /// fallback. Null when the project has no repo configured or git was
    /// unavailable.
    /// </summary>
    public string? HeadShaBefore { get; init; }
    /// <summary>
    /// HEAD SHA captured after the run finished (backfilled in
    /// <c>OnCliFinishedAsync</c>, in lockstep with
    /// <see cref="CapturedSessionId"/>). Equal to <see cref="HeadShaBefore"/>
    /// when the agent did not commit during the run.
    /// </summary>
    public string? HeadShaAfter { get; init; }
    /// <summary>
    /// Relative path (under the job folder, forward-slashed) to the file
    /// that captured the exact context string handed to the CLI for this
    /// run - the rendered prompt template plus the task's prompt.md,
    /// attachments list, mode framing, and any foregrounded reissue
    /// open-items block. Written at spawn time so reruns / escalations are
    /// auditable. Null for runs recorded before this was captured, or when
    /// the file write failed. The full text is served on demand by
    /// <c>GET /api/tasks/{id}/runs/{index}/context</c> and never inlined in
    /// the polled runs list.
    /// </summary>
    public string? ContextRef { get; init; }
}

/// <summary>
/// Per-job derived view of "what the agent actually did", folded from
/// <c>logs/session-events.jsonl</c> (one row per CLI start / continue /
/// recovery) and <c>logs/tool-calls.jsonl</c> (one row per tool started /
/// completed). Drives the Overview tab's Agent Work block so the user sees
/// concrete metrics (call count, tool mix, recovery status) instead of an
/// inert session UUID. Every field tolerates a missing log file by
/// returning zeros / nulls; the endpoint never throws on absent logs.
/// </summary>
public record AgentWorkSummary
{
    /// <summary>Number of session-event rows (start + continue + recovery).</summary>
    public int Calls { get; init; }
    /// <summary>True when at least one session event has <c>Kind == "recovery"</c>.</summary>
    public bool Recovered { get; init; }
    /// <summary>Total <c>kind=started</c> tool-call rows.</summary>
    public int ToolCalls { get; init; }
    /// <summary>Per-tool started counts, sorted by count descending.</summary>
    public List<AgentWorkToolCount> ToolCounts { get; init; } = [];
    /// <summary>Timestamp of the earliest session event, or null when the log is empty.</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>
    /// Timestamp of the latest signal we have - max(latest session event,
    /// latest tool-call row). Null when both logs are empty.
    /// </summary>
    public DateTime? LastTouchAt { get; init; }
    /// <summary>Echoed from <c>job.json</c> for the Debug tooltip; the operator-facing UI hides this by default.</summary>
    public string? CurrentSessionId { get; init; }
}

public record AgentWorkToolCount
{
    public string Tool { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>
/// The per-job task plan the plan strip renders above the activity log. Folded
/// by <c>PlanReader</c> from <c>logs/plan-snapshots.jsonl</c> (the agent's own
/// TodoWrite / update_plan frames) and <c>logs/tool-calls.jsonl</c>. Read-only
/// observability: no model call, no edits. When the agent never emitted a plan
/// (or the CLI has no native plan frame), <see cref="HasPlan"/> is false and the
/// strip is hidden. See <c>docs/mockups/task-progress-tracking/</c>.
/// </summary>
public record TaskPlanView
{
    /// <summary>False when no plan snapshot exists; the strip renders nothing.</summary>
    public bool HasPlan { get; init; }
    /// <summary>Frame kind that produced the latest snapshot: <c>claude/TodoWrite</c> or <c>codex/update_plan</c>.</summary>
    public string? Source { get; init; }
    /// <summary>Number of plan snapshots observed for this job.</summary>
    public int SnapshotCount { get; init; }
    /// <summary>Id of the single item currently <c>active</c>, or null when none is.</summary>
    public string? ActiveItemId { get; init; }
    /// <summary>Median sub-action count of already-<c>done</c> siblings; null below two samples (no estimate band drawn).</summary>
    public int? SoftEstimateMedian { get; init; }
    /// <summary>The latest snapshot's items, each with its derived sub-actions.</summary>
    public List<TaskPlanItemView> Items { get; init; } = [];
    /// <summary>Tool calls observed before any plan item was active ("before plan").</summary>
    public List<TaskPlanSubAction> UnassignedSubActions { get; init; } = [];
}

/// <summary>One top-level plan item plus the sub-actions attributed to it.</summary>
public record TaskPlanItemView
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary><c>pending</c> | <c>active</c> | <c>done</c>.</summary>
    public string Status { get; init; } = "pending";
    public int SubActionCount { get; init; }
    public List<TaskPlanSubAction> SubActions { get; init; } = [];
}

/// <summary>One tool call attributed to a plan item; the "Sub-Tasks" the user wants to see after an item finishes.</summary>
public record TaskPlanSubAction
{
    public DateTime Ts { get; init; }
    public string Tool { get; init; } = "";
    public string? Label { get; init; }
}

public record TaskDetail
{
    public TaskInfo Info { get; init; } = new();
    public string? PromptMarkdown { get; init; }
    /// <summary>
    /// Append-only timeline of task extensions: <c>prompt-1.md</c>,
    /// <c>prompt-2.md</c>, ... written by Extend mode. Empty when the user
    /// has never extended the task. Read in the order the timeline was
    /// written; the original task body is in <see cref="PromptMarkdown"/>.
    /// </summary>
    public List<TaskPromptHistoryEntry> PromptHistory { get; init; } = [];
    /// <summary>
    /// Append-only timeline of title changes recorded for this task in
    /// <c>title-history.json</c>. Each rename through
    /// <c>PUT /api/tasks/{id}/title</c> appends one entry; the current
    /// title stays on <see cref="TaskInfo.Title"/>. Empty when the title
    /// was never edited, including for legacy job folders that predate
    /// the file. Oldest first.
    /// </summary>
    public List<TaskTitleHistoryEntry> TitleHistory { get; init; } = [];
    public string? StatusMarkdown { get; init; }
    public ContextUsageSnapshot? ContextUsage { get; init; }
    public List<TaskLogEntry> Log { get; init; } = [];
    public TaskSummaryState? SummaryState { get; init; }
    /// <summary>
    /// Task-level review evidence parsed from
    /// <c>results/review-evidence.jsonl</c>. Findings produced by security
    /// audits, code-review passes, task checks, or human reviewer notes.
    /// Empty when the file is absent or only contained malformed lines.
    /// Findings are evidence for review, not blockers: their presence does
    /// not gate any state transition. See
    /// <c>docs/filesystem-contract.md</c> "results/review-evidence.jsonl".
    /// </summary>
    public List<ReviewEvidenceEntry> ReviewEvidence { get; init; } = [];
}

/// <summary>
/// One finding in <c>results/review-evidence.jsonl</c>. The wire shape mirrors
/// the on-disk shape one-to-one so a producer can write the same record it
/// reads back. The producer is responsible for stable <see cref="Id"/> values
/// across appends so readers can fold the file into latest-per-id.
/// </summary>
public record ReviewEvidenceEntry
{
    public string Id { get; init; } = "";
    /// <summary>One of <see cref="ReviewEvidenceSources"/>. Unknown values are normalized to <c>other</c>.</summary>
    public string Source { get; init; } = ReviewEvidenceSources.Other;
    /// <summary>One of <see cref="ReviewEvidenceSeverities"/>. Unknown values are normalized to <c>info</c>.</summary>
    public string Severity { get; init; } = ReviewEvidenceSeverities.Info;
    public string Title { get; init; } = "";
    public string? Body { get; init; }
    public DateTime CreatedAt { get; init; }
    /// <summary>1-based run index this finding belongs to (matches <c>RunRecord.Index</c>). Null when not tied to a specific run.</summary>
    public int? RunIndex { get; init; }
    /// <summary>Paths relative to the job folder, e.g. <c>results/foo.png</c>.</summary>
    public List<string> Artifacts { get; init; } = [];
    /// <summary>Repository-relative file references, optionally <c>path:line</c>.</summary>
    public List<string> FileRefs { get; init; } = [];
    public bool Acknowledged { get; init; }
    /// <summary>Job id of a queued follow-up created from this finding (set by the "Create follow-up task" action).</summary>
    public string? FollowupJobId { get; init; }
}

/// <summary>
/// Allowed source slugs on <see cref="ReviewEvidenceEntry.Source"/>.
/// Producers should write one of these literals. Unknown values are accepted
/// on read and normalised to <see cref="Other"/> so a malformed file never
/// breaks the endpoint.
/// </summary>
public static class ReviewEvidenceSources
{
    public const string SecurityAudit = "security-audit";
    public const string CodeReview = "code-review";
    public const string TaskCheck = "task-check";
    public const string HumanNote = "human-note";
    public const string Other = "other";

    public static readonly string[] All = [SecurityAudit, CodeReview, TaskCheck, HumanNote, Other];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;
        var v = value.Trim();
        foreach (var s in All)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase)) return s;
        return Other;
    }
}

/// <summary>
/// Allowed severities on <see cref="ReviewEvidenceEntry.Severity"/>. Same
/// permissive-on-read contract as <see cref="ReviewEvidenceSources"/>.
/// </summary>
public static class ReviewEvidenceSeverities
{
    public const string Info = "info";
    public const string Warn = "warn";
    public const string High = "high";

    public static readonly string[] All = [Info, Warn, High];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Info;
        var v = value.Trim();
        foreach (var s in All)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase)) return s;
        return Info;
    }
}

/// <summary>
/// Body for <c>POST /api/tasks/{id}/review-evidence/{evidenceId}/follow-up</c>.
/// Optional title override; the endpoint defaults to the finding's title when
/// omitted. The created task is queued in the same project as the source job
/// and lands in <c>1-preparation</c>; the user promotes it to <c>2-ready</c>
/// when they want auto-pickup to run it.
/// </summary>
public record CreateFollowupFromEvidenceRequest
{
    public string? Title { get; init; }
    public string? TargetState { get; init; }
}

/// <summary>
/// Response shape for the follow-up endpoint. <c>JobId</c> is the slug
/// assigned to the new task; the frontend uses it to route the user to the
/// new card.
/// </summary>
public record CreateFollowupFromEvidenceResponse
{
    public string JobId { get; init; } = "";
    public string TargetState { get; init; } = "1-preparation";
}

/// <summary>
/// One entry in the task's prompt-extension timeline. Index matches the
/// filename suffix (<c>prompt-3.md</c> → Index = 3).
/// </summary>
public record TaskPromptHistoryEntry
{
    public int Index { get; init; }
    public string FileName { get; init; } = "";
    public string Markdown { get; init; } = "";
    public DateTime WrittenAt { get; init; }
}

/// <summary>
/// One entry in the task's title-revision timeline. Written by
/// <see cref="OrchestratorApi.Services.Tasks.TaskMutationService.SetJobTitle"/>
/// to <c>title-history.json</c> in the job folder whenever the title
/// actually changes (no-op renames are not recorded). The current title
/// stays on <see cref="TaskInfo.Title"/>; this is the audit trail of what
/// it used to be.
/// </summary>
public record TaskTitleHistoryEntry
{
    public DateTime At { get; init; }
    public string OldTitle { get; init; } = "";
    public string NewTitle { get; init; } = "";
    /// <summary>
    /// Free-form provenance label. Today the only writer is the rename
    /// endpoint and emits <c>"api"</c>; future producers (intake
    /// refinement, orchestrator) may use distinct sources so the UI can
    /// disambiguate.
    /// </summary>
    public string Source { get; init; } = "api";
}

public enum TaskSummaryStatus
{
    None,
    Generating,
    Ready,
    Failed
}

public record TaskSummaryState
{
    public TaskSummaryStatus Status { get; init; } = TaskSummaryStatus.None;
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public int? BytesWritten { get; init; }
}

public record ContextUsageSnapshot
{
    public DateTime At { get; init; }
    public string Command { get; init; } = "/context usage";
    public string Status { get; init; } = "ok";
    public string? Error { get; init; }
    public List<ContextUsageMetric> Metrics { get; init; } = [];
    public List<ContextUsageSection> Sections { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public string RawText { get; init; } = "";
}

public record ContextUsageMetric
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
}

public record ContextUsageSection
{
    public string Title { get; init; } = "";
    public List<string> Items { get; init; } = [];
}

public record TaskLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Event { get; init; } = "";
    public string? Detail { get; init; }
}

public record MoveJobRequest
{
    public string TargetState { get; init; } = "";

    /// <summary>
    /// Optional 0-based insertion slot in the target lane. When supplied,
    /// the move pins the dropped job to that position and rewrites every
    /// other job's <c>order</c> in the same lane + project so the
    /// resulting sequence is stable. <c>null</c> preserves the legacy
    /// behaviour: the folder moves and the job keeps whatever <c>order</c>
    /// value it had in the source lane.
    /// </summary>
    public int? TargetIndex { get; init; }
}

public enum MoveJobStatus
{
    Success,
    NotFound,
    TargetFolderExists,
    Failure
}

/// <summary>
/// Result of a <see cref="OrchestratorApi.Services.Tasks.TaskStateMachine.MoveJob"/>
/// call. <paramref name="NewFolderPath"/> is populated only on
/// <see cref="MoveJobStatus.Success"/> and carries the absolute path of the
/// post-move job folder. Callers that want to write into the moved folder
/// (chat-log line, follow-up file) MUST use this rather than re-finding the
/// job through the scanner — the cache may not yet reflect the move, and
/// a stale path would silently recreate the source folder on first write.
/// </summary>
public record MoveJobOutcome(MoveJobStatus Status, string? Message = null, string? NewFolderPath = null);

/// <summary>Result of <c>POST /api/tasks/{id}/restore-from-failed-pickup</c>.</summary>
public enum RestoreFromFailedPickupStatus
{
    /// <summary>Folder was restored into the target lane under the resolved slug.</summary>
    Success,
    /// <summary>Slug is not in <c>3a-failed-pickup</c>: either it does not exist
    /// or it has already been restored. Distinguished from <see cref="NotFound"/>
    /// by the caller (the endpoint maps <c>NotFound</c> to 404 and <c>NoOp</c>
    /// to 200 with a status payload so the call is idempotent).</summary>
    NoOp,
    /// <summary>No folder with this slug exists in <c>3a-failed-pickup</c>.</summary>
    NotFound,
    /// <summary>A folder with the resolved slug already exists in the target lane.</summary>
    TargetFolderExists,
    /// <summary>The slug did not match the dead-letter shape <c>&lt;original&gt;-pickup-failed-&lt;yyyy-mm-dd&gt;</c>.</summary>
    InvalidSlug,
    /// <summary>Filesystem operation failed unexpectedly.</summary>
    Failure
}

/// <summary>Outcome of a <c>POST /api/tasks/{id}/restore-from-failed-pickup</c> call.
/// On <see cref="RestoreFromFailedPickupStatus.Success"/> the caller can read
/// <see cref="RestoredSlug"/> (the slug the folder now lives under) and
/// <see cref="OriginalSlug"/> (the slug parsed back from the dead-letter name).</summary>
public record RestoreFromFailedPickupOutcome(
    RestoreFromFailedPickupStatus Status,
    string? RestoredSlug = null,
    string? OriginalSlug = null,
    string? SourceSlug = null,
    string? Message = null);

/// <summary>Body for <c>POST /api/tasks/{id}/restore-from-failed-pickup</c>.
/// Body is optional; defaults to restoring the original slug.</summary>
public record RestoreFromFailedPickupRequest
{
    /// <summary>When <c>true</c>, keep the <c>-pickup-failed-&lt;utc&gt;</c>
    /// suffix on the restored folder. Default <c>false</c>: strip the suffix
    /// so the slug matches the pre-dead-letter name.</summary>
    public bool KeepDeadLetterSlug { get; init; }
}

/// <summary>
/// Per-item entry for <c>POST /api/tasks/batch-move</c>. Each item names
/// the job, the watch path that disambiguates a slug that lives in two
/// workspaces, the target lane, and an optional 0-based insertion slot
/// (<see cref="MoveJobRequest.TargetIndex"/>). Items are processed
/// independently: a failure on one item does not roll back items that
/// already moved.
/// </summary>
public record BatchMoveItem
{
    public string JobId { get; init; } = "";
    public string? WatchPath { get; init; }
    public string TargetState { get; init; } = "";
    public int? TargetIndex { get; init; }
}

public record BatchMoveRequest
{
    public List<BatchMoveItem> Items { get; init; } = [];
}

/// <summary>
/// Per-item outcome string for the batch-move response:
/// <list type="bullet">
/// <item><description><c>moved</c>: folder transitioned to the target lane.</description></item>
/// <item><description><c>not-found</c>: no job folder matched the (jobId, watchPath) pair.</description></item>
/// <item><description><c>conflict</c>: a folder with the same slug already exists in the target lane (stale duplicate).</description></item>
/// <item><description><c>rejected</c>: invalid input (unknown lane name, empty jobId, etc.).</description></item>
/// <item><description><c>failed</c>: an unexpected IO error blocked the move.</description></item>
/// </list>
/// </summary>
public record BatchMoveItemResult
{
    public string JobId { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Message { get; init; }
}

public record BatchMoveResponse
{
    public List<BatchMoveItemResult> Results { get; init; } = [];
}

public record CreateJobRequest
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Order { get; init; } = 999;
    public string Agent { get; init; } = "claude";
    public string WatchPath { get; init; } = "";
    public string? PromptMarkdown { get; init; }
    public string? Model { get; init; }
    public string? TargetState { get; init; }
    /// <summary>Optional CLI backend (claude|codex|copilot|gemini). Defaults to claude when omitted.</summary>
    public string? CliType { get; init; }
    /// <summary>Card kind: <c>task</c> (default) or <c>epic</c>. See <see cref="TaskKinds"/>.</summary>
    public string? Kind { get; init; }
    /// <summary>Optional parent epic id (assignment way 1: at create time). The new card is created as a sub-task of this epic.</summary>
    public string? EpicId { get; init; }
    /// <summary>Execution mode: <c>coding</c> (default) | <c>planning</c> | <c>research</c>. See <see cref="TaskModes"/>.</summary>
    public string? Mode { get; init; }
    /// <summary>Allow web search/fetch for this run. When null, defaults by mode (research = on, else off).</summary>
    public bool? AllowWebAccess { get; init; }
    /// <summary>
    /// Optional client identity that owns the new job. When omitted, the
    /// endpoint falls back to the X-Client-Id header on the incoming
    /// request, then to <see cref="DefaultClientIdentity.Id"/>.
    /// </summary>
    public string? OwnerClientId { get; init; }

    /// <summary>
    /// When <c>true</c>, the new job is marked as an E2E test fixture and is
    /// hidden from the default kanban response. Used by Playwright specs that
    /// create real job folders to keep their fixtures out of the user's view
    /// on stable.
    /// </summary>
    public bool Fixture { get; init; }

    /// <summary>
    /// Structural classification (<c>bug</c>, <c>feature</c>, <c>chore</c>).
    /// Defaults to <see cref="TaskTypes.Chore"/> when omitted. Validated on
    /// the server and normalized via <see cref="TaskTypes.Normalize"/>; legacy
    /// <c>"user-story"</c> input maps to <see cref="TaskTypes.Feature"/>.
    /// </summary>
    public string? TaskType { get; init; }

    /// <summary>
    /// Optional tag ids to attach to the new job. Unknown ids are dropped
    /// silently; the registry is the source of truth for label and colour.
    /// </summary>
    public List<string>? Tags { get; init; }
}

/// <summary>
/// Payload from <c>GET /api/tasks/{id}/promote-to-coding</c>: a fully
/// pre-filled coding-task draft derived from a finished planning task.
/// The frontend seeds the existing create-task modal with these fields so
/// the modal stays the single source of truth for the create UX. Images
/// are returned as fetchable references (not inline bytes); the modal
/// re-uploads them byte-for-byte into the new task's <c>attachments/</c>
/// on save. See docs/research/planning-research-task-kinds-2026-05.md.
/// </summary>
public record PromoteToCodingResponse
{
    /// <summary>Title for the new coding task (the planning task's title, or its report heading).</summary>
    public string Title { get; init; } = "";

    /// <summary>Prompt body, extracted from the report's <c>## Proposed task prompt</c> section.</summary>
    public string PromptMarkdown { get; init; } = "";

    /// <summary>Always <see cref="TaskModes.Coding"/> — the promotion target mode.</summary>
    public string Mode { get; init; } = TaskModes.Coding;

    /// <summary>Always <see cref="TaskStates.Preparation"/> so the user gets one review pass before pickup (decision 3).</summary>
    public string TargetState { get; init; } = TaskStates.Preparation;

    /// <summary>Watch path of the source planning task; the new task lands in the same project.</summary>
    public string WatchPath { get; init; } = "";

    /// <summary>Project name of the source planning task (display convenience).</summary>
    public string ProjectName { get; init; } = "";

    /// <summary>Every image under the planning task's <c>results/</c> and <c>attachments/</c> folders, deduped by file name.</summary>
    public List<PromoteAttachmentRef> Attachments { get; init; } = [];
}

/// <summary>
/// One copyable image attachment surfaced by
/// <see cref="PromoteToCodingResponse"/>. The frontend fetches
/// <see cref="Url"/> as a blob, then re-uploads it into the new task.
/// </summary>
public record PromoteAttachmentRef
{
    public string FileName { get; init; } = "";

    /// <summary>Source folder: <c>results</c> or <c>attachments</c>.</summary>
    public string Source { get; init; } = "";

    /// <summary>Relative API URL that serves the image bytes from the source task.</summary>
    public string Url { get; init; } = "";
}

/// <summary>
/// One entry in the workspace-level tag registry. Stored as one element of
/// the JSON array at <c>&lt;TaskRepository&gt;/tags.json</c> and surfaced via
/// <c>GET /api/tags</c>. The id is the lookup key referenced from each
/// <see cref="TaskInfo.Tags"/> entry; label, colour, and description are
/// pure display metadata.
/// </summary>
public record TagRegistryEntry
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string Color { get; init; } = "#94a3b8";
    public string Description { get; init; } = "";
}

/// <summary>
/// Body for <c>POST /api/tags</c>. When <see cref="Id"/> is omitted, the
/// server derives it from <see cref="Label"/> by lowercasing and stripping
/// to <c>[a-z0-9-]</c>.
/// </summary>
public record CreateTagRequest
{
    public string? Id { get; init; }
    public string Label { get; init; } = "";
    public string? Color { get; init; }
    public string? Description { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/tasks/{id}/tags</c>. Replace-all: the supplied list is
/// the new full set of tag ids on the job. Empty list clears tags. Unknown
/// ids are accepted (the registry may evolve), but they will render as a
/// ghost chip until the registry catches up or the job is re-tagged.
/// </summary>
public record SetJobTagsRequest
{
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Body for <c>PUT /api/tasks/{id}/task-type</c>. Validated via
/// <see cref="TaskTypes.Normalize"/>; an unknown value collapses to
/// <see cref="TaskTypes.Chore"/>.
/// </summary>
public record SetJobTaskTypeRequest
{
    public string TaskType { get; init; } = TaskTypes.Chore;
}

public record ReorderRequest
{
    public List<string> JobIds { get; init; } = [];
    public List<TaskOrderItem> Jobs { get; init; } = [];
}

public record TaskOrderItem
{
    public string JobId { get; init; } = "";
    public string WatchPath { get; init; } = "";
}

public record ChangeProjectRequest
{
    public string TargetWatchPath { get; init; } = "";
}

public record UpdateJobFileRequest
{
    public string FileName { get; init; } = "";
    public string Content { get; init; } = "";
}

/// <summary>
/// Kind classification for a job-folder markdown artifact, used by
/// <c>GET /api/tasks/{id}/artifacts</c>. The frontend Files tab styles each
/// card by kind (prompt is editable; aspect carries the auto-review verdict
/// section; note marks operator/recovery hand-offs).
/// </summary>
public enum TaskArtifactKind
{
    Prompt,
    Aspect,
    Note,
    Other,
}

/// <summary>
/// One markdown file in the job root surfaced by the Files tab. The
/// content is not embedded; the existing
/// <c>GET /api/tasks/{id}/files/{fileName}</c> endpoint serves it on
/// demand so the listing stays cheap.
/// </summary>
public record TaskArtifact
{
    public string Name { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime Mtime { get; init; }
    public TaskArtifactKind Kind { get; init; }
    /// <summary>Set when <see cref="Kind"/> is <c>Aspect</c>; the part between <c>aspect-</c> and <c>.md</c>.</summary>
    public string? AspectName { get; init; }
}

/// <summary>Wire shape for <c>GET /api/tasks/{id}/artifacts</c>.</summary>
public record TaskArtifactsResponse
{
    public string JobId { get; init; } = "";
    public List<TaskArtifact> Files { get; init; } = [];
}

public record GitCommitRequest
{
    public string Message { get; init; } = "";
}

/// <summary>
/// Body for the operator "include commit" override
/// (<c>POST /api/tasks/{id}/commits/{sha}/include</c>). Optional metadata for
/// the add-from-recent case; the endpoint fills the rest from live git.
/// </summary>
public record IncludeCommitRequest
{
    public string? Message { get; init; }
    public DateTime? At { get; init; }
}

public record ProjectSettings
{
    /// <summary>When true, transition <c>3-progress → 4-auto-review</c> auto-commits and stamps the SHA on the job.</summary>
    public bool AutoCommit { get; init; } = true;

    /// <summary>
    /// Controls when the platform pushes runner-owned commits. Default is
    /// <see cref="AutoPushStrategies.OnCompleted"/> so only commits that have
    /// passed human review and reached <c>6-completed</c> are pushed.
    /// </summary>
    public string AutoPushStrategy { get; init; } = AutoPushStrategies.OnCompleted;

    /// <summary>
    /// Last runner mode chosen by the user for this project ("manual", "auto-single",
    /// "auto-continuous", "paused"). Restored at backend startup so the auto-pickup
    /// toggle survives self-rebuild / restart. Null means "use the default (manual)".
    /// </summary>
    public string? RunnerMode { get; init; }

    /// <summary>
    /// Model the orchestrator uses when it makes decisions on behalf of the
    /// user in auto mode (Phase E and later). Defaults to the strongest
    /// Claude model (<c>claude-opus-4-7</c>) so decisions are high-quality;
    /// the user can downgrade to Sonnet for cost. Null means use the default.
    /// </summary>
    public string? OrchestratorModel { get; init; }

    /// <summary>
    /// Per-topic cadence for scheduled analysis reports (project-level
    /// "Analysis Reports" surface). Map of topic slug
    /// (e.g. <c>roadmapAlignment</c>, <c>queueHealth</c>, <c>docsDrift</c>,
    /// <c>staleJobs</c>, <c>tokenSpend</c>, <c>qaStatus</c>) to one of
    /// <c>disabled</c>, <c>fewHours</c>, <c>daily</c>, <c>manualOnly</c>.
    /// Default null = "disabled" for every topic; reports never auto-run
    /// without an explicit opt-in. The contract for execution is documented
    /// in <c>docs/analysis-reports.md</c>; this struct stores the user's
    /// cadence choice only.
    /// </summary>
    public Dictionary<string, string>? AnalysisSchedules { get; init; }

    /// <summary>
    /// ADR-0026 orchestrator-prep autonomy scale, <c>0..4</c>:
    /// <c>0</c> manual, <c>1</c> cautious, <c>2</c> balanced (default),
    /// <c>3</c> confident, <c>4</c> fully-auto. Governs whether the
    /// orchestrator-prep loop accepts borderline tasks, iterates, or
    /// escalates them to <c>5-human-review</c> (the retired
    /// <c>1b-needs-human-review</c> lane is gone). Null means "use the
    /// default (balanced, level 2)". The setting is consulted on each
    /// pickup tick; mid-iteration policy switches do not happen.
    /// </summary>
    public int? AutonomyLevel { get; init; }

    /// <summary>
    /// Per-project switch for the orchestrator intake loop. When true, the
    /// coding runner waits for orchestrator intake to finish before picking
    /// up a 2-ready card (gates pickup on <c>phase == intake-passed</c>).
    /// When false / null (default), the gate is open: cards are picked up
    /// regardless of phase, and the intake hosted service does not act on
    /// the project. Intake is opt-in per project so the broader migration
    /// risk stays bounded; see the <c>ready-orchestrator-intake-lane</c>
    /// task in the expanded-lifecycle-lanes plan.
    /// </summary>
    public bool? IntakeEnabled { get; init; }

    /// <summary>
    /// F35: per-lane sort strategy override. Map of lane key
    /// (<see cref="TaskStates.Backlog"/> .. <see cref="TaskStates.Archive"/>)
    /// to a strategy id from <see cref="LaneSortStrategies"/>. Null or a
    /// missing lane key falls back to <see cref="LaneSortStrategies.GetDefaultForLane"/>.
    /// Used by the kanban grouped endpoint when ordering jobs inside a lane;
    /// the runner's pickup loop keeps its own deterministic order and is
    /// unaffected.
    /// </summary>
    public Dictionary<string, string>? LaneSortStrategyOverrides { get; init; }

    /// <summary>
    /// Per-project pipeline-step configuration. Map of pipeline step id
    /// (e.g. <c>aspect-code-quality</c>, <c>post-lint-scss</c>) to a
    /// per-step override of <c>enabled</c> / <c>mode</c> / <c>model</c>.
    /// A missing step id, or a null field inside an entry, falls through
    /// to the built-in pipeline default. The known step ids come from
    /// <c>PipelineCatalogue.Standard</c>; this map only overrides those
    /// code-defined steps - it does not add or reorder steps, because the
    /// runtime maps each step id to a concrete service. Resolution order
    /// for <c>model</c> is step -&gt; <see cref="OrchestratorModel"/> -&gt;
    /// runtime default; for <c>mode</c> it is step -&gt; built-in default.
    /// Persisted in <c>project-settings.json</c>.
    /// </summary>
    public Dictionary<string, PipelineStepSetting>? PipelineSteps { get; init; }

    /// <summary>
    /// ADR-0052: maximum number of tasks the runner may execute concurrently
    /// for this project. Default <c>1</c> keeps the runner strictly sequential
    /// (one active slot, behaviour byte-for-byte identical to the pre-parallel
    /// runner). Values &gt; 1 opt the project into worktree-isolated parallel
    /// execution; the runner clamps to <c>&gt;= 1</c>. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public int MaxParallelism { get; init; } = 1;

    /// <summary>
    /// ADR-0052: branch that parallel task worktrees branch off and merge back
    /// into (the project's integration line). Default <c>develop</c> so
    /// <c>main</c> stays the released line. When parallelism is off
    /// (<see cref="MaxParallelism"/> == 1) the sequential runner keeps pushing
    /// to its configured target and this value is unused.
    /// </summary>
    public string IntegrationBranch { get; init; } = "develop";

    /// <summary>
    /// ADR-0052: how a finished task branch is folded back into
    /// <see cref="IntegrationBranch"/>. One of <see cref="IntegrationStrategies"/>
    /// (<c>direct-merge</c> default, or <c>pull-request</c>). Only consulted
    /// when <see cref="MaxParallelism"/> &gt; 1.
    /// </summary>
    public string IntegrationStrategy { get; init; } = IntegrationStrategies.DirectMerge;

    /// <summary>
    /// Per-CLI permission / sandbox mode override. Map of <see cref="CliTypes"/>
    /// id (<c>claude</c> / <c>codex</c> / <c>gemini</c> / <c>copilot</c>) to a
    /// mode id from <see cref="CliPermissionModes"/>. A missing CLI key means
    /// "no project override" and resolves to the platform default
    /// (<see cref="CliPermissionModes.Yolo"/>) or, where detectable, the CLI's
    /// global config. The resolved mode is rendered to concrete flags by
    /// <see cref="CliPermissionFlags"/> on every spawn, so changes take effect
    /// on the next run without a backend restart. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public Dictionary<string, string>? CliModes { get; init; }

    /// <summary>
    /// Model the epic planning/decomposition run uses (way 3): when a
    /// <see cref="TaskKinds.Epic"/> card is picked up, the runner runs a
    /// planning step that authors the sub-task list instead of a coding run.
    /// Null means "use the epic card's own <see cref="TaskInfo.Model"/>"; set
    /// it to bias decomposition toward a stronger (or cheaper) model than the
    /// sub-tasks themselves will run on. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public string? EpicPlanningModel { get; init; }

    /// <summary>
    /// Where an epic decomposition run's generated sub-tasks land. False /
    /// null (default) lands them in <c>0-backlog</c> for human triage, exactly
    /// like the deterministic <c>POST /api/epics/{id}/sub-tasks</c> path. True
    /// lands them straight in <c>2-ready</c> so an auto-pickup project starts
    /// executing the plan without a manual triage pass. Persisted in
    /// <c>project-settings.json</c>.
    /// </summary>
    public bool? EpicSubTasksToReady { get; init; }
}

/// <summary>
/// Run-condition vocabulary for <see cref="PipelineStepCondition.When"/>. A
/// step's condition decides whether it executes for a given task run, on top
/// of the enabled flag. <see cref="Always"/> is the default (run whenever the
/// step is enabled); <see cref="Never"/> keeps the override around without
/// firing. The remaining tokens gate on the run outcome or the task's own
/// classification.
/// </summary>
public static class PipelineStepConditions
{
    /// <summary>Run whenever the step is enabled (default).</summary>
    public const string Always = "always";

    /// <summary>Keep the override but never run the step.</summary>
    public const string Never = "never";

    /// <summary>Run only when the run ended in an abort/stop outcome.</summary>
    public const string OnAbort = "on-abort";

    /// <summary>Run only when the CLI process exited with a non-zero code.</summary>
    public const string OnNonzeroExit = "on-nonzero-exit";

    /// <summary>Run only when at least one review aspect failed.</summary>
    public const string OnAspectFail = "on-aspect-fail";

    /// <summary>
    /// Run only for a matching <see cref="TaskInfo.TaskType"/> (the condition
    /// value names the task type, e.g. <c>bug</c>).
    /// </summary>
    public const string TaskType = "task-type";

    /// <summary>
    /// Run only when the task carries a matching tag (the condition value names
    /// the tag).
    /// </summary>
    public const string Tag = "tag";

    /// <summary>Every known condition token, in display order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Always, Never, OnAbort, OnNonzeroExit, OnAspectFail, TaskType, Tag,
    ];

    /// <summary>Tokens whose semantics require a non-empty <see cref="PipelineStepCondition.Value"/>.</summary>
    public static readonly IReadOnlyList<string> ValueBearing = [TaskType, Tag];

    public static bool IsKnown(string? when) =>
        when != null && All.Contains(when, StringComparer.OrdinalIgnoreCase);

    public static bool RequiresValue(string? when) =>
        when != null && ValueBearing.Contains(when, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lower-cases and trims a token to its canonical form. Returns null for a
    /// null/blank/unknown token so callers can treat it as "no condition".
    /// </summary>
    public static string? Normalize(string? when)
    {
        if (string.IsNullOrWhiteSpace(when)) return null;
        var trimmed = when.Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase)) return known;
        }
        return null;
    }
}

/// <summary>
/// Per-step run condition: a <see cref="When"/> token from
/// <see cref="PipelineStepConditions"/> plus an optional <see cref="Value"/>
/// used by the value-bearing tokens (<c>task-type</c>, <c>tag</c>). A null or
/// <see cref="PipelineStepConditions.Always"/> condition means "run whenever
/// the step is enabled".
/// </summary>
public record PipelineStepCondition
{
    public string When { get; init; } = PipelineStepConditions.Always;
    public string? Value { get; init; }
}

/// <summary>
/// Per-step project override stored in <see cref="ProjectSettings.PipelineSteps"/>.
/// Every field is nullable: null means "no override, use the pipeline /
/// runtime default" so a partial entry (e.g. only a model choice) leaves
/// the other dimensions on their defaults.
/// </summary>
public record PipelineStepSetting
{
    /// <summary>
    /// When <c>false</c>, the step is skipped for this project. Null or
    /// <c>true</c> leaves the step enabled. Only honoured for steps the
    /// runtime can actually skip (today: the aspect post-steps and the
    /// lint-scss gate); the core agent run cannot be disabled.
    /// </summary>
    public bool? Enabled { get; init; }

    /// <summary>
    /// Gate mode for steps that support it (<c>off</c> / <c>warn</c> /
    /// <c>fail</c>, see <c>PostStepMode</c>). Null falls through to the
    /// built-in default. Ignored for steps that have no gate semantics.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>
    /// Model id that runs this step's LLM call (uses the shared CLI+model
    /// selector vocabulary). Null falls back to the project
    /// <see cref="ProjectSettings.OrchestratorModel"/> and then the runtime
    /// default. Only meaningful for steps that invoke an LLM (the aspect
    /// post-steps); deterministic tool steps ignore it.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Run condition gating whether this step executes for a given task run.
    /// Null (or an <see cref="PipelineStepConditions.Always"/> condition) means
    /// "run whenever the step is enabled". Only honoured for steps the runtime
    /// evaluates conditions for (today: the abort-review step).
    /// </summary>
    public PipelineStepCondition? Condition { get; init; }
}

public static class LaneSortStrategies
{
    /// <summary>
    /// User-managed order. Sort by <c>order</c> ASC + <c>key</c> desc as
    /// tiebreaker. Drag-and-drop on the kanban is only enabled on lanes set
    /// to this strategy.
    /// </summary>
    public const string Manual = "manual";

    /// <summary>Newest key on top. Sort by <c>key</c> desc + <c>createdAt</c> desc.</summary>
    public const string NewestFirst = "newest-first";

    /// <summary>Oldest key on top (FIFO triage). Sort by <c>key</c> asc + <c>createdAt</c> asc.</summary>
    public const string OldestFirst = "oldest-first";

    /// <summary>Most-recent activity on top. Sort by <c>lastActivity</c> desc + <c>order</c> asc.</summary>
    public const string LastActivity = "last-activity";

    /// <summary>
    /// Hybrid default: most-recently-entered-lane on top, with manually
    /// dragged cards pinned. Cards with an explicit <c>order</c> (i.e. not the
    /// 999 sentinel) cluster on top by <c>order</c> asc — these are the
    /// drag-pinned cards; the rest flow by <c>enteredLaneAt</c> desc so the
    /// newest arrival is on top. This is the default for every lane.
    /// </summary>
    public const string LaneEntry = "lane-entry";

    /// <summary>
    /// Internal auto-pickup priority. Sort by <c>order</c> asc + <c>lastActivity</c>
    /// asc. Reserved for the runner; not selectable in the project-settings UI.
    /// </summary>
    public const string PickupPriority = "pickup-priority";

    /// <summary>The sentinel <c>order</c> value meaning "not explicitly placed".</summary>
    public const int UnpinnedOrder = 999;

    /// <summary>All strategies including the internal pickup-priority.</summary>
    public static readonly string[] All =
        [Manual, NewestFirst, OldestFirst, LastActivity, LaneEntry, PickupPriority];

    /// <summary>Strategies surfaced in the project-settings UI dropdown.</summary>
    public static readonly string[] UserVisible =
        [LaneEntry, Manual, NewestFirst, OldestFirst, LastActivity];

    /// <summary>
    /// Default strategy used when a lane has no explicit override in
    /// <see cref="ProjectSettings.LaneSortStrategies"/>. Every lane now defaults
    /// to <see cref="LaneEntry"/>: the card that most recently entered the lane
    /// floats to the top, while a manual drag pins a card in place. A project
    /// can still override any lane via <c>LaneSortStrategyOverrides</c>.
    /// </summary>
    public static string GetDefaultForLane(string lane) => LaneEntry;

    /// <summary>
    /// Returns the configured strategy for a lane, falling back to
    /// <see cref="GetDefaultForLane"/> when the project has no override.
    /// Unknown strategy ids fall back to the default too.
    /// </summary>
    public static string Resolve(ProjectSettings settings, string lane)
    {
        if (settings.LaneSortStrategyOverrides != null
            && settings.LaneSortStrategyOverrides.TryGetValue(lane, out var configured)
            && IsValid(configured))
        {
            return configured;
        }
        return GetDefaultForLane(lane);
    }

    public static bool IsValid(string? strategy)
        => !string.IsNullOrWhiteSpace(strategy)
           && All.Contains(strategy, StringComparer.OrdinalIgnoreCase);

    public static bool IsUserSelectable(string? strategy)
        => !string.IsNullOrWhiteSpace(strategy)
           && UserVisible.Contains(strategy, StringComparer.OrdinalIgnoreCase);

    public static string Normalize(string? strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy)) return Manual;
        var v = strategy.Trim();
        foreach (var s in All)
            if (string.Equals(s, v, StringComparison.OrdinalIgnoreCase))
                return s;
        return Manual;
    }

    /// <summary>
    /// Returns the comparer that implements <paramref name="strategy"/> for
    /// <see cref="TaskInfo"/>. Unknown strategy ids fall back to manual.
    /// </summary>
    public static IComparer<TaskInfo> GetComparer(string strategy)
    {
        return Normalize(strategy) switch
        {
            NewestFirst => Comparer<TaskInfo>.Create(CompareNewestFirst),
            OldestFirst => Comparer<TaskInfo>.Create(CompareOldestFirst),
            LastActivity => Comparer<TaskInfo>.Create(CompareLastActivityDesc),
            LaneEntry => Comparer<TaskInfo>.Create(CompareLaneEntry),
            PickupPriority => Comparer<TaskInfo>.Create(ComparePickupPriority),
            _ => Comparer<TaskInfo>.Create(CompareManual),
        };
    }

    private static int CompareManual(TaskInfo a, TaskInfo b)
    {
        var byOrder = a.Order.CompareTo(b.Order);
        if (byOrder != 0) return byOrder;
        // Stable tiebreaker: newer key on top so two cards at order 999
        // sort consistently. CompareKeyDesc handles null keys safely.
        return CompareKeyDesc(a, b);
    }

    /// <summary>
    /// Hybrid lane-entry order. A card is "pinned" when it carries an explicit
    /// <c>order</c> (anything other than the <see cref="UnpinnedOrder"/>
    /// sentinel) — those are the cards a user dragged into place. Pinned cards
    /// cluster on top sorted by <c>order</c> asc; everything else flows below
    /// them by <c>enteredLaneAt</c> desc (newest arrival on top), with key desc
    /// as a stable tiebreaker. This lets a manual drag override the time-based
    /// flow without disabling it for the rest of the lane.
    /// </summary>
    private static int CompareLaneEntry(TaskInfo a, TaskInfo b)
    {
        var aPinned = a.Order != UnpinnedOrder;
        var bPinned = b.Order != UnpinnedOrder;
        if (aPinned != bPinned) return aPinned ? -1 : 1;
        if (aPinned)
        {
            var byOrder = a.Order.CompareTo(b.Order);
            if (byOrder != 0) return byOrder;
            return CompareKeyDesc(a, b);
        }
        var byEntry = b.EnteredLaneAt.CompareTo(a.EnteredLaneAt);
        if (byEntry != 0) return byEntry;
        return CompareKeyDesc(a, b);
    }

    private static int CompareNewestFirst(TaskInfo a, TaskInfo b)
    {
        var byKey = CompareKeyDesc(a, b);
        if (byKey != 0) return byKey;
        return b.CreatedAt.CompareTo(a.CreatedAt);
    }

    private static int CompareOldestFirst(TaskInfo a, TaskInfo b)
    {
        var byKey = CompareKeyAsc(a, b);
        if (byKey != 0) return byKey;
        return a.CreatedAt.CompareTo(b.CreatedAt);
    }

    private static int CompareLastActivityDesc(TaskInfo a, TaskInfo b)
    {
        var byActivity = b.LastActivity.CompareTo(a.LastActivity);
        if (byActivity != 0) return byActivity;
        return a.Order.CompareTo(b.Order);
    }

    private static int ComparePickupPriority(TaskInfo a, TaskInfo b)
    {
        var byOrder = a.Order.CompareTo(b.Order);
        if (byOrder != 0) return byOrder;
        return a.LastActivity.CompareTo(b.LastActivity);
    }

    /// <summary>
    /// Compare reference keys (e.g. <c>ATP-130</c>) in semantic order: split
    /// at the dash so the numeric suffix sorts numerically, not
    /// lexicographically. Jobs with a null key fall to the end of the lane.
    /// </summary>
    private static int CompareKeyAsc(TaskInfo a, TaskInfo b)
    {
        var ka = a.Key;
        var kb = b.Key;
        if (string.IsNullOrEmpty(ka) && string.IsNullOrEmpty(kb)) return 0;
        if (string.IsNullOrEmpty(ka)) return 1;
        if (string.IsNullOrEmpty(kb)) return -1;
        return KeyComparer.Compare(ka, kb);
    }

    private static int CompareKeyDesc(TaskInfo a, TaskInfo b)
    {
        // Keep "null keys to the bottom" regardless of direction; a naive
        // CompareKeyAsc(b, a) would float nulls to the top in desc order.
        var ka = a.Key;
        var kb = b.Key;
        if (string.IsNullOrEmpty(ka) && string.IsNullOrEmpty(kb)) return 0;
        if (string.IsNullOrEmpty(ka)) return 1;
        if (string.IsNullOrEmpty(kb)) return -1;
        return -KeyComparer.Compare(ka, kb);
    }

    private static class KeyComparer
    {
        public static int Compare(string a, string b)
        {
            var dashA = a.LastIndexOf('-');
            var dashB = b.LastIndexOf('-');
            if (dashA > 0 && dashB > 0)
            {
                var prefixA = a.AsSpan(0, dashA);
                var prefixB = b.AsSpan(0, dashB);
                var byPrefix = prefixA.CompareTo(prefixB, StringComparison.OrdinalIgnoreCase);
                if (byPrefix != 0) return byPrefix;
                if (int.TryParse(a.AsSpan(dashA + 1), out var nA)
                    && int.TryParse(b.AsSpan(dashB + 1), out var nB))
                {
                    return nA.CompareTo(nB);
                }
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public static class AutoPushStrategies
{
    public const string Never = "never";
    public const string OnCompleted = "on-completed";
    public const string AlwaysImmediate = "always-immediate";

    public static readonly string[] All = [Never, OnCompleted, AlwaysImmediate];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return OnCompleted;
        var v = value.Trim();
        foreach (var strategy in All)
            if (string.Equals(strategy, v, StringComparison.OrdinalIgnoreCase))
                return strategy;
        return OnCompleted;
    }
}

public static class IntegrationStrategies
{
    public const string DirectMerge = "direct-merge";
    public const string PullRequest = "pull-request";

    public static readonly string[] All = [DirectMerge, PullRequest];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DirectMerge;
        var v = value.Trim();
        foreach (var strategy in All)
            if (string.Equals(strategy, v, StringComparison.OrdinalIgnoreCase))
                return strategy;
        return DirectMerge;
    }
}

public record SetAutoCommitRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/max-parallelism</c> (ADR-0052). The
/// value is clamped to <c>&gt;= 1</c> server-side; <c>1</c> means sequential.
/// </summary>
public record SetMaxParallelismRequest
{
    public int MaxParallelism { get; init; } = 1;
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/integration-branch</c> (ADR-0052).
/// Blank reverts to the default integration branch.
/// </summary>
public record SetIntegrationBranchRequest
{
    public string? Branch { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/integration-strategy</c> (ADR-0052).
/// Unknown values normalize to <see cref="IntegrationStrategies.DirectMerge"/>.
/// </summary>
public record SetIntegrationStrategyRequest
{
    public string Strategy { get; init; } = IntegrationStrategies.DirectMerge;
}

public record SetAutoPushStrategyRequest
{
    public string Strategy { get; init; } = AutoPushStrategies.OnCompleted;
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/cli-mode</c>. Sets the per-project
/// permission mode for one CLI. A null / empty <see cref="Mode"/> clears the
/// override so the CLI reverts to the platform default (YOLO) / global config.
/// </summary>
public record SetCliModeRequest
{
    public string CliType { get; init; } = "";
    public string? Mode { get; init; }
}

public record SetOrchestratorModelRequest
{
    public string? Model { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/epic-planning</c>. Tunes the epic
/// decomposition (planning) run: which model authors the sub-task list, and
/// whether the generated sub-tasks land in <c>2-ready</c> instead of
/// <c>0-backlog</c>. Null/absent fields leave that knob on its default.
/// </summary>
public record SetEpicPlanningRequest
{
    public string? Model { get; init; }
    public bool? SubTasksToReady { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/autonomy</c>. The integer level is
/// clamped to <c>0..4</c> server-side. See ADR-0026.
/// </summary>
public record SetAutonomyLevelRequest
{
    public int Level { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{name}/lane-sort-strategy</c> (F35). When
/// <see cref="Strategy"/> is null or empty, the explicit override is cleared
/// and the lane reverts to its default.
/// </summary>
public record SetLaneSortStrategyRequest
{
    public string Lane { get; init; } = "";
    public string? Strategy { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/projects/{projectName}/pipeline-step</c>. Sets the
/// per-project override for one pipeline step. Null fields leave that
/// dimension on its built-in default; an all-null body clears the override.
/// </summary>
public record SetPipelineStepRequest
{
    /// <summary>Full pipeline step id (e.g. <c>aspect-code-quality</c>) or bare suffix (<c>code-quality</c>).</summary>
    public string StepId { get; init; } = "";
    public bool? Enabled { get; init; }
    public string? Mode { get; init; }
    public string? Model { get; init; }

    /// <summary>
    /// Run condition for this step (see <see cref="PipelineStepConditions"/>).
    /// Null leaves the condition on its built-in default; an
    /// <see cref="PipelineStepConditions.Always"/> condition is treated as "no
    /// override" and clears any stored condition.
    /// </summary>
    public PipelineStepCondition? Condition { get; init; }
}

/// <summary>
/// Body for <c>POST /api/runner/{projectName}/orchestrator-log/override</c>.
/// The user is overriding an orchestrator decision: <see cref="OriginalTs"/>
/// names the entry being overridden (timestamp from the feed),
/// <see cref="NewDirection"/> is the new follow-up the user wants applied
/// to <see cref="JobId"/>.
/// </summary>
public record OrchestratorOverrideRequest
{
    public DateTime OriginalTs { get; init; }
    public string JobId { get; init; } = "";
    public string NewDirection { get; init; } = "";
}

/// <summary>
/// Snapshot of the commit a job produced when transitioning from progress to
/// review. Cached in <c>job.json</c> so the board card and detail view can
/// render file count + SHA without re-running git per render.
///
/// <para>
/// Commit-attribution metadata (<see cref="Attribution"/> + <see cref="Confidence"/>)
/// is populated by the deterministic post-execution attribution step (ADR
/// "Commit-Attribution-Regel"). Legacy entries without an explicit
/// <see cref="Attribution"/> are treated as <see cref="CommitAttributionKinds.Legacy"/>
/// at render time so the UI distinguishes "we know this came from the rule
/// engine" from "this was stamped before attribution existed".
/// </para>
/// </summary>
public record TaskCommitInfo
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public string Message { get; init; } = "";
    public int FilesChanged { get; init; }
    public List<string> Files { get; init; } = [];
    public DateTime At { get; init; }
    /// <summary>
    /// How the commit got attributed to this task. One of
    /// <see cref="CommitAttributionKinds"/>. Null on legacy job.json entries
    /// that pre-date the attribution step; the reader treats null as
    /// <see cref="CommitAttributionKinds.Legacy"/>.
    /// </summary>
    public string? Attribution { get; init; }
    /// <summary>
    /// Confidence of an automatic attribution (0..1). Null for
    /// <see cref="CommitAttributionKinds.ManualAdd"/> /
    /// <see cref="CommitAttributionKinds.ManualIncludeAfterExclude"/> and
    /// for legacy entries. The frontend renders a small badge when this is
    /// present so the operator can see where the system was uncertain.
    /// </summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// One commit that the deterministic attribution rule subtracted from a
/// task's commit set (see ADR "Commit-Attribution-Regel"). Persisted under
/// <c>excludedCommits</c> in <c>job.json</c>. Surfaced under a
/// "(N excluded)" expander in the protocol-pane git view with the reason
/// tooltip; lets the operator see *why* a commit was withheld.
/// </summary>
public record TaskExcludedCommitInfo
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    /// <summary>One of <see cref="CommitExclusionReasons"/>. Free-form on read.</summary>
    public string Reason { get; init; } = CommitExclusionReasons.Other;
    /// <summary>Commit subject (first line). Optional; carried so the UI can render the row without re-querying git.</summary>
    public string? Subject { get; init; }
    public DateTime At { get; init; }
    /// <summary>
    /// True when the operator excluded a commit that the rule engine had
    /// originally attributed to this task. Used by the UI to render a
    /// "manual" marker so the operator can see where they intervened.
    /// </summary>
    public bool Manual { get; init; }
}

/// <summary>
/// String constants for <see cref="TaskCommitInfo.Attribution"/>. Kept as
/// constants (not an enum) so the wire format stays a literal string and
/// hand-written job.json files remain readable.
/// </summary>
public static class CommitAttributionKinds
{
    /// <summary>The deterministic rule engine attributed this commit.</summary>
    public const string Automatic = "automatic";
    /// <summary>Operator added a commit the rule engine missed.</summary>
    public const string ManualAdd = "manual-add";
    /// <summary>
    /// Operator restored a commit that had been excluded (e.g. because the
    /// rule engine flagged it as a crash-recovery for another task and the
    /// operator confirmed it belongs here).
    /// </summary>
    public const string ManualIncludeAfterExclude = "manual-include-after-exclude";
    /// <summary>
    /// Legacy entry without explicit attribution (job.json pre-dates the
    /// attribution step). Treated as "trust the existing stamp" by readers.
    /// </summary>
    public const string Legacy = "legacy";

    public static readonly string[] All = [Automatic, ManualAdd, ManualIncludeAfterExclude, Legacy];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Legacy;
        var v = value.Trim();
        foreach (var k in All)
            if (string.Equals(k, v, StringComparison.OrdinalIgnoreCase)) return k;
        return Legacy;
    }
}

/// <summary>
/// String constants for <see cref="TaskExcludedCommitInfo.Reason"/>. The
/// rule engine writes one of these; the UI maps each to a human-friendly
/// hover label.
/// </summary>
public static class CommitExclusionReasons
{
    /// <summary>Crash-recovery commit that names another task in its message.</summary>
    public const string CrashRecoveryOfOtherTask = "crash-recovery-of-other-task";
    /// <summary>Submodule / stable update commits that don't belong to any one task.</summary>
    public const string UpdateStableBump = "update-stable-bump";
    /// <summary>git pull merge commits produced by the update-stable workflow.</summary>
    public const string MergeCommit = "merge-commit";
    /// <summary>Commit landed before the task's first start; outside the window.</summary>
    public const string OutsideTaskWindow = "outside-task-window";
    /// <summary>Operator manually excluded the commit via the UI.</summary>
    public const string ManualExclude = "manual-exclude";
    /// <summary>Unrecognized exclusion reason.</summary>
    public const string Other = "other";

    public static readonly string[] All =
        [CrashRecoveryOfOtherTask, UpdateStableBump, MergeCommit, OutsideTaskWindow, ManualExclude, Other];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;
        var v = value.Trim();
        foreach (var r in All)
            if (string.Equals(r, v, StringComparison.OrdinalIgnoreCase)) return r;
        return Other;
    }
}

public record WatchPathEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string RootPath { get; init; } = "";
    public string RepositoryPath { get; init; } = "";
}

public record CliExecution
{
    public string JobId { get; init; } = "";
    public string TaskKey { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTime StartedAt { get; init; }
    public string Status { get; init; } = "";      // running | completed | failed | cancelled
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public string? Model { get; init; }
    /// <summary>
    /// Canonical terminal run outcome once known: success, failed, noop,
    /// blocked, needs-input, interrupted, or unknown. Null while running and
    /// on legacy in-memory records.
    /// </summary>
    public string? RunOutcome { get; init; }
}

public static class TaskIdentity
{
    public static string CreateKey(string watchPath, string jobId) => $"{watchPath}::{jobId}";
}

public record RunnerStatus
{
    public Dictionary<string, ProjectRunnerStatus> Projects { get; init; } = new();
}

public record ProjectRunnerStatus
{
    public string ProjectName { get; init; } = "";
    public string Mode { get; init; } = "manual";
    public string? ActiveJobId { get; init; }
    public CliExecution? ActiveExecution { get; init; }
    public List<string> QueuedJobIds { get; init; } = [];
    /// <summary>
    /// Reason recorded the last time the runner mode changed. Mirrors the
    /// <c>reason</c> argument to <see cref="OrchestratorApi.Services.Runner.ProjectRunner.SetMode"/>;
    /// surfaces so the board can distinguish operator-initiated
    /// <c>manual</c> / <c>paused</c> transitions ("api-toggle", "api: POST /api/runner/{project}/stop")
    /// from system-initiated ones ("auto-failure circuit-breaker", "capture-fail circuit-breaker",
    /// "cross-slug infra circuit-breaker", "supervisor pause") in the lane pill chip.
    /// </summary>
    public string? ModeReason { get; init; }
    /// <summary>
    /// UTC timestamp when the mode last changed. Null on legacy in-memory
    /// records (before the backend started recording it).
    /// </summary>
    public DateTime? ModeChangedAt { get; init; }
    /// <summary>
    /// Coarse classification of where the current mode came from. One of
    /// <c>user</c>, <c>circuit-breaker</c>, <c>supervisor</c>, <c>system</c>.
    /// Derived from <see cref="ModeReason"/> at SetMode time so the frontend
    /// does not have to re-implement the heuristic on every render.
    /// </summary>
    public string? ModeSource { get; init; }
    /// <summary>
    /// Backend role assigned via <c>Runner:Role</c> config; one of
    /// <c>orchestrator</c> (the default — picks tasks automatically when mode is
    /// <c>auto-*</c>) or <c>test-subject</c> (pickup loop is structurally
    /// disabled — only explicit start endpoints can drive a job). The dev
    /// backend ships as <c>test-subject</c> so a shared workspace does not
    /// produce a parallel pickup race against stable.
    /// </summary>
    public string Role { get; init; } = "orchestrator";
    /// <summary>
    /// Mode the operator asked for while a job was still running. Non-null only
    /// when a <c>PUT /api/runner/{project}/mode</c> with <c>manual</c> /
    /// <c>paused</c> arrived while <see cref="ActiveJobId"/> was set. The
    /// runner applies the value the moment the active job clears; the frontend
    /// renders the lane pill as "MANUAL (after current)" while this field is
    /// populated.
    /// </summary>
    public string? PendingMode { get; init; }
    /// <summary>
    /// Job id the deferred mode change is waiting on. Mirrors
    /// <see cref="ActiveJobId"/> at the moment the deferred change was recorded
    /// so the UI can render "after &lt;slug&gt;" in the tooltip.
    /// </summary>
    public string? PendingModeWillApplyAfter { get; init; }
}

public record StartJobRequest
{
    public string? AgentOverride { get; init; }
    public string? Model { get; init; }
    public string? CliType { get; init; }
}

public record ContinueJobRequest
{
    public string Prompt { get; init; } = "";
    public string? Model { get; init; }
    public string? CliType { get; init; }
    /// <summary>
    /// How the follow-up should be interpreted. <c>continue</c> (default) is a
    /// next-turn message in the same conversation. <c>steer</c> frames the
    /// follow-up as a course correction. <c>extend</c> appends a new prompt
    /// file to the job folder so the task history grows blog-style.
    /// <c>newTask</c> starts a new sub-task in the same session.
    /// See <see cref="ContinueModes"/>.
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
/// Discriminated response for <c>POST /api/tasks/{id}/continue</c> and
/// <c>POST /api/tasks/{id}/start</c>. <c>started</c> means the run is
/// actually live; <c>queued</c> means the project was busy with another
/// job, the user's intent has been saved as a draft on the target task,
/// and the target task has been moved to the top of <c>2-ready</c> so the
/// auto-pickup loop will run it on the next tick. The frontend treats
/// queued as success-with-info (no modal); the chat carries the
/// orchestrator's <c>[queued]</c> meta line for user-facing feedback.
/// </summary>
public record ContinueJobResponse
{
    /// <summary><c>started</c> | <c>queued</c></summary>
    public string Status { get; init; } = "started";
    public CliExecution? Execution { get; init; }
    public ContinueJobQueuedInfo? Queued { get; init; }
}

public record ContinueJobQueuedInfo
{
    /// <summary><c>project-busy</c> is the only reason today.</summary>
    public string Reason { get; init; } = "project-busy";
    /// <summary>The job that was running when the user's send hit; for context only.</summary>
    public string? ActiveJobId { get; init; }
    public string? ActiveJobTitle { get; init; }
    /// <summary>Where in the <c>2-ready</c> queue the target ended up (1 = next pickup).</summary>
    public int Position { get; init; }
    /// <summary>The state the target was in before the queue promotion.</summary>
    public string? PromotedFromState { get; init; }
}

/// <summary>
/// Saved user intent on a job that could not run immediately because the
/// project was busy. Persisted as <c>pending-intent.json</c> in the job
/// folder. The auto-pickup loop reads and consumes this when it runs the
/// job, which turns the auto-pickup into a UserContinue with the saved
/// follow-up + mode instead of a fresh start.
/// </summary>
public record PendingIntent
{
    public int Version { get; init; } = 1;
    /// <summary>One of <see cref="ContinueModes"/>.</summary>
    public string Mode { get; init; } = ContinueModes.Continue;
    public string Prompt { get; init; } = "";
    public DateTime SavedAt { get; init; }
    /// <summary><c>project-busy</c> for now.</summary>
    public string SavedReason { get; init; } = "project-busy";
    /// <summary>Diagnostic only: which job was active when this was saved.</summary>
    public string? SavedAgainstActiveJobId { get; init; }
}

/// <summary>
/// String values accepted on <see cref="ContinueJobRequest.Mode"/>. Kept as
/// constants (not an enum) so the JSON wire format is the literal string,
/// which is friendlier for hand-written API calls and stable across enum
/// renames.
/// </summary>
public static class ContinueModes
{
    public const string Continue = "continue";
    public const string Steer    = "steer";
    public const string Extend   = "extend";
    public const string NewTask  = "newTask";

    public static readonly string[] All = [Continue, Steer, Extend, NewTask];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Continue;
        var v = value.Trim();
        foreach (var m in All)
            if (string.Equals(m, v, StringComparison.OrdinalIgnoreCase)) return m;
        return Continue;
    }
}

public record SetJobModelRequest
{
    public string? Model { get; init; }
}

public record SetJobCliTypeRequest
{
    public string CliType { get; init; } = "";
    public bool? UseOwnSession { get; init; }
}

public record SetJobTitleRequest
{
    public string Title { get; init; } = "";
}

/// <summary>Body for <c>PUT /api/tasks/{id}/epic</c>: the parent epic id, or null/empty to detach.</summary>
public record SetJobEpicRequest
{
    public string? EpicId { get; init; }
}

/// <summary>Curated entry in a CLI's model catalog returned by <c>GET /api/cli/{type}/models</c>.</summary>
public record CliModelInfo
{
    /// <summary>Model identifier passed to <c>--model &lt;id&gt;</c>.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-friendly label shown in dropdowns. Defaults to <c>Id</c> when empty.</summary>
    public string Label { get; init; } = "";
    /// <summary>Premium-request multiplier (Copilot only; null elsewhere).</summary>
    public double? Multiplier { get; init; }
    /// <summary>Optional vendor / family grouping (anthropic, openai, google, …).</summary>
    public string? Vendor { get; init; }
    /// <summary>Marks the entry the CLI uses by default when <c>--model</c> is omitted.</summary>
    public bool IsDefault { get; init; }
}

public record CliModelCatalog
{
    public List<CliModelInfo> Models { get; init; } = [];
    /// <summary>How the catalog was obtained: <c>config</c>, <c>cli-pty</c>, <c>hardcoded</c>, …</summary>
    public string Source { get; init; } = "config";
    /// <summary>UTC timestamp of the most recent (re)build. Useful for cache diagnostics.</summary>
    public DateTime FetchedAt { get; init; }
}


public record SetRunnerModeRequest
{
    public string Mode { get; init; } = "manual";
}

public record SetCliPathRequest
{
    public string Path { get; init; } = "";
}

public record SetGitHubTokenRequest
{
    public string? Token { get; init; }
}

/// <summary>
/// Body for <c>PUT /api/cli/quota/caps</c>. Sets one cap entry by
/// <c>(cliType, windowLabel)</c>; the label matches what the per-CLI quota
/// probe emits (e.g. "Current 5-hour session", "Weekly", "Premium requests").
/// </summary>
public record SetCliQuotaCapRequest
{
    public string CliType { get; init; } = "";
    public string WindowLabel { get; init; } = "";
    public int CapPct { get; init; }
}

public record CliOutputLine
{
    public DateTime Timestamp { get; init; }
    public string Stream { get; init; } = "stdout";  // stdout | stderr
    public string Text { get; init; } = "";
}

/// <summary>
/// Well-known task-slug prefixes that carry semantic meaning across the
/// pipeline. Kept next to <see cref="TaskStates"/> so the runner, the
/// orchestrator-prep rules, and the workspace summary agree on one spelling.
/// </summary>
public static class TaskSlugs
{
    /// <summary>
    /// Prefix the orchestrator stamps on a card that exists only so a human
    /// can make a call the automation must not. Such a card is never
    /// machine-actionable: the runner's pickup sweep herds it to
    /// <see cref="TaskStates.HumanReview"/> regardless of autonomy level (the
    /// former 1b-needs-human-review bounce lane was retired), and the runner
    /// refuses to auto-pick it out of
    /// <see cref="TaskStates.Ready"/> (which would NOOP-burn a CLI run and
    /// trip the cross-slug infra circuit breaker).
    /// </summary>
    public const string HumanDecisionNeededPrefix = "human-decision-needed-";

    /// <summary>True when <paramref name="slug"/> names a human-decision-needed card.</summary>
    public static bool IsHumanDecisionNeeded(string? slug) =>
        !string.IsNullOrEmpty(slug)
        && slug.StartsWith(HumanDecisionNeededPrefix, System.StringComparison.OrdinalIgnoreCase);
}

public static class TaskStates
{
    /// <summary>
    /// Triage staging area for new tasks. Sits before <see cref="Preparation"/>
    /// and is the default landing lane for <see cref="CreateJobRequest"/> when
    /// no explicit <c>targetState</c> is supplied. Auto-pickup never reaches
    /// into this lane: a backlog job must be promoted explicitly. The numeric
    /// prefix sorts it before <c>1-preparation</c> on disk and in the kanban.
    /// </summary>
    public const string Backlog = "0-backlog";

    public const string Preparation = "1-preparation";

    // ADR-0026: the orchestrator-prep lane is *additive* (no rename of the
    // existing 1-preparation -> 2-ready -> ... chain). The 1a- sort key slots
    // between 1- and 2- both on disk and in the kanban: ASCII '-' (45) is less
    // than 'a' (97), and '1' is less than '2'.
    //
    // The former 1b-needs-human-review bounce lane has been retired: the
    // "Human decision needed" concept was obsoleted. Prep now admits actionable
    // cards straight to 2-ready, and genuine "a human must decide" cases are
    // escalated to 5-human-review by the orchestrator / the human-review funnel.
    // Boot migration in TaskStateMachine moves any stray 1b folder to 2-ready.
    public const string OrchestratorPrep = "1a-orchestrator-prep";

    public const string Ready = "2-ready";
    public const string Progress = "3-progress";

    // 3a-failed-pickup is the visible orphan lane for boot-sweep verdicts
    // that previously vanished into 7-archive. The pickup-loud-not-archive
    // contract: a folder that crossed the resume window without a completion
    // sentinel lands here, never silently in 7-archive. Hide-when-empty in
    // the UI (same rule as 5-human-review). The
    // additive 3a- sort key keeps existing folders, code references, and
    // tests valid: ASCII '-' (45) < 'a' (97) so 3-progress sorts before
    // 3a-...; '3' < '4' so 3a- sorts before 4-auto-review. Populated by
    // StaleProgressArchiver when it sees a stale orphan or empty 3-progress
    // folder. See ADR-0028.
    public const string FailedPickup = "3a-failed-pickup";

    // 3b-code-not-complete is the park lane for a task that exhausted its
    // auto-pickup retry budget without ever reaching review (no commit, agent
    // never signalled done, classifier-unknown). Instead of stopping the whole
    // project at the first broken task, the runner parks it here and keeps
    // pulling the next Ready task; the project only flips to manual once the
    // systemic "3x3" pattern trips (see ProjectRunner.AutoFailureDistinctTaskHaltThreshold).
    // Additive lane (no boot migration): the 3b- sort key slots between
    // 3a-failed-pickup and 4-auto-review on disk and in the kanban (ASCII '-'
    // (45) < 'a' (97), and '3' < '4'). Hide-when-empty in the UI (same rule as
    // 5-human-review). Auto-pickup never reaches into
    // this lane: the picker only enumerates 3-progress.
    public const string CodeNotComplete = "3b-code-not-complete";

    // 4-auto-review is the orchestrator's lane: ReviewDecisionOrchestrator
    // can reissue, accept-as-done, or escalate. Anything that has crossed
    // the "ready for the user" line lives in 5-human-review instead, so
    // the kanban can split "machine still chewing" from "waiting on you".
    // The legacy single 4-review lane is migrated on backend boot via
    // TaskStateMachine.EnsureStateFoldersAndMigrate. See ADR-0025.
    public const string AutoReview = "4-auto-review";
    public const string HumanReview = "5-human-review";
    public const string Completed = "6-completed";
    public const string Archive = "7-archive";

    public static readonly string[] All =
        [Backlog, Preparation, OrchestratorPrep, Ready, Progress, FailedPickup, CodeNotComplete, AutoReview, HumanReview, Completed, Archive];

    /// <summary>Maps old unnumbered folder names to new numbered ones.</summary>
    public static readonly Dictionary<string, string> LegacyFolderMap = new()
    {
        ["preparation"] = Preparation,
        ["ready"] = Ready,
        ["progress"] = Progress,
        // The pre-ADR-0025 lane shape mapped one "review" lane to the
        // orchestrator's pass; preserve that meaning by funnelling unnumbered
        // legacy folders into 4-auto-review.
        ["review"] = AutoReview,
        ["completed"] = Completed,
    };

    /// <summary>
    /// Numbered legacy lane names that pre-date ADR-0025 (three-stage review
    /// pipeline). The boot-time migration in
    /// <see cref="OrchestratorApi.Services.Tasks.TaskStateMachine.EnsureStateFoldersAndMigrate"/>
    /// uses this to rename folders and rewrite job.json state fields.
    /// </summary>
    public static readonly Dictionary<string, string> NumberedLegacyMap = new()
    {
        ["4-review"] = AutoReview,
        ["5-completed"] = Completed,
        ["6-archive"] = Archive,
    };

    public static string MapLegacyState(string state) => state switch
    {
        "draft" => Preparation,
        "running" => Progress,
        "review-needed" => AutoReview,
        "accepted" => Completed,
        "rejected" => Completed,
        "archived" => Completed,
        "4-review" => AutoReview,
        "5-completed" => Completed,
        "6-archive" => Archive,
        // Retired lane: any job still tagged with the removed 1b state lands
        // in 2-ready (the destination the lane was manually emptied into).
        "1b-needs-human-review" => Ready,
        _ => Preparation
    };

    /// <summary>Returns the display name without the number prefix.</summary>
    public static string DisplayName(string state) =>
        state.Contains('-') ? state[(state.IndexOf('-') + 1)..] : state;
}
