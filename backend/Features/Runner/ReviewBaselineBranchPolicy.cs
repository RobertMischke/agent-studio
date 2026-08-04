using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>Where the integration line used for a review baseline came from.</summary>
public enum ReviewBaselineBranchSource
{
    /// <summary>The project's configured integration branch (the delivery target).</summary>
    ProjectSetting,
    /// <summary>The registered checkout's <c>origin/HEAD</c>.</summary>
    RepositoryDefault,
    /// <summary>The branch recorded on the card when the worktree was prepared.</summary>
    TaskCard,
    /// <summary>Nothing was known; the product default applies.</summary>
    Fallback,
}

/// <summary>
/// The integration line a review baseline is resolved against, plus whether the
/// card's own <c>integrationBranch</c> field still agrees with it.
/// </summary>
public sealed record ReviewBaselineBranchDecision(
    string Branch,
    string IntegrationRef,
    ReviewBaselineBranchSource Source,
    string? CardBranch,
    bool CardOutdated)
{
    /// <summary>One-line rationale for timeline entries and operator logs.</summary>
    public string Rationale => CardOutdated
        ? $"card recorded '{CardBranch}', {SourceLabel} says '{Branch}'"
        : $"{SourceLabel} says '{Branch}'";

    private string SourceLabel => Source switch
    {
        ReviewBaselineBranchSource.ProjectSetting => "project integration branch",
        ReviewBaselineBranchSource.RepositoryDefault => "repository origin/HEAD",
        ReviewBaselineBranchSource.TaskCard => "card integration branch",
        _ => "product default",
    };
}

/// <summary>
/// Decides which integration line a review baseline is computed against.
/// <para>
/// The card's <c>integrationBranch</c> is a snapshot taken when a runner
/// prepared the worktree. It goes stale: AGT-2220 still carried
/// <c>refs/heads/main</c> after develop became the working branch (30.07.), so
/// every baseline was a merge-base against main - an ancient commit the verify
/// commands no longer run on. Four review attempts died on it without ever
/// producing a verdict.
/// </para>
/// <para>
/// So project/repo truth wins over the card, in the same order
/// <see cref="AgentStudio.Tasks.RemoteProjectRepositoryResolver"/> already uses:
/// the configured integration branch is the authoritative delivery target and
/// <c>origin/HEAD</c> is only a fallback for a project that has none. The card
/// is used last and, when it disagrees, reported as outdated so the caller can
/// correct it.
/// </para>
/// </summary>
public static class ReviewBaselineBranchPolicy
{
    /// <summary>Applies when neither project nor repository nor card knows a branch.</summary>
    public const string FallbackBranch = "develop";

    public static ReviewBaselineBranchDecision Decide(
        string? cardBranch,
        string? projectIntegrationBranch,
        string? repositoryDefaultBranch)
    {
        var card = ShortName(cardBranch);
        var project = ShortName(projectIntegrationBranch);
        var repository = ShortName(repositoryDefaultBranch);

        var (branch, source) =
            project is not null ? (project, ReviewBaselineBranchSource.ProjectSetting)
            : repository is not null ? (repository, ReviewBaselineBranchSource.RepositoryDefault)
            : card is not null ? (card, ReviewBaselineBranchSource.TaskCard)
            : (FallbackBranch, ReviewBaselineBranchSource.Fallback);

        // Git branch names are case sensitive, so an ordinal comparison is the
        // only correct staleness test here.
        var outdated = card is not null && !string.Equals(card, branch, StringComparison.Ordinal);
        return new ReviewBaselineBranchDecision(
            branch,
            $"refs/heads/{branch}",
            source,
            card,
            outdated);
    }

    private static string? ShortName(string? value)
    {
        var name = TaskIntegrationBranch.Name(value, fallback: string.Empty);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
