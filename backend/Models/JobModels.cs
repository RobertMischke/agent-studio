namespace OrchestratorApi.Models;

public record JobInfo
{
    public string Id { get; init; } = "";
    public string JobKey { get; init; } = "";
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
    /// Per-job orchestrator token totals, surfaced on the kanban card as a
    /// small "token bubble". Populated at endpoint-read time from the
    /// project's <c>orchestrator.jsonl</c>, filtered by this job's id; null
    /// when the job has had no orchestrator LLM activity yet.
    /// </summary>
    public JobTokenSummary? TokenSummary { get; init; }
    /// <summary>Name passed to Copilot CLI via <c>--name</c> on first start; reused with <c>--resume</c> for follow-ups.</summary>
    public string? SessionName { get; init; }
    /// <summary>Preferred model for this job (e.g. <c>claude-sonnet-4.5</c>); passed via <c>--model</c> when supported.</summary>
    public string? Model { get; init; }
    /// <summary>Which CLI backend executes this job: <c>copilot</c>, <c>claude</c>, or <c>codex</c>. Defaults to <c>copilot</c>.</summary>
    public string? CliType { get; init; }
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
    public JobCommitInfo? Commit { get; init; }
    /// <summary>
    /// Ordered chain of commits attributed to this task across iterations
    /// (oldest -&gt; newest). Tasks regularly produce more than one commit:
    /// continue-mode adds a new commit on top of the original, crash-recovery
    /// leaves a recovery commit plus a follow-up, operator-driven steers
    /// often produce a separate commit. Backwards compatible with the
    /// singular <see cref="Commit"/> field: legacy <c>job.json</c> files
    /// without <c>commits</c> are surfaced as <c>[commit]</c> by the scanner.
    /// </summary>
    public List<JobCommitInfo> Commits { get; init; } = [];
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
    /// Lower-bound count of commits attributed to this job, derived
    /// cheaply by the scanner from session-events.jsonl SHA ranges plus
    /// <see cref="Commit"/>. The kanban card surfaces a "+N commits"
    /// hint when this is greater than 1 so reviewers can see at a
    /// glance that more than the single auto-commit is waiting. The
    /// precise list lives behind <c>/api/jobs/{id}/commits</c>.
    /// </summary>
    public int CommitCount { get; init; }
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
    public JobSummaryState? SummaryState { get; init; }

    /// <summary>
    /// Latest runner-outcome issue found in <c>logs/cli-output.log</c>.
    /// Derived at read time from orchestrator log lines, not stored in
    /// <c>job.json</c>. The UI uses this to surface permission blocks,
    /// watchdog timeouts, missing terminal sentinels, and classifier
    /// ambiguity directly on the card and protocol header.
    /// </summary>
    public JobOutcomeIssue? OutcomeIssue { get; init; }

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
    /// out of <c>/api/jobs</c> and <c>/api/jobs/grouped</c> by default;
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
}

public record JobOutcomeIssue
{
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public string Severity { get; init; } = "Info";
    public string Summary { get; init; } = "";
    public DateTime? LastSeenAt { get; init; }
}

/// <summary>
/// String constants for <see cref="JobInfo.TaskType"/>. Kept as constants (not
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
/// <see cref="JobInfo"/>. The hybrid V1 model picked in
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
    /// this map (preparation, the orchestrator-prep / needs-human-review
    /// lanes, the two review lanes, completed, archive) carry no phase: the
    /// state already says enough. Keeping this small dictionary avoids a
    /// scatter of <c>switch</c> statements when the migration tests and
    /// future frontend lane projection both need to know "is this phase
    /// legal here".
    /// </summary>
    public static readonly Dictionary<string, string[]> AllowedByState = new()
    {
        [JobStates.Ready] = [HumanReady, IntakeRunning, IntakeBlocked, IntakePassed],
        [JobStates.Progress] = [ExecutionRunning, ExecutionStalled, PostProcessingRunning, PostProcessingBlocked, AwaitingReview],
    };

    /// <summary>
    /// Pure default-derivation for jobs whose <c>phase</c> is null on disk.
    /// Implements the compatibility contract from
    /// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c>
    /// section 10: a job with no <c>phase</c> renders in the default lane of
    /// its state. Returns null for states that carry no phase (preparation,
    /// the orchestrator-prep / needs-human-review lanes, the review lanes,
    /// completed, archive).
    /// </summary>
    public static string? DefaultFor(string state, string? executionStatus, JobSummaryStatus summaryStatus)
    {
        return state switch
        {
            JobStates.Ready => HumanReady,
            JobStates.Progress when string.Equals(executionStatus, "running", StringComparison.OrdinalIgnoreCase) => ExecutionRunning,
            JobStates.Progress when summaryStatus == JobSummaryStatus.Generating => PostProcessingRunning,
            // Stopped / failed / unfinished runs still live in 3-progress;
            // the existing UI treats them as the execution lane today, so
            // the lane projection keeps that behavior under the new model.
            JobStates.Progress => ExecutionRunning,
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
/// fit on the wire-level <see cref="JobInfo.Phase"/> field: which intake
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
    /// <summary>The current phase. Mirrors <see cref="JobInfo.Phase"/>; the wire field is the source of truth.</summary>
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
public record JobTokenSummary
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
    public List<JobTokenCall> Entries { get; init; } = [];
}

/// <summary>
/// One orchestrator LLM call attributed to a job. Used by the popover to
/// list per-run rows below the aggregate.
/// </summary>
public record JobTokenCall
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
}

public record JobDetail
{
    public JobInfo Info { get; init; } = new();
    public string? PromptMarkdown { get; init; }
    /// <summary>
    /// Append-only timeline of task extensions: <c>prompt-1.md</c>,
    /// <c>prompt-2.md</c>, ... written by Extend mode. Empty when the user
    /// has never extended the task. Read in the order the timeline was
    /// written; the original task body is in <see cref="PromptMarkdown"/>.
    /// </summary>
    public List<JobPromptHistoryEntry> PromptHistory { get; init; } = [];
    /// <summary>
    /// Append-only timeline of title changes recorded for this task in
    /// <c>title-history.json</c>. Each rename through
    /// <c>PUT /api/jobs/{id}/title</c> appends one entry; the current
    /// title stays on <see cref="JobInfo.Title"/>. Empty when the title
    /// was never edited, including for legacy job folders that predate
    /// the file. Oldest first.
    /// </summary>
    public List<JobTitleHistoryEntry> TitleHistory { get; init; } = [];
    public string? StatusMarkdown { get; init; }
    public ContextUsageSnapshot? ContextUsage { get; init; }
    public List<JobLogEntry> Log { get; init; } = [];
    public JobSummaryState? SummaryState { get; init; }
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
/// Body for <c>POST /api/jobs/{id}/review-evidence/{evidenceId}/follow-up</c>.
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
public record JobPromptHistoryEntry
{
    public int Index { get; init; }
    public string FileName { get; init; } = "";
    public string Markdown { get; init; } = "";
    public DateTime WrittenAt { get; init; }
}

/// <summary>
/// One entry in the task's title-revision timeline. Written by
/// <see cref="OrchestratorApi.Services.Jobs.JobMutationService.SetJobTitle"/>
/// to <c>title-history.json</c> in the job folder whenever the title
/// actually changes (no-op renames are not recorded). The current title
/// stays on <see cref="JobInfo.Title"/>; this is the audit trail of what
/// it used to be.
/// </summary>
public record JobTitleHistoryEntry
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

public enum JobSummaryStatus
{
    None,
    Generating,
    Ready,
    Failed
}

public record JobSummaryState
{
    public JobSummaryStatus Status { get; init; } = JobSummaryStatus.None;
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

public record JobLogEntry
{
    public DateTime Timestamp { get; init; }
    public string Event { get; init; } = "";
    public string? Detail { get; init; }
}

public record MoveJobRequest
{
    public string TargetState { get; init; } = "";
}

public enum MoveJobStatus
{
    Success,
    NotFound,
    TargetFolderExists,
    Failure
}

public record MoveJobOutcome(MoveJobStatus Status, string? Message = null);

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
/// One entry in the workspace-level tag registry. Stored as one element of
/// the JSON array at <c>&lt;TaskRepository&gt;/tags.json</c> and surfaced via
/// <c>GET /api/tags</c>. The id is the lookup key referenced from each
/// <see cref="JobInfo.Tags"/> entry; label, colour, and description are
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
/// Body for <c>PUT /api/jobs/{id}/tags</c>. Replace-all: the supplied list is
/// the new full set of tag ids on the job. Empty list clears tags. Unknown
/// ids are accepted (the registry may evolve), but they will render as a
/// ghost chip until the registry catches up or the job is re-tagged.
/// </summary>
public record SetJobTagsRequest
{
    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Body for <c>PUT /api/jobs/{id}/task-type</c>. Validated via
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
    public List<JobOrderItem> Jobs { get; init; } = [];
}

public record JobOrderItem
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

public record GitCommitRequest
{
    public string Message { get; init; } = "";
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
    /// bounces them to <c>1b-needs-human-review</c>. Null means "use the
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

public record SetAutoCommitRequest
{
    public bool Enabled { get; init; }
}

public record SetAutoPushStrategyRequest
{
    public string Strategy { get; init; } = AutoPushStrategies.OnCompleted;
}

public record SetOrchestratorModelRequest
{
    public string? Model { get; init; }
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
/// </summary>
public record JobCommitInfo
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    public string Message { get; init; } = "";
    public int FilesChanged { get; init; }
    public List<string> Files { get; init; } = [];
    public DateTime At { get; init; }
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
    public string JobKey { get; init; } = "";
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

public static class JobIdentity
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
/// Discriminated response for <c>POST /api/jobs/{id}/continue</c> and
/// <c>POST /api/jobs/{id}/start</c>. <c>started</c> means the run is
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

public static class JobStates
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

    // ADR-0026: orchestrator-prep + needs-human-review lanes are *additive*
    // (no rename of the existing 1-preparation -> 2-ready -> ... chain).
    // The sort keys 1a- and 1b- slot between 1- and 2- both on disk and in
    // the kanban: ASCII '-' (45) is less than 'a' (97), and '1' is less
    // than '2'. Visible kanban order: Prep -> OrchPrep -> NeedsClar -> Ready -> ...
    public const string OrchestratorPrep = "1a-orchestrator-prep";

    // 1b-needs-human-review is the bounce lane the orchestrator-prep loop
    // writes to at autonomy <= 3 when a task is genuinely-unclear. Hidden
    // when empty in the UI (same rule as failed-pickup and 5-human-review).
    public const string NeedsHumanReview = "1b-needs-human-review";

    public const string Ready = "2-ready";
    public const string Progress = "3-progress";

    // 3a-failed-pickup is the visible orphan lane for boot-sweep verdicts
    // that previously vanished into 7-archive. The pickup-loud-not-archive
    // contract: a folder that crossed the resume window without a completion
    // sentinel lands here, never silently in 7-archive. Hide-when-empty in
    // the UI (same rule as 1b-needs-human-review and 5-human-review). The
    // additive 3a- sort key keeps existing folders, code references, and
    // tests valid: ASCII '-' (45) < 'a' (97) so 3-progress sorts before
    // 3a-...; '3' < '4' so 3a- sorts before 4-auto-review. Populated by
    // StaleProgressArchiver when it sees a stale orphan or empty 3-progress
    // folder. See ADR-0028.
    public const string FailedPickup = "3a-failed-pickup";

    // 4-auto-review is the orchestrator's lane: ReviewDecisionOrchestrator
    // can reissue, accept-as-done, or escalate. Anything that has crossed
    // the "ready for the user" line lives in 5-human-review instead, so
    // the kanban can split "machine still chewing" from "waiting on you".
    // The legacy single 4-review lane is migrated on backend boot via
    // JobStateMachine.EnsureStateFoldersAndMigrate. See ADR-0025.
    public const string AutoReview = "4-auto-review";
    public const string HumanReview = "5-human-review";
    public const string Completed = "6-completed";
    public const string Archive = "7-archive";

    public static readonly string[] All =
        [Backlog, Preparation, OrchestratorPrep, NeedsHumanReview, Ready, Progress, FailedPickup, AutoReview, HumanReview, Completed, Archive];

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
    /// <see cref="OrchestratorApi.Services.Jobs.JobStateMachine.EnsureStateFoldersAndMigrate"/>
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
        _ => Preparation
    };

    /// <summary>Returns the display name without the number prefix.</summary>
    public static string DisplayName(string state) =>
        state.Contains('-') ? state[(state.IndexOf('-') + 1)..] : state;
}
