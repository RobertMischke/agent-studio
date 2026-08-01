using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Tasks;

/// <summary>
/// Stable identity of the repository registration a host proved. Any change
/// to the project, fetch/push URL, or requested base branch creates a cache
/// miss before the next card can be leased.
/// </summary>
public static class ProjectDeliveryPreflightFingerprint
{
    public static string Create(RemoteProjectRepository repository)
        => Create(
            repository.ProjectId,
            repository.RepositoryUrl,
            repository.DefaultBranch);

    public static string CreateUnconfigured(string projectId, string targetBranch)
        => Create(projectId, "", targetBranch);

    private static string Create(string projectId, string repositoryUrl, string targetBranch)
    {
        var value = string.Join('\n',
            projectId.Trim(),
            repositoryUrl.Trim(),
            repositoryUrl.Trim(),
            targetBranch.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

/// <summary>
/// A repository permission or branch can change without a project settings
/// write. Cached delivery proofs therefore have a bounded lifetime.
/// </summary>
public static class ProjectDeliveryPreflightPolicy
{
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    public static bool IsFresh(RunnerProjectPreflight preflight, DateTime now) =>
        preflight.CheckedAt <= now.AddMinutes(1)
        && now - preflight.CheckedAt <= FreshFor;
}
