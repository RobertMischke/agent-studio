namespace OrchestratorApi.Models;

public record JobInfo
{
    public string Id { get; init; } = "";
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
}

public record JobDetail
{
    public JobInfo Info { get; init; } = new();
    public string? PromptMarkdown { get; init; }
    public string? StatusMarkdown { get; init; }
    public List<JobLogEntry> Log { get; init; } = [];
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

public record CreateJobRequest
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public int Order { get; init; } = 999;
    public string Agent { get; init; } = "copilot";
    public string WatchPath { get; init; } = "";
    public string? PromptMarkdown { get; init; }
}

public record ReorderRequest
{
    public List<string> JobIds { get; init; } = [];
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

public record WatchPathEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string RootPath { get; init; } = "";
}

public record CliExecution
{
    public string JobId { get; init; } = "";
    public int ProcessId { get; init; }
    public DateTime StartedAt { get; init; }
    public string Status { get; init; } = "";      // running | completed | failed | cancelled
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
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

    public static readonly string[] All = [Preparation, Ready, Progress, Review, Completed];

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
