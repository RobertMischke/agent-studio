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

public record CliOutputLine
{
    public DateTime Timestamp { get; init; }
    public string Stream { get; init; } = "stdout";  // stdout | stderr
    public string Text { get; init; } = "";
}

public static class JobStates
{
    public const string Preparation = "1-preparation";
    public const string Ready = "2-ready";
    public const string Progress = "3-progress";
    public const string Review = "4-review";
    public const string Completed = "5-completed";
    public const string Archive = "6-archive";

    public static readonly string[] All = [Preparation, Ready, Progress, Review, Completed, Archive];

    /// <summary>Maps old unnumbered folder names to new numbered ones.</summary>
    public static readonly Dictionary<string, string> LegacyFolderMap = new()
    {
        ["preparation"] = Preparation,
        ["ready"] = Ready,
        ["progress"] = Progress,
        ["review"] = Review,
        ["completed"] = Completed,
    };

    public static string MapLegacyState(string state) => state switch
    {
        "draft" => Preparation,
        "running" => Progress,
        "review-needed" => Review,
        "accepted" => Completed,
        "rejected" => Completed,
        "archived" => Completed,
        _ => Preparation
    };

    /// <summary>Returns the display name without the number prefix.</summary>
    public static string DisplayName(string state) =>
        state.Contains('-') ? state[(state.IndexOf('-') + 1)..] : state;
}
