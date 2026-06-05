using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// The outcome of the read-only containment check. <see cref="IsViolation"/> is
/// true only when a planning / research run left a non-empty working-tree diff -
/// the read-only pipeline omits the git pre/post steps, so any change at run end
/// is a file the agent should not have written. <see cref="FileList"/> is the
/// display-capped, comma-joined path list (with a "+N more" suffix when the diff
/// exceeds <see cref="ReadOnlyContainmentPolicy.MaxInlinedFiles"/>);
/// <see cref="ChangedFiles"/> is the full, uncapped count - the load-bearing
/// signal the ledger row carries.
/// </summary>
public sealed record ReadOnlyContainment(
    bool IsViolation, string Summary, string FileList, int ChangedFiles)
{
    public static readonly ReadOnlyContainment None =
        new(false, string.Empty, string.Empty, 0);
}

/// <summary>
/// Pure decision for ADR-0052's "containment over trust" rule: a read-only task
/// mode (planning / research) runs without the git steps, so a dirty tree at run
/// end is a hard containment violation - reported, never auto-reverted. This
/// helper isolates the decision (mode + git status -> violation?) from the
/// runner's side effects (timeline event, chat note, warning log) so the rule is
/// unit-testable without standing up a <c>ProjectRunner</c>. The runner keeps the
/// cheap mode short-circuit before it pays for a <c>git status</c>; this policy
/// re-checks the mode defensively.
/// </summary>
public static class ReadOnlyContainmentPolicy
{
    /// <summary>
    /// Cap on the number of paths inlined into <see cref="ReadOnlyContainment.FileList"/>
    /// so a runaway diff cannot bloat the timeline row. The count stays accurate.
    /// </summary>
    public const int MaxInlinedFiles = 20;

    public static ReadOnlyContainment Evaluate(
        string? mode, bool isRepo, IReadOnlyList<string> changedFiles)
    {
        // No containment concern for coding mode, a non-repo path, or a clean tree.
        if (!TaskModes.IsReadOnly(mode)) return ReadOnlyContainment.None;
        if (!isRepo || changedFiles.Count == 0) return ReadOnlyContainment.None;

        var fileList = string.Join(", ", changedFiles.Take(MaxInlinedFiles));
        if (changedFiles.Count > MaxInlinedFiles)
            fileList += $", +{changedFiles.Count - MaxInlinedFiles} more";

        var summary =
            $"Read-only {mode} run left {changedFiles.Count} changed file(s) - " +
            "containment violation (not auto-reverted)";

        return new ReadOnlyContainment(true, summary, fileList, changedFiles.Count);
    }
}
