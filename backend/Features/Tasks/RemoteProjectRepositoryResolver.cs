using System.Text.RegularExpressions;

namespace AgentStudio.Tasks;

/// <summary>
/// Resolves the git origin and default branch a remote runner needs from the
/// durable project registry. A URL entry named <c>repo</c> is authoritative;
/// otherwise the origin and branch are derived from the registered local
/// repository checkout.
/// </summary>
public static class RemoteProjectRepositoryResolver
{
    private static readonly Regex ScpStyleRemote = new(
        @"^[^\s/@:]+@[^\s/:]+:.+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RemoteProjectRepository? Resolve(ProjectRecord? project, string? configuredDefaultBranch)
    {
        if (project is null) return null;

        var repoEntry = project.Urls.FirstOrDefault(url =>
            string.Equals(url.Id, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repo", StringComparison.OrdinalIgnoreCase)
            || string.Equals(url.Label, "repository", StringComparison.OrdinalIgnoreCase));

        var repositoryUrl = IsUsableRemote(repoEntry?.Url) ? repoEntry!.Url.Trim() : null;
        var source = repositoryUrl is null ? "repository-path" : "registry-url";
        var gitDirectory = ResolveGitDirectory(project.RepositoryPath);

        repositoryUrl ??= ReadOriginUrl(gitDirectory);
        if (!IsUsableRemote(repositoryUrl)) return null;
        var resolvedUrl = repositoryUrl!;

        var defaultBranch = ReadDefaultBranch(gitDirectory);
        if (string.IsNullOrWhiteSpace(defaultBranch))
            defaultBranch = string.IsNullOrWhiteSpace(configuredDefaultBranch)
                ? "main"
                : configuredDefaultBranch.Trim();

        return new RemoteProjectRepository(
            project.Id,
            resolvedUrl.Trim(),
            defaultBranch,
            source);
    }

    private static string? ResolveGitDirectory(string? repositoryPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return null;

        var dotGit = Path.Combine(repositoryPath, ".git");
        if (Directory.Exists(dotGit)) return dotGit;
        if (!File.Exists(dotGit)) return null;

        var pointer = File.ReadLines(dotGit).FirstOrDefault();
        const string prefix = "gitdir:";
        if (pointer is null || !pointer.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var path = pointer[prefix.Length..].Trim();
        return Path.GetFullPath(path, repositoryPath);
    }

    private static string? ReadOriginUrl(string? gitDirectory)
    {
        if (gitDirectory is null) return null;
        var configPath = Path.Combine(gitDirectory, "config");
        if (!File.Exists(configPath)) return null;

        var inOrigin = false;
        foreach (var raw in File.ReadLines(configPath))
        {
            var line = raw.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                inOrigin = string.Equals(line, "[remote \"origin\"]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inOrigin) continue;
            var separator = line.IndexOf('=');
            if (separator < 0 || !string.Equals(line[..separator].Trim(), "url", StringComparison.OrdinalIgnoreCase))
                continue;
            return line[(separator + 1)..].Trim();
        }

        return null;
    }

    private static string? ReadDefaultBranch(string? gitDirectory)
    {
        if (gitDirectory is null) return null;

        var remoteHead = ReadSymbolicRef(Path.Combine(gitDirectory, "refs", "remotes", "origin", "HEAD"), "refs/remotes/origin/");
        if (!string.IsNullOrWhiteSpace(remoteHead)) return remoteHead;

        return ReadSymbolicRef(Path.Combine(gitDirectory, "HEAD"), "refs/heads/");
    }

    private static string? ReadSymbolicRef(string path, string prefix)
    {
        if (!File.Exists(path)) return null;
        var value = File.ReadLines(path).FirstOrDefault()?.Trim();
        const string refPrefix = "ref:";
        if (value is null || !value.StartsWith(refPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var reference = value[refPrefix.Length..].Trim();
        return reference.StartsWith(prefix, StringComparison.Ordinal)
            ? reference[prefix.Length..]
            : null;
    }

    private static bool IsUsableRemote(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var trimmed = value.Trim();
        if (ScpStyleRemote.IsMatch(trimmed)) return true;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "https" or "http" or "ssh" or "git";
    }
}

public sealed record RemoteProjectRepository(
    string ProjectId,
    string RepositoryUrl,
    string DefaultBranch,
    string Source);
