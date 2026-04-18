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

public record UpdateStateRequest
{
    public string State { get; init; } = "";
}

public static class JobStates
{
    public const string Draft = "draft";
    public const string Running = "running";
    public const string ReviewNeeded = "review-needed";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
    public const string Archived = "archived";

    public static readonly string[] All = [Draft, Running, ReviewNeeded, Accepted, Rejected, Archived];

    public static string Categorize(string state) => state switch
    {
        Running => "active",
        ReviewNeeded => "review",
        Accepted or Archived => "completed",
        Rejected => "failed",
        Draft => "idle",
        _ => "idle"
    };
}
