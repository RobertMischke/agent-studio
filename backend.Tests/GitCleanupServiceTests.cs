using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2009 Git-Management cleanup: real-temp-repo coverage for
/// <see cref="GitCleanupService"/>. Every test drives git against a throwaway
/// repo (develop as the integration branch, merged + unmerged <c>task/*</c>
/// branches, <c>refs/backups/*</c> refs, and a stale worktree registration) so
/// the plan classification and the execute teardown are exercised end to end.
///
/// The load-bearing invariant under test is AGT-1945: only GEMERGTES is ever
/// deleted. The plan must mark unmerged branches / uncontained backup refs
/// ineligible, and execute must refuse to delete them even when the request
/// explicitly names them.
/// </summary>
public sealed class GitCleanupServiceTests : IDisposable
{
    private readonly string _tempDir;

    public GitCleanupServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-cleanup-" + Guid.NewGuid().ToString("N"));
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
    public void BuildPlan_ClassifiesMergedEligible_UnmergedKept()
    {
        var (repo, cleanup) = SetupWithBranches();

        var plan = cleanup.BuildPlan("Demo");

        Assert.True(plan.IsRepo);
        Assert.Equal("develop", plan.IntegrationBranch);

        var merged = Assert.Single(plan.Candidates,
            c => c.Kind == CleanupTargetKind.LocalBranch && c.Name == "task/merged");
        Assert.True(merged.Eligible);
        Assert.Equal(CleanupMergeStatus.Merged, merged.MergeStatus);

        var unmerged = Assert.Single(plan.Candidates,
            c => c.Kind == CleanupTargetKind.LocalBranch && c.Name == "task/unmerged");
        Assert.False(unmerged.Eligible);
        Assert.Equal(CleanupMergeStatus.Unmerged, unmerged.MergeStatus);
        Assert.Contains("AGT-1945", unmerged.Reason);
    }

    [Fact]
    public void BuildPlan_BackupRef_ContainedEligible_UncontainedKept()
    {
        var (repo, cleanup) = SetupWithBranches();
        var developTip = RunGitOut(repo, "rev-parse", "develop").Trim();
        var unmergedTip = RunGitOut(repo, "rev-parse", "task/unmerged").Trim();
        RunGit(repo, "update-ref", "refs/backups/contained", developTip);
        RunGit(repo, "update-ref", "refs/backups/orphan", unmergedTip);

        var plan = cleanup.BuildPlan("Demo");

        var contained = Assert.Single(plan.Candidates,
            c => c.Kind == CleanupTargetKind.BackupRef && c.Name == "refs/backups/contained");
        Assert.True(contained.Eligible);

        var orphan = Assert.Single(plan.Candidates,
            c => c.Kind == CleanupTargetKind.BackupRef && c.Name == "refs/backups/orphan");
        Assert.False(orphan.Eligible);
    }

    [Fact]
    public void BuildPlan_StaleWorktreeRegistration_IsEligible()
    {
        var (repo, cleanup) = SetupWithBranches();
        var wtPath = Path.Combine(_tempDir, "stale-wt");
        RunGit(repo, "worktree", "add", "--detach", wtPath, "develop");
        // Remove the directory out-of-band so the registration is orphaned.
        DeleteDirectory(wtPath);

        var plan = cleanup.BuildPlan("Demo");

        var stale = Assert.Single(plan.Candidates, c => c.Kind == CleanupTargetKind.StaleWorktree);
        Assert.True(stale.Eligible);
        Assert.Equal(CleanupMergeStatus.NotApplicable, stale.MergeStatus);
    }

    [Fact]
    public void BuildPlan_BranchCheckedOutInWorktree_IsKept()
    {
        var (repo, cleanup) = SetupWithBranches();
        // Attach the merged branch into a live worktree so it is checked out.
        var wtPath = Path.Combine(_tempDir, "live-wt");
        RunGit(repo, "worktree", "add", wtPath, "task/merged");

        var plan = cleanup.BuildPlan("Demo");

        var merged = Assert.Single(plan.Candidates,
            c => c.Kind == CleanupTargetKind.LocalBranch && c.Name == "task/merged");
        Assert.False(merged.Eligible);
        Assert.Contains("worktree", merged.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Execute_DeletesMerged_KeepsUnmerged()
    {
        var (repo, cleanup) = SetupWithBranches();

        var result = cleanup.Execute("Demo", new GitCleanupRequest(new[]
        {
            new CleanupExecutionItem("LocalBranch", "task/merged", null),
            new CleanupExecutionItem("LocalBranch", "task/unmerged", null),
        }));

        Assert.True(result.IsRepo);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.KeptCount);

        // task/merged is gone; task/unmerged survives untouched (AGT-1945).
        Assert.NotEqual(0, RunGitCode(repo, "rev-parse", "--verify", "--quiet", "refs/heads/task/merged"));
        Assert.Equal(0, RunGitCode(repo, "rev-parse", "--verify", "--quiet", "refs/heads/task/unmerged"));
    }

    [Fact]
    public void Execute_UnmergedRequestedExplicitly_IsRefused()
    {
        var (repo, cleanup) = SetupWithBranches();

        var result = cleanup.Execute("Demo", new GitCleanupRequest(new[]
        {
            new CleanupExecutionItem("LocalBranch", "task/unmerged", null),
        }));

        Assert.Equal(0, result.DeletedCount);
        var action = Assert.Single(result.Actions);
        Assert.False(action.Deleted);
        // The branch still exists.
        Assert.Equal(0, RunGitCode(repo, "rev-parse", "--verify", "--quiet", "refs/heads/task/unmerged"));
    }

    [Fact]
    public void Execute_DeletesContainedBackupRef_KeepsOrphan()
    {
        var (repo, cleanup) = SetupWithBranches();
        var developTip = RunGitOut(repo, "rev-parse", "develop").Trim();
        var unmergedTip = RunGitOut(repo, "rev-parse", "task/unmerged").Trim();
        RunGit(repo, "update-ref", "refs/backups/contained", developTip);
        RunGit(repo, "update-ref", "refs/backups/orphan", unmergedTip);

        var result = cleanup.Execute("Demo", new GitCleanupRequest(new[]
        {
            new CleanupExecutionItem("BackupRef", "refs/backups/contained", null),
            new CleanupExecutionItem("BackupRef", "refs/backups/orphan", null),
        }));

        Assert.Equal(1, result.DeletedCount);
        Assert.NotEqual(0, RunGitCode(repo, "rev-parse", "--verify", "--quiet", "refs/backups/contained"));
        Assert.Equal(0, RunGitCode(repo, "rev-parse", "--verify", "--quiet", "refs/backups/orphan"));
    }

    [Fact]
    public void Execute_PrunesStaleWorktree()
    {
        var (repo, cleanup) = SetupWithBranches();
        var wtPath = Path.Combine(_tempDir, "stale-wt2");
        RunGit(repo, "worktree", "add", "--detach", wtPath, "develop");
        DeleteDirectory(wtPath);

        var result = cleanup.Execute("Demo", new GitCleanupRequest(new[]
        {
            new CleanupExecutionItem("StaleWorktree", wtPath, null),
        }));

        Assert.Equal(1, result.DeletedCount);
        // The stale registration is gone from `git worktree list`.
        Assert.DoesNotContain("stale-wt2", RunGitOut(repo, "worktree", "list", "--porcelain"));
    }

    [Fact]
    public void BuildPlan_UnknownProject_ReturnsNotRepoWithError()
    {
        var (_, cleanup) = SetupWithBranches();

        var plan = cleanup.BuildPlan("No Such Project");

        Assert.False(plan.IsRepo);
        Assert.NotNull(plan.Error);
        Assert.Empty(plan.Candidates);
    }

    // ----- fixture -----

    /// <summary>
    /// Seeds a repo with develop as the integration branch, a merged task branch
    /// (folded back into develop, so it is an ancestor) and an unmerged task
    /// branch (commit that never reached develop).
    /// </summary>
    private (string repo, GitCleanupService cleanup) SetupWithBranches()
    {
        var repo = Path.Combine(_tempDir, "repo");
        var watchPath = Path.Combine(repo, ".orchestrator", "jobs");
        Directory.CreateDirectory(watchPath);

        RunGit(_tempDir, "init", "-q", "-b", "main", "repo");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");
        RunGit(repo, "config", "commit.gpgsign", "false");
        WriteAndCommit(repo, "README.md", "seed", "seed");

        RunGit(repo, "checkout", "-q", "-b", "develop");

        // Merged task branch: cut from develop, one commit, merged back with a
        // real merge commit so its tip is contained in develop.
        RunGit(repo, "checkout", "-q", "-b", "task/merged");
        WriteAndCommit(repo, "merged.txt", "merged work", "task: merged work");
        RunGit(repo, "checkout", "-q", "develop");
        RunGit(repo, "merge", "--no-ff", "--no-edit", "task/merged");

        // Unmerged task branch: cut from develop, one commit, never merged.
        RunGit(repo, "checkout", "-q", "-b", "task/unmerged");
        WriteAndCommit(repo, "unmerged.txt", "wip", "task: unmerged wip");
        RunGit(repo, "checkout", "-q", "develop");

        var cleanup = BuildCleanupService(repo, watchPath);
        return (repo, cleanup);
    }

    private static GitCleanupService BuildCleanupService(string repoRoot, string watchPath)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Demo",
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["WatchPaths:0:Path"] = watchPath,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        return new GitCleanupService(git, settings, NullLogger<GitCleanupService>.Instance);
    }

    private static void WriteAndCommit(string repo, string relativePath, string content, string message)
    {
        var full = Path.Combine(repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-q", "-m", message);
    }

    private static void DeleteDirectory(string path)
    {
        foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var (_, err, code) = Run(cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {err}");
    }

    private static string RunGitOut(string cwd, params string[] args) => Run(cwd, args).Out;

    private static int RunGitCode(string cwd, params string[] args) => Run(cwd, args).Code;

    private static (string Out, string Err, int Code) Run(string cwd, string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }
}
