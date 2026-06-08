namespace OrchestratorApi.Services.RegressionRadar;

/// <summary>
/// Classification of a spec file change within a task's attributed commits.
/// </summary>
public enum SpecChangeCategory
{
    /// <summary>New spec or spec changed alongside its companion implementation.</summary>
    Intended,
    /// <summary>Spec assertions changed without a matching implementation change - needs review.</summary>
    AtRisk,
    /// <summary>Spec deleted or renamed without a replacement in the diff.</summary>
    Drift
}

/// <summary>
/// One spec file entry in the regression radar result.
/// </summary>
public sealed record SpecChangeEntry
{
    /// <summary>Task id that owns this entry when the result is project-scoped.</summary>
    public string? JobId { get; init; }
    /// <summary>Task title that owns this entry when the result is project-scoped.</summary>
    public string? JobTitle { get; init; }
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    /// <summary>Git status letter: A (added), D (deleted), M (modified), R (renamed).</summary>
    public string GitStatus { get; init; } = "";
    public SpecChangeCategory Category { get; init; }
    public string Reason { get; init; } = "";
    /// <summary>Path to the companion implementation file (if resolved).</summary>
    public string? CompanionPath { get; init; }
    /// <summary>True when the companion was also changed in the same attributed commit set.</summary>
    public bool CompanionChanged { get; init; }
    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }
    /// <summary>Operator override: null when no override, otherwise the operator-set category.</summary>
    public SpecChangeCategory? OverrideCategory { get; init; }
    /// <summary>Operator override justification text.</summary>
    public string? OverrideReason { get; init; }
}

/// <summary>
/// Aggregate regression radar result for a task.
/// </summary>
public sealed record RegressionRadarResult
{
    /// <summary>Overall status: the worst category across all entries.</summary>
    public SpecChangeCategory OverallStatus { get; init; }
    public int IntendedCount { get; init; }
    public int AtRiskCount { get; init; }
    public int DriftCount { get; init; }
    public int TotalSpecChanges { get; init; }
    /// <summary>First attributed commit SHA used for the compact analysis label.</summary>
    public string? BaselineSha { get; init; }
    /// <summary>Last attributed commit SHA used for the compact analysis label.</summary>
    public string? HeadSha { get; init; }
    public List<SpecChangeEntry> Entries { get; init; } = [];
    /// <summary>Project-scoped grouping of entries by task; empty for task-scoped responses.</summary>
    public List<RegressionRadarTaskGroup> TaskGroups { get; init; } = [];
    /// <summary>Non-null when the analysis could not run (no repo, no commits, etc.).</summary>
    public string? Error { get; init; }
    /// <summary>UTC timestamp when this analysis was generated.</summary>
    public DateTime GeneratedAt { get; init; }
    /// <summary>Wall-clock time the analysis took to produce, in milliseconds.</summary>
    public long DurationMs { get; init; }
}

public sealed record RegressionRadarTaskGroup
{
    public string JobId { get; init; } = "";
    public string JobTitle { get; init; } = "";
    public string State { get; init; } = "";
    public int IntendedCount { get; init; }
    public int AtRiskCount { get; init; }
    public int DriftCount { get; init; }
    public int TotalSpecChanges { get; init; }
    public List<SpecChangeEntry> Entries { get; init; } = [];
}
