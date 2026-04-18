namespace OrchestratorApi.Models;

public record JobInfo
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = "draft";
    public string Priority { get; init; } = "normal";
    public string Agent { get; init; } = "";
    public DateTime CreatedAt { get; init; }
    public string WatchPath { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public DateTime LastActivity { get; init; }
    public long TotalSizeBytes { get; init; }
}

public record JobDetail
{
    public JobInfo Info { get; init; } = new();
    public string? PromptMarkdown { get; init; }
    public string? StatusMarkdown { get; init; }
    public string? ReviewMarkdown { get; init; }
    public JobMetrics? Metrics { get; init; }
    public List<string> Artifacts { get; init; } = [];
    public List<string> Screenshots { get; init; } = [];
    public List<string> Logs { get; init; } = [];
    public List<JobTimelineEntry> Timeline { get; init; } = [];
}

public record JobMetrics
{
    public int DurationMinutes { get; init; }
    public int FilesChanged { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    public int ScreenshotsProduced { get; init; }
    public bool AcceptedFirstTry { get; init; }
    public int ReworkCount { get; init; }
    public bool? BuildSuccess { get; init; }
    public bool? TestSuccess { get; init; }
}

public record JobTimelineEntry
{
    public DateTime Timestamp { get; init; }
    public string Event { get; init; } = "";
    public string? Detail { get; init; }
}

public record MoveJobRequest
{
    public string TargetState { get; init; } = "";
}

public static class JobStates
{
    public const string Preparation = "preparation";
    public const string Ready = "ready";
    public const string Progress = "progress";
    public const string Review = "review";
    public const string Completed = "completed";

    public static readonly string[] All = [Preparation, Ready, Progress, Review, Completed];

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
}
