using System.Diagnostics;

namespace AgentStudio.Retention;

public sealed record RetentionGitCommitResult(
    bool Success,
    bool DidCommit,
    string? Sha,
    string? Error);

public sealed class RetentionGitCommitter
{
    private readonly ArtifactClassifier _classifier = new();

    public string? ResolveRepositoryRoot(string workspaceRoot)
    {
        var result = RunGit(workspaceRoot, ["rev-parse", "--show-toplevel"]);
        return result.Code == 0 && !string.IsNullOrWhiteSpace(result.Output)
            ? Path.GetFullPath(result.Output.Trim())
            : null;
    }

    public RetentionGitCommitResult CommitProject(
        string workspaceRoot,
        string project,
        int archivedTasks,
        long archivedBytes,
        string? message = null)
    {
        var gitRootResult = RunGit(workspaceRoot, ["rev-parse", "--show-toplevel"]);
        if (gitRootResult.Code != 0)
            return new(false, false, null, "workspace is not a Git repository");
        var gitRoot = Path.GetFullPath(gitRootResult.Output.Trim());
        var pathspec = Path.GetRelativePath(gitRoot, Path.Combine(workspaceRoot, "projects", project))
            .Replace('\\', '/');
        if (pathspec.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(pathspec))
            return new(false, false, null, "project path is outside the Git repository");

        lock (RepositoryMutationGate.For(gitRoot))
        {
            var oversize = ChangedOversizeFiles(gitRoot, pathspec);
            if (oversize.Count > 0)
                return new(false, false, null, $"artifact-oversize-refused: {string.Join(", ", oversize)}");
            var add = RunGit(gitRoot, ["add", "-A", "--", pathspec]);
            if (add.Code != 0)
                return new(false, false, null, add.Error);
            var diff = RunGit(gitRoot, ["diff", "--cached", "--quiet", "--", pathspec]);
            if (diff.Code == 0)
                return new(true, false, null, null);
            if (diff.Code != 1)
                return new(false, false, null, diff.Error);
            var commitMessage = message ?? $"retention: archived {archivedTasks} tasks, {archivedBytes} bytes";
            var commit = RunGit(gitRoot,
                ["-c", "user.name=agent-orchestrator", "-c", "user.email=agent-orchestrator@local",
                    "commit", "-m", commitMessage, "--", pathspec]);
            if (commit.Code != 0)
                return new(false, false, null, commit.Error);
            var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
            return new(true, true, sha.Code == 0 ? sha.Output.Trim() : null, null);
        }
    }

    public RetentionGitCommitResult CommitRuntimeExclusions(string workspaceRoot)
    {
        var gitRootResult = RunGit(workspaceRoot, ["rev-parse", "--show-toplevel"]);
        if (gitRootResult.Code != 0)
            return new(false, false, null, "workspace is not a Git repository");
        var gitRoot = Path.GetFullPath(gitRootResult.Output.Trim());
        if (!Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar)
                .Equals(gitRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return new(false, false, null, "runtime exclusions require the workspace to be the Git root");
        lock (RepositoryMutationGate.For(gitRoot))
        {
            var ignorePath = Path.Combine(gitRoot, ".gitignore");
            var existing = File.Exists(ignorePath) ? File.ReadAllLines(ignorePath).ToList() : [];
            var changed = false;
            foreach (var rule in new[] { "/logs/bus/", "/.metadata/attempt-authority*" })
            {
                if (existing.Any(line => line.Trim().Equals(rule, StringComparison.Ordinal)))
                    continue;
                existing.Add(rule);
                changed = true;
            }
            if (changed)
                File.WriteAllText(ignorePath, string.Join(Environment.NewLine, existing) + Environment.NewLine);
            var trackedBus = RunGit(gitRoot, ["ls-files", "--", "logs/bus"]);
            var hadTrackedBus = trackedBus.Code == 0 && !string.IsNullOrWhiteSpace(trackedBus.Output);
            var untrack = RunGit(gitRoot, ["rm", "-r", "--cached", "--ignore-unmatch", "--", "logs/bus"]);
            if (untrack.Code != 0)
                return new(false, false, null, untrack.Error);
            var add = RunGit(gitRoot, ["add", "--", ".gitignore"]);
            if (add.Code != 0)
                return new(false, false, null, add.Error);
            var paths = hadTrackedBus ? new[] { ".gitignore", "logs/bus" } : [".gitignore"];
            var diff = RunGit(gitRoot, ["diff", "--cached", "--quiet", "--", .. paths]);
            if (diff.Code == 0)
                return new(true, false, null, null);
            var commit = RunGit(gitRoot,
                ["-c", "user.name=agent-orchestrator", "-c", "user.email=agent-orchestrator@local",
                    "commit", "-m", "retention: untrack runtime artifacts", "--", .. paths]);
            if (commit.Code != 0)
                return new(false, false, null, commit.Error);
            var sha = RunGit(gitRoot, ["rev-parse", "--short", "HEAD"]);
            return new(true, true, sha.Code == 0 ? sha.Output.Trim() : null, null);
        }
    }

    private IReadOnlyList<string> ChangedOversizeFiles(string gitRoot, string pathspec)
    {
        var status = RunGit(gitRoot, ["status", "--porcelain=v1", "-z", "--", pathspec]);
        if (status.Code != 0)
            return [];
        var refused = new List<string>();
        foreach (var item in status.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (item.Length <= 3)
                continue;
            var relative = item[3..];
            var path = Path.Combine(gitRoot, relative);
            if (!File.Exists(path))
                continue;
            var bytes = new FileInfo(path).Length;
            if (_classifier.IsCommitRefused(relative, bytes))
                refused.Add($"{relative} ({bytes} bytes)");
        }
        return refused;
    }

    private static GitResult RunGit(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null)
            return new GitResult(-1, string.Empty, "could not start git");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitResult(process.ExitCode, output, error.Trim());
    }

    private sealed record GitResult(int Code, string Output, string Error);
}
