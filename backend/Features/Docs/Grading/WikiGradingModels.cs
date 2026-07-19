namespace AgentStudio.Docs;

/// <summary>
/// Lifecycle of a wiki-grading maintenance run (AGT-2051). A project has at most
/// one run in flight; the state machine is
/// <c>Running -&gt; (Completed | Aborted | Failed)</c>.
/// </summary>
public enum WikiGradingRunState
{
    Running,
    Completed,
    Aborted,
    Failed,
}

/// <summary>
/// One page's outcome within a run, for the compact "recent" tail the status
/// endpoint surfaces so the UI can show what just happened without streaming.
/// </summary>
public enum WikiGradeOutcome
{
    Graded,
    Skipped,
    Failed,
}

/// <summary>
/// Parameters chosen at the trigger for a grading run. Model + level are the
/// operator's pick (pre-filled from the workspace maintenance default);
/// <see cref="Force"/> re-grades even pages whose fingerprint is unchanged, and
/// <see cref="Limit"/> caps the page count for a cheap probe (0 = all pages).
/// </summary>
public sealed record WikiGradingRunRequest(
    string CliType,
    string Model,
    string? ThinkingLevel,
    bool Force = false,
    int Limit = 0);

/// <summary>The page content handed to a grader for one page.</summary>
public sealed record WikiPageGradeInput(
    string ProjectName,
    string RelPath,
    string Title,
    string Content,
    string SourceHash);

/// <summary>
/// A grader's verdict for one page. <see cref="Ok"/> is true when a real model
/// reply was parsed; a failed CLI call or unparseable reply lands as
/// <c>Ok=false</c> with <see cref="Grade"/> = <c>unknown</c> and a populated
/// <see cref="Error"/> so the run can record an honest failure.
/// </summary>
public sealed record WikiPageGradeVerdict(
    string Grade,
    string Assessment,
    bool? Outdated,
    bool? Contradictory,
    bool? Gaps,
    IReadOnlyList<string> Notes,
    bool Ok,
    string? Error)
{
    public static WikiPageGradeVerdict Fail(string error) =>
        new("unknown", "The grader produced no usable verdict.", null, null, null, [], false, error);

    /// <summary>True when the verdict marks the page as needing attention (C/D).</summary>
    public bool IsCritical =>
        string.Equals(Grade, "C", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Grade, "D", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Per-page result recorded by the run loop.</summary>
public sealed record WikiPageGradeResult(
    string RelPath,
    string Grade,
    WikiGradeOutcome Outcome,
    string? Error);

/// <summary>
/// Snapshot of a run for the status endpoint: progress counters, the current
/// page, and a short tail of recent per-page outcomes. Immutable; the service
/// rebuilds it on every read from the mutable run handle under a lock.
/// </summary>
public sealed record WikiGradingRunStatus(
    string ProjectName,
    string RunId,
    WikiGradingRunState State,
    string CliType,
    string Model,
    string? ThinkingLevel,
    bool Force,
    int Total,
    int Processed,
    int Graded,
    int Skipped,
    int Failed,
    int Critical,
    string? CurrentRelPath,
    string StartedAtUtc,
    string? CompletedAtUtc,
    string? Error,
    IReadOnlyList<WikiGradingRunItem> Recent);

/// <summary>One row in the run's recent-outcome tail.</summary>
public sealed record WikiGradingRunItem(string RelPath, string Grade, string Outcome);
