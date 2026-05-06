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
    /// <summary>Auto-commit produced on the progress→review transition; null when no commit recorded.</summary>
    public JobCommitInfo? Commit { get; init; }
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
    public string? StatusMarkdown { get; init; }
    public ContextUsageSnapshot? ContextUsage { get; init; }
    public List<JobLogEntry> Log { get; init; } = [];
    public JobSummaryState? SummaryState { get; init; }
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
    public string Agent { get; init; } = "copilot";
    public string WatchPath { get; init; } = "";
    public string? PromptMarkdown { get; init; }
    public string? Model { get; init; }
    public string? TargetState { get; init; }
    /// <summary>Optional CLI backend (copilot|claude|codex). Defaults to copilot when omitted.</summary>
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
    /// <summary>When true, transition <c>3-progress → 4-review</c> auto-commits and stamps the SHA on the job.</summary>
    public bool AutoCommit { get; init; }

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

public record SetAutoCommitRequest
{
    public bool Enabled { get; init; }
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
        [Preparation, OrchestratorPrep, NeedsHumanReview, Ready, Progress, FailedPickup, AutoReview, HumanReview, Completed, Archive];

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
