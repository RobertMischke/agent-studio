using System.Text.RegularExpressions;
using AgentStudio.Git;
using AgentStudio.Shared;

namespace AgentStudio.Tasks;

public sealed record RemoteCommitAttributionResult(
    bool Accepted,
    IReadOnlyList<TaskCommitInfo> Commits,
    string? Warning);

/// <summary>
/// Converts an exact remote runner branch range into task commit attribution.
/// The whole range is rejected when either the branch belongs to another task
/// or a commit subject explicitly names another task key.
/// </summary>
public static partial class RemoteCommitAttributionGuard
{
    [GeneratedRegex(@"\b[A-Z][A-Z0-9]{1,15}-\d+\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TaskKeyPattern();

    public static RemoteCommitAttributionResult Attribute(
        string taskKey,
        string deliveryBranch,
        IReadOnlyList<GitCommitInfo> commits)
    {
        var expected = taskKey.Trim();
        var branchTaskKey = TaskIntegrationBranch.Name(deliveryBranch)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (!string.Equals(branchTaskKey, expected, StringComparison.OrdinalIgnoreCase))
        {
            return Rejected(
                $"Remote commit attribution rejected: branch '{deliveryBranch}' does not belong to task '{expected}'.");
        }

        foreach (var commit in commits)
        {
            var foreign = TaskKeyPattern().Matches(commit.Subject)
                .Select(match => match.Value)
                .FirstOrDefault(key => !string.Equals(key, expected, StringComparison.OrdinalIgnoreCase));
            if (foreign is not null)
            {
                return Rejected(
                    $"Remote commit attribution rejected for '{expected}': commit {commit.ShortSha} names foreign task '{foreign}'.");
            }
        }

        var attributed = commits.Select(commit => new TaskCommitInfo
        {
            Sha = commit.Sha,
            ShortSha = commit.ShortSha,
            Message = commit.Subject,
            FilesChanged = commit.FilesChanged,
            At = commit.AuthorDateUtc,
            Attribution = CommitAttributionKinds.Automatic,
            Confidence = 1.0,
        }).ToList();
        return new RemoteCommitAttributionResult(true, attributed, null);
    }

    private static RemoteCommitAttributionResult Rejected(string warning)
        => new(false, [], warning);
}
