namespace AgentStudio.Shared;

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
    /// Confidence of an automatic attribution (0..1). Null for legacy
    /// entries. The frontend renders a small badge when this is present so
    /// the operator can see where the system was uncertain.
    /// </summary>
    public double? Confidence { get; init; }
}

/// <summary>
/// One commit that the deterministic attribution rule subtracted from a
/// task's commit set (see ADR "Commit-Attribution-Regel"). An internal
/// rule-engine value: the engine emits these so the post-step can log how
/// many commits it withheld and why. Not persisted onto <see cref="TaskInfo"/>
/// and not surfaced in the UI.
/// </summary>
public record TaskExcludedCommitInfo
{
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";
    /// <summary>One of <see cref="CommitExclusionReasons"/>. Free-form on read.</summary>
    public string Reason { get; init; } = CommitExclusionReasons.Other;
    /// <summary>Commit subject (first line). Optional.</summary>
    public string? Subject { get; init; }
    public DateTime At { get; init; }
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
    /// <summary>
    /// Legacy entry without explicit attribution (job.json pre-dates the
    /// attribution step). Treated as "trust the existing stamp" by readers.
    /// </summary>
    public const string Legacy = "legacy";

    public static readonly string[] All = [Automatic, Legacy];

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
    /// <summary>Unrecognized exclusion reason.</summary>
    public const string Other = "other";

    public static readonly string[] All =
        [CrashRecoveryOfOtherTask, UpdateStableBump, MergeCommit, OutsideTaskWindow, Other];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Other;
        var v = value.Trim();
        foreach (var r in All)
            if (string.Equals(r, v, StringComparison.OrdinalIgnoreCase)) return r;
        return Other;
    }
}
