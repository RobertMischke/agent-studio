using System.Text.RegularExpressions;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

/// <summary>
/// Compatibility parsing and display naming for structured commit repository
/// metadata. The message prefix is read only while migrating legacy records and
/// never participates in current attribution or integration decisions.
/// </summary>
internal static partial class CommitRepositoryMetadata
{
    [GeneratedRegex(@"^\[(?<repository>[^\]\r\n]+)\]\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LegacyPrefixPattern();

    internal static string? LegacyRepositoryPrefix(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var match = LegacyPrefixPattern().Match(message);
        return match.Success ? match.Groups["repository"].Value.Trim() : null;
    }

    internal static string Label(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return "repository";
        var value = repository.Trim().TrimEnd('/', '\\');
        var separator = Math.Max(value.LastIndexOf('/'), Math.Max(value.LastIndexOf('\\'), value.LastIndexOf(':')));
        var label = separator >= 0 ? value[(separator + 1)..] : value;
        if (label.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) label = label[..^4];
        return string.IsNullOrWhiteSpace(label) || label.StartsWith("repo_", StringComparison.Ordinal)
            ? "repository"
            : label;
    }

    internal static bool Matches(ProjectRecord project, string repository)
    {
        if (string.IsNullOrWhiteSpace(repository)) return false;
        var candidate = repository.Trim();
        var url = RepositoryUrl(project);
        var identity = RepositoryIdentityContract.FromUrl(url);
        return string.Equals(candidate, identity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, url, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, project.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, project.DisplayName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, Label(url), StringComparison.OrdinalIgnoreCase);
    }

    internal static string? RepositoryUrl(ProjectRecord project) => project.Urls
        .FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repository", StringComparison.OrdinalIgnoreCase))
        ?.Url?.Trim();
}
