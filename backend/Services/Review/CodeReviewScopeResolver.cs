namespace OrchestratorApi.Services.Review;

/// <summary>
/// How a code-review pass should scope the diff it reviews.
/// </summary>
public enum CodeReviewScopeMode
{
    /// <summary>Review exactly one commit (explicit override or a single task commit).</summary>
    SingleCommit,

    /// <summary>Review the combined diff of every commit the task owns.</summary>
    AggregateCommits,

    /// <summary>No commit could be resolved; fall back to the live working-tree diff.</summary>
    WorkingTree,
}

/// <summary>
/// The resolved review scope: which mode, the SHAs it covers, and a
/// human-readable label naming the reviewed commit range.
/// </summary>
public sealed record CodeReviewScope(
    CodeReviewScopeMode Mode,
    IReadOnlyList<string> Shas,
    string Label);

/// <summary>
/// Pure decision for which commits a user-triggered code review should
/// inspect. The historical default reviewed only HEAD, so a task whose
/// feature landed in an earlier commit and whose HEAD was a later
/// test/doc-only commit was wrongly judged "not implemented" (ASS-794).
///
/// <para>
/// New default: review the <b>aggregate diff of every commit the task
/// owns</b>, mirroring the per-task scoping the regression-radar and the
/// protocol-pane change-set use. An explicit <c>body.Commit</c> still
/// pins the review to that single commit. When the task has no attributed
/// commits yet, we fall back to HEAD (and finally the working tree) so the
/// endpoint never produces an empty review.
/// </para>
///
/// <para>
/// Kept pure (no git I/O) so the scope rules are unit-testable without a
/// repository; the endpoint fetches the actual diff text from the resolved
/// mode + SHAs.
/// </para>
/// </summary>
public static class CodeReviewScopeResolver
{
    public static CodeReviewScope Resolve(string? overrideCommit, IReadOnlyList<string>? taskShas, string? headSha)
    {
        // Explicit single-commit override always wins.
        if (!string.IsNullOrWhiteSpace(overrideCommit))
        {
            var ov = overrideCommit!.Trim();
            return new CodeReviewScope(CodeReviewScopeMode.SingleCommit, new[] { ov }, ShortLabel(ov));
        }

        var shas = (taskShas ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (shas.Count == 1)
            return new CodeReviewScope(CodeReviewScopeMode.SingleCommit, shas, ShortLabel(shas[0]));

        if (shas.Count > 1)
            return new CodeReviewScope(CodeReviewScopeMode.AggregateCommits, shas, AggregateLabel(shas));

        // No commits attributed to the task yet: fall back to HEAD so the
        // behaviour degrades to the historical single-commit review rather
        // than producing nothing.
        if (!string.IsNullOrWhiteSpace(headSha))
        {
            var head = headSha!.Trim();
            return new CodeReviewScope(CodeReviewScopeMode.SingleCommit, new[] { head }, ShortLabel(head) + " (HEAD)");
        }

        return new CodeReviewScope(CodeReviewScopeMode.WorkingTree, Array.Empty<string>(), "working tree");
    }

    private static string ShortLabel(string sha) =>
        sha.Length > 8 ? sha[..8] : sha;

    private static string AggregateLabel(IReadOnlyList<string> shas)
    {
        var shorts = shas.Select(ShortLabel).ToList();
        return $"{shas.Count} task commits ({string.Join(", ", shorts)})";
    }
}
