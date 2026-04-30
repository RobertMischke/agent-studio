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
    public long TotalSizeBytes { get; init; }
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
}

public record SessionUsage
{
    public DateTime At { get; init; }
    public string? Tokens { get; init; }
    public string? Changes { get; init; }
    public string? Requests { get; init; }
}

public record JobDetail
{
    public JobInfo Info { get; init; } = new();
    public string? PromptMarkdown { get; init; }
    public string? StatusMarkdown { get; init; }
    public ContextUsageSnapshot? ContextUsage { get; init; }
    public List<JobLogEntry> Log { get; init; } = [];
    public JobSummaryState? SummaryState { get; init; }
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
}

public record SetAutoCommitRequest
{
    public bool Enabled { get; init; }
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
