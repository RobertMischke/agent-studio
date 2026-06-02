using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0052 slice 2: the worktree pre-step + merge / cleanup post-steps
/// (<see cref="WorktreeTaskLifecycle"/>) driven end to end against a throwaway
/// temp repo. Covers the direct-merge happy path, the rebase-replay path when
/// the integration branch advances under a running task, the conflict path, the
/// pull-request (no auto-merge) path, and teardown.
/// </summary>
public sealed class WorktreeTaskLifecycleTests : IDisposable
{
    private readonly string _tempDir;

    public WorktreeTaskLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "worktree-lifecycle-" + Guid.NewGuid().ToString("N"));
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
    public void Prepare_CreatesWorktreeOnTaskBranchOffIntegrationBranch()
    {
        var (repo, life) = SeedWithDevelop("prepare");

        var prep = life.Prepare(repo, "ATP-101", "develop", WorktreeRoot());

        Assert.True(prep.Success, prep.Error);
        Assert.Equal("task/ATP-101", prep.Branch);
        Assert.True(Directory.Exists(prep.WorktreePath));
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/ATP-101").Code);
        // Branch was cut from develop, not main: its merge-base is develop's tip.
        var developTip = RunGit(repo, "rev-parse develop").Out.Trim();
        Assert.Equal(0, RunGit(prep.WorktreePath!, $"merge-base --is-ancestor {developTip} HEAD").Code);
    }

    [Fact]
    public void DirectMerge_FoldsTaskBranchIntoDevelop_ThenTeardownRemovesEverything()
    {
        var (repo, life) = SeedWithDevelop("direct");
        var prep = life.Prepare(repo, "task-7", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "feature.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");
        var taskTip = RunGit(prep.WorktreePath!, "rev-parse HEAD").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge);

        Assert.Equal(IntegrationOutcome.Merged, result.Outcome);
        Assert.Equal(taskTip, RunGit(repo, "rev-parse develop").Out.Trim());
        Assert.Equal(taskTip, result.IntegratedSha);

        var teardown = life.Teardown(repo, prep.WorktreePath!, prep.Branch, deleteBranch: true, force: true);
        Assert.True(teardown.Success, teardown.Error);
        Assert.False(Directory.Exists(prep.WorktreePath));
        Assert.NotEqual(0, RunGit(repo, "rev-parse --verify task/task-7").Code);
    }

    [Fact]
    public void DirectMerge_RebasesOntoAdvancedDevelop_KeepsLinearHistory()
    {
        var (repo, life) = SeedWithDevelop("advance");
        var prep = life.Prepare(repo, "task-8", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        // Task does its work in the worktree.
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "task.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");

        // Meanwhile develop advances in the main checkout (a sibling task merged).
        File.WriteAllText(Path.Combine(repo, "other.txt"), "other work");
        Commit(repo, "feat: sibling work on develop");
        var advancedTip = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge);

        Assert.Equal(IntegrationOutcome.Merged, result.Outcome);
        // develop now contains both the sibling work and the rebased task work,
        // with the sibling commit as an ancestor (linear history, no merge commit).
        Assert.Equal(0, RunGit(repo, $"merge-base --is-ancestor {advancedTip} develop").Code);
        Assert.True(File.Exists(Path.Combine(repo, "task.txt")));
        Assert.True(File.Exists(Path.Combine(repo, "other.txt")));
    }

    [Fact]
    public void DirectMerge_Conflict_LeavesDevelopUntouched_AndKeepsBranch()
    {
        var (repo, life) = SeedWithDevelop("conflict", seedShared: true);
        var prep = life.Prepare(repo, "task-9", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);

        File.WriteAllText(Path.Combine(prep.WorktreePath!, "shared.txt"), "task version");
        Commit(prep.WorktreePath!, "feat: task edits shared");

        // develop edits the same file differently -> rebase must conflict.
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "develop version");
        Commit(repo, "chore: develop edits shared");
        var developTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.DirectMerge);

        Assert.Equal(IntegrationOutcome.Conflict, result.Outcome);
        // develop is exactly where it was; nothing was merged.
        Assert.Equal(developTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
        // The branch survives so a conflict-resolution agent / PR can pick it up.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/task-9").Code);
        // The worktree was left clean (rebase aborted): no rebase in progress.
        Assert.NotEqual(0, RunGit(prep.WorktreePath!, "rev-parse --verify REBASE_HEAD").Code);
    }

    [Fact]
    public void PullRequestStrategy_DoesNotAutoMerge()
    {
        var (repo, life) = SeedWithDevelop("pr");
        var prep = life.Prepare(repo, "task-10", "develop", WorktreeRoot());
        Assert.True(prep.Success, prep.Error);
        File.WriteAllText(Path.Combine(prep.WorktreePath!, "feature.txt"), "task work");
        Commit(prep.WorktreePath!, "feat: task work");
        var developTipBefore = RunGit(repo, "rev-parse develop").Out.Trim();

        var result = life.Integrate(repo, prep.WorktreePath!, prep.Branch!, "develop", IntegrationStrategies.PullRequest);

        Assert.Equal(IntegrationOutcome.PushedForReview, result.Outcome);
        Assert.Equal(developTipBefore, RunGit(repo, "rev-parse develop").Out.Trim());
    }

    [Fact]
    public void Prepare_EmptyTaskId_FailsWithoutTouchingGit()
    {
        var (repo, life) = SeedWithDevelop("empty");
        var prep = life.Prepare(repo, "   ", "develop", WorktreeRoot());
        Assert.False(prep.Success);
    }

    // --- harness ------------------------------------------------------------

    private string WorktreeRoot()
    {
        var root = Path.Combine(_tempDir, "wts-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        return root;
    }

    private (string Repo, WorktreeTaskLifecycle Life) SeedWithDevelop(string name, bool seedShared = false)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        if (seedShared) File.WriteAllText(Path.Combine(repo, "shared.txt"), "base");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        // The integration branch is develop; main stays the released line.
        RunGit(repo, "checkout -q -b develop");

        var git = BuildGitService(repo);
        var life = new WorktreeTaskLifecycle(git, NullLogger<WorktreeTaskLifecycle>.Instance);
        return (repo, life);
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private static GitService BuildGitService(string repo)
    {
        var dict = new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Fixture",
            ["WatchPaths:0:RootPath"] = repo,
            ["WatchPaths:0:RepositoryPath"] = repo,
            ["WatchPaths:0:Path"] = Path.Combine(repo, ".orchestrator", "jobs"),
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new GitService(NullLogger<GitService>.Instance, scanner, config);
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args)
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
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit(15_000);
        return (so, se, p.ExitCode);
    }
}
