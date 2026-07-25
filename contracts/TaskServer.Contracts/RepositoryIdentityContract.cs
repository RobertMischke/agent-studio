using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Canonical identity for a materializable Git repository.
/// Project handles identify task-board projects and must never be used as
/// repository identities in run, result, or review subjects.
/// </summary>
public static class RepositoryIdentityContract
{
    public static string? FromUrl(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl)) return null;
        var canonical = repositoryUrl.Trim().TrimEnd('/').ToLowerInvariant();
        return "repo_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
