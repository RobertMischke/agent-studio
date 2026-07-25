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
    {
        var value = string.Join('\n',
            repository.ProjectId.Trim(),
            repository.RepositoryUrl.Trim(),
            repository.RepositoryUrl.Trim(),
            repository.DefaultBranch.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
