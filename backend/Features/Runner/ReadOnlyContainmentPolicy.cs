
namespace AgentStudio.Runner;

/// <summary>
/// The outcome of the read-only containment check. <see cref="IsViolation"/> is
/// true when a planning / research run left any working-tree diff, or when a
/// concept run changed anything outside one <c>docs/&lt;topic&gt;/</c>
/// directory. <see cref="FileList"/> is the
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
/// mode runs without product-code git steps. Planning/research must be clean;
/// concept may carry one bounded repository-dossier diff. Any other dirty tree
/// is a hard containment violation - reported, never auto-reverted. This
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

        var violatingFiles = changedFiles;
        if (TaskModes.IsConcept(mode))
        {
            var normalized = changedFiles
                .Select(path => path.Replace('\\', '/').TrimStart('/'))
                .ToList();
            var outside = normalized
                .Where(path => !path.StartsWith(
                    AgentStudio.Pipeline.ConceptWorkbenchContract.DossierPrefix,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            var topicRoots = normalized
                .Where(path => path.StartsWith(
                    AgentStudio.Pipeline.ConceptWorkbenchContract.DossierPrefix,
                    StringComparison.OrdinalIgnoreCase))
                .Select(path =>
                {
                    var remainder = path[AgentStudio.Pipeline.ConceptWorkbenchContract.DossierPrefix.Length..];
                    var slash = remainder.IndexOf('/');
                    return slash <= 0 ? path : remainder[..slash];
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (outside.Count == 0 && topicRoots.Count == 1)
                return ReadOnlyContainment.None;
            violatingFiles = outside.Count > 0 ? outside : normalized;
        }

        var fileList = string.Join(", ", violatingFiles.Take(MaxInlinedFiles));
        if (violatingFiles.Count > MaxInlinedFiles)
            fileList += $", +{violatingFiles.Count - MaxInlinedFiles} more";

        var summary = TaskModes.IsConcept(mode)
            ? $"Read-only concept run left {violatingFiles.Count} disallowed changed file(s) - containment violation (not auto-reverted)"
            : $"Read-only {mode} run left {changedFiles.Count} changed file(s) - containment violation (not auto-reverted)";

        return new ReadOnlyContainment(true, summary, fileList, changedFiles.Count);
    }
}
