using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Git;

/// <summary>
/// Captures the repository refs that drive board merge/publish projections
/// without spawning git. Supports normal checkouts and linked worktrees.
/// </summary>
internal static class ReadOnlyGitRefFingerprint
{
    public static string Capture(
        string repoRoot,
        IEnumerable<string> branchNames,
        bool includeTags = false)
    {
        var metadata = ResolveMetadataDirectories(repoRoot);
        if (metadata is null) return "missing";

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var paths = new HashSet<string>(comparer)
        {
            Path.Combine(metadata.Worktree, "HEAD"),
            Path.Combine(metadata.Common, "packed-refs"),
            Path.Combine(metadata.Common, "refs", "remotes", "origin", "HEAD"),
        };

        foreach (var branch in branchNames)
            AddBranchPaths(metadata.Common, branch, paths);

        // A configured integration branch may be absent, in which case
        // GitService falls back through origin/HEAD or HEAD. Include the target
        // of those symbolic refs so a commit on that fallback invalidates too.
        AddSymbolicTarget(metadata.Common, Path.Combine(metadata.Worktree, "HEAD"), paths);
        AddSymbolicTarget(
            metadata.Common,
            Path.Combine(metadata.Common, "refs", "remotes", "origin", "HEAD"),
            paths);

        if (includeTags)
        {
            var tagsRoot = Path.Combine(metadata.Common, "refs", "tags");
            try
            {
                if (Directory.Exists(tagsRoot))
                {
                    foreach (var tag in Directory.EnumerateFiles(tagsRoot, "*", SearchOption.AllDirectories))
                        paths.Add(Path.GetFullPath(tag));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: tag enumeration failed");
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, Path.GetFullPath(repoRoot));
        foreach (var path in paths.OrderBy(p => p, comparer))
            AppendFile(hash, metadata.Common, path);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AddBranchPaths(string commonDir, string? branchName, HashSet<string> paths)
    {
        if (string.IsNullOrWhiteSpace(branchName)) return;
        var branch = branchName.Trim().Replace('\\', '/');
        if (branch.StartsWith("refs/heads/", StringComparison.Ordinal))
            branch = branch["refs/heads/".Length..];
        else if (branch.StartsWith("origin/", StringComparison.Ordinal))
            branch = branch["origin/".Length..];

        AddSafeRefPath(commonDir, Path.Combine("refs", "heads"), branch, paths);
        AddSafeRefPath(commonDir, Path.Combine("refs", "remotes", "origin"), branch, paths);
    }

    private static void AddSymbolicTarget(string commonDir, string symbolicRefPath, HashSet<string> paths)
    {
        try
        {
            if (!File.Exists(symbolicRefPath)) return;
            var text = File.ReadAllText(symbolicRefPath).Trim();
            const string prefix = "ref: ";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return;
            AddSafeRelativePath(commonDir, text[prefix.Length..], paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: symbolic ref read failed");
        }
    }

    private static void AddSafeRefPath(
        string commonDir,
        string prefix,
        string branch,
        HashSet<string> paths)
        => AddSafeRelativePath(commonDir, Path.Combine(prefix, branch), paths);

    private static void AddSafeRelativePath(string commonDir, string relativePath, HashSet<string> paths)
    {
        try
        {
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(commonDir);
            var candidate = Path.GetFullPath(Path.Combine(root, normalized));
            var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (candidate.StartsWith(rootPrefix, comparison)) paths.Add(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: invalid ref path");
        }
    }

    private static void AppendFile(IncrementalHash hash, string commonDir, string path)
    {
        Append(hash, Path.GetRelativePath(commonDir, path).Replace('\\', '/'));
        try
        {
            if (!File.Exists(path))
            {
                Append(hash, "missing");
                return;
            }

            var info = new FileInfo(path);
            Append(hash, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}");
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Append(hash, "unreadable");
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: ref read failed");
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static MetadataDirectories? ResolveMetadataDirectories(string repoRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
            var dotGit = Path.Combine(repoRoot, ".git");
            string worktreeDir;
            if (Directory.Exists(dotGit))
            {
                worktreeDir = Path.GetFullPath(dotGit);
            }
            else if (File.Exists(dotGit))
            {
                var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim();
                const string prefix = "gitdir:";
                if (line is null || !line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return null;
                var configured = line[prefix.Length..].Trim();
                worktreeDir = Path.GetFullPath(Path.IsPathRooted(configured)
                    ? configured
                    : Path.Combine(repoRoot, configured));
            }
            else if (File.Exists(Path.Combine(repoRoot, "HEAD")))
            {
                worktreeDir = Path.GetFullPath(repoRoot);
            }
            else
            {
                return null;
            }

            var commonDir = worktreeDir;
            var commonFile = Path.Combine(worktreeDir, "commondir");
            if (File.Exists(commonFile))
            {
                var configured = File.ReadAllText(commonFile).Trim();
                if (configured.Length > 0)
                {
                    commonDir = Path.GetFullPath(Path.IsPathRooted(configured)
                        ? configured
                        : Path.Combine(worktreeDir, configured));
                }
            }
            return new MetadataDirectories(worktreeDir, commonDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SilentCatch.Note(ex, "ReadOnlyGitRefFingerprint: git metadata resolution failed");
            return null;
        }
    }

    private sealed record MetadataDirectories(string Worktree, string Common);
}
