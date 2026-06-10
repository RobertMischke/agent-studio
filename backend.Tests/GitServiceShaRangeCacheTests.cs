using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Covers the SHA-range LRU memo introduced for the
/// "Accept→next-task must be sub-30 ms" caching pass: the answers for a
/// fixed (toplevel, beforeSha, afterSha[, path]) tuple never change, so a
/// second call returns the cached <see cref="List{T}"/> / string instance
/// without re-spawning <c>git</c>.
///
/// The signal is instance identity. The methods always build a fresh
/// List per git call; the cache returns the stored list reference. So a
/// cache hit is provable as <c>ReferenceEquals(first, second) == true</c>
/// without timing assertions that flake on slow CI.
/// </summary>
public class GitServiceShaRangeCacheTests : IDisposable
{
    private readonly string _tempDir;

    public GitServiceShaRangeCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-sha-range-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_tempDir, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            }
            Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public void GetCommitsInShaRange_SecondCallReturnsCachedInstance()
    {
        var (repoRoot, baseSha, headSha) = BuildTwoCommitRepo();
        var git = BuildGitService(("Repo", repoRoot));
        var first = git.GetCommitsInShaRange("any-job-id", repoRoot, baseSha, headSha);
        Assert.Single(first);
        var second = git.GetCommitsInShaRange("any-job-id", repoRoot, baseSha, headSha);
        Assert.True(ReferenceEquals(first, second),
            "Second call must return the cached list instance — that proves git was not re-spawned.");
    }

    [Fact]
    public void GetFilesChangedInShaRange_SecondCallReturnsCachedInstance()
    {
        var (repoRoot, baseSha, headSha) = BuildTwoCommitRepo();
        var git = BuildGitService(("Repo", repoRoot));
        var first = git.GetFilesChangedInShaRange("any-job-id", repoRoot, baseSha, headSha);
        Assert.NotEmpty(first);
        var second = git.GetFilesChangedInShaRange("any-job-id", repoRoot, baseSha, headSha);
        Assert.True(ReferenceEquals(first, second),
            "Second call must return the cached list instance.");
    }

    [Fact]
    public void GetDiffInShaRange_SecondCallReturnsCachedDiffString()
    {
        var (repoRoot, baseSha, headSha) = BuildTwoCommitRepo();
        var git = BuildGitService(("Repo", repoRoot));
        var first = git.GetDiffInShaRange("any-job-id", repoRoot, baseSha, headSha, path: null);
        Assert.False(string.IsNullOrEmpty(first));
        var second = git.GetDiffInShaRange("any-job-id", repoRoot, baseSha, headSha, path: null);
        // Strings are immutable, but .NET will allocate a fresh string per
        // process spawn. ReferenceEquals == true is a clean cache signal.
        Assert.True(ReferenceEquals(first, second),
            "Second call must return the cached diff string instance.");
    }

    [Fact]
    public void DifferentPathsAreSeparateCacheKeys()
    {
        var (repoRoot, baseSha, headSha) = BuildTwoCommitRepo("added.txt", "second.txt");
        var git = BuildGitService(("Repo", repoRoot));
        var diffA = git.GetDiffInShaRange("any-job-id", repoRoot, baseSha, headSha, path: "added.txt");
        var diffB = git.GetDiffInShaRange("any-job-id", repoRoot, baseSha, headSha, path: "second.txt");
        // Different files → different content; cache must not collapse them.
        Assert.NotEqual(diffA, diffB);
    }

    [Fact]
    public void GetCommitDiffResult_PathOutsideCommitIsSuccessfulEmptyDiff()
    {
        var (repoRoot, _, headSha) = BuildTwoCommitRepo("added.txt");
        var git = BuildGitService(("Repo", repoRoot));

        var result = git.GetCommitDiffResult("any-job-id", repoRoot, headSha, path: "not-touched.txt");

        Assert.True(result.Success);
        Assert.Equal("", result.Diff);
        Assert.Null(result.Error);
    }

    [Fact]
    public void GetAggregateCommitDiffResult_UsesOnlyProvidedTaskCommitSet()
    {
        var (repoRoot, _, firstTaskSha) = BuildTwoCommitRepo("task-owned.txt");
        WriteFile(repoRoot, "foreign.txt", "foreign\n");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m foreign");
        var git = BuildGitService(("Repo", repoRoot));

        var result = git.GetAggregateCommitDiffResult(
            "any-job-id",
            repoRoot,
            [firstTaskSha],
            path: "foreign.txt");

        Assert.True(result.Success);
        Assert.Equal("", result.Diff);
        Assert.Null(result.Error);
    }

    private (string repoRoot, string baseSha, string headSha) BuildTwoCommitRepo(
        params string[] extraFilesInSecondCommit)
    {
        var repoRoot = Path.Combine(_tempDir, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(repoRoot, "seed.txt"), "seed");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m seed");
        var baseSha = CurrentSha(repoRoot);

        var files = extraFilesInSecondCommit.Length > 0
            ? extraFilesInSecondCommit
            : new[] { "added.txt" };
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(repoRoot, f), $"content of {f}\nline two\n");
        }
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m add-files");
        var headSha = CurrentSha(repoRoot);
        SeedJob(repoRoot, "any-job-id");
        return (repoRoot, baseSha, headSha);
    }

    private GitService BuildGitService(params (string Name, string RootPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RootPath;
            dict[$"WatchPaths:{i}:Path"] = entries[i].RootPath;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static void SeedJob(string watchPath, string jobId)
    {
        var dir = Path.Combine(watchPath, "3-progress", jobId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), $$"""
        {
          "id": "{{jobId}}",
          "title": "Git fixture job",
          "state": "3-progress",
          "order": 1,
          "agent": "claude",
          "createdAt": "2026-06-09T00:00:00Z"
        }
        """);
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string CurrentSha(string repoRoot)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = "rev-parse HEAD",
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var sha = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(15_000);
        return sha;
    }

    private static void RunGit(string cwd, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        p.WaitForExit(15_000);
    }
}
