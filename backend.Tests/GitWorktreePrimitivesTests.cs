using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0052 slice 1 (config + worktree plumbing in GitService): real-worktree
/// coverage for the new low-level git primitives that the parallel-task model
/// builds on - worktree add/remove, rebase-onto, fast-forward merge, and the
/// branch-parameterized push. Every test drives git against a throwaway temp
/// repo so the behaviour is exercised end to end, not mocked.
/// </summary>
public sealed class GitWorktreePrimitivesTests : IDisposable
{
    private readonly string _tempDir;

    public GitWorktreePrimitivesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "git-worktree-primitives-" + Guid.NewGuid().ToString("N"));
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
    public void WorktreeAdd_CreatesIsolatedWorktreeOnNewBranch()
    {
        var repo = SeedRepo("add");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-add");

        var result = git.WorktreeAdd(repo, wtPath, "task/1", "main");

        Assert.True(result.Success, result.Error);
        Assert.Equal(wtPath, result.Path);
        Assert.True(Directory.Exists(wtPath));
        // The new branch exists and the seeded file is checked out in the worktree.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/1").Code);
        Assert.True(File.Exists(Path.Combine(wtPath, "README.md")));
    }

    [Fact]
    public void WorktreeAdd_PathWithSpaces_IsHandledByArgumentList()
    {
        var repo = SeedRepo("add-spaces");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "work tree with spaces");

        var result = git.WorktreeAdd(repo, wtPath, "task/2", "main");

        Assert.True(result.Success, result.Error);
        Assert.True(Directory.Exists(wtPath));
    }

    [Theory]
    [InlineData("-evil")]
    [InlineData("bad..name")]
    [InlineData("trailing/")]
    [InlineData("with space")]
    public void WorktreeAdd_InvalidBranchName_FailsWithoutTouchingGit(string branch)
    {
        var repo = SeedRepo("add-invalid-" + Math.Abs(branch.GetHashCode()));
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-invalid");

        var result = git.WorktreeAdd(repo, wtPath, branch, "main");

        Assert.False(result.Success);
        Assert.False(Directory.Exists(wtPath));
    }

    [Fact]
    public void WorktreeRemove_TearsDownTheWorktree()
    {
        var repo = SeedRepo("remove");
        var git = BuildGitService(("Fixture", repo));
        var wtPath = Path.Combine(_tempDir, "wt-remove");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/3", "main").Success);

        var result = git.WorktreeRemove(repo, wtPath);

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(wtPath));
        // Branch ref survives removal so the work can be integrated separately.
        Assert.Equal(0, RunGit(repo, "rev-parse --verify task/3").Code);
    }

    [Fact]
    public void RebaseOnto_ReplaysTaskBranchOnLatestIntegrationTip()
    {
        var repo = SeedRepo("rebase");
        var git = BuildGitService(("Fixture", repo));

        // main advances after the task branch was cut.
        var wtPath = Path.Combine(_tempDir, "wt-rebase");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/4", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "task.txt"), "task work");
        Commit(wtPath, "feat: task work");

        File.WriteAllText(Path.Combine(repo, "main.txt"), "main moved");
        Commit(repo, "chore: advance main");
        var mainTip = RunGit(repo, "rev-parse main").Out.Trim();

        var result = git.RebaseOnto(wtPath, "main");

        Assert.True(result.Success, result.Error);
        // The task branch now descends from the new main tip (linear history).
        Assert.Equal(0, RunGit(wtPath, $"merge-base --is-ancestor {mainTip} HEAD").Code);
    }

    [Fact]
    public void RebaseOnto_Conflict_AbortsAndLeavesWorktreeClean()
    {
        var repo = SeedRepo("rebase-conflict");
        var git = BuildGitService(("Fixture", repo));

        var wtPath = Path.Combine(_tempDir, "wt-conflict");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/5", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "shared.txt"), "task version");
        Commit(wtPath, "feat: task edits shared");

        // main edits the same file differently -> rebase must conflict.
        File.WriteAllText(Path.Combine(repo, "shared.txt"), "main version");
        Commit(repo, "chore: main edits shared");

        var result = git.RebaseOnto(wtPath, "main");

        Assert.False(result.Success);
        // The rebase was aborted: no rebase is in progress in the worktree.
        Assert.NotEqual(0, RunGit(wtPath, "rev-parse --verify REBASE_HEAD").Code);
    }

    [Fact]
    public void MergeFastForward_FoldsRebasedBranchIntoIntegration()
    {
        var repo = SeedRepo("ff");
        var git = BuildGitService(("Fixture", repo));

        // Task branch is a pure descendant of main, so main can fast-forward.
        var wtPath = Path.Combine(_tempDir, "wt-ff");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/6", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "task.txt"), "task work");
        Commit(wtPath, "feat: task work");
        var taskTip = RunGit(wtPath, "rev-parse HEAD").Out.Trim();

        // repo has main checked out; fast-forward it to the task branch tip.
        var result = git.MergeFastForward(repo, "task/6");

        Assert.True(result.Success, result.Error);
        Assert.Equal(taskTip, RunGit(repo, "rev-parse main").Out.Trim());
    }

    [Fact]
    public void MergeFastForward_NonFastForward_FailsWithoutMergeCommit()
    {
        var repo = SeedRepo("ff-no");
        var git = BuildGitService(("Fixture", repo));

        var wtPath = Path.Combine(_tempDir, "wt-ff-no");
        Assert.True(git.WorktreeAdd(repo, wtPath, "task/7", "main").Success);
        File.WriteAllText(Path.Combine(wtPath, "task.txt"), "task work");
        Commit(wtPath, "feat: task work");

        // main diverges, so the task branch is no longer a fast-forward.
        File.WriteAllText(Path.Combine(repo, "main.txt"), "main diverged");
        Commit(repo, "chore: diverge main");
        var mainTipBefore = RunGit(repo, "rev-parse main").Out.Trim();

        var result = git.MergeFastForward(repo, "task/7");

        Assert.False(result.Success);
        // No merge commit was created; main is exactly where it was.
        Assert.Equal(mainTipBefore, RunGit(repo, "rev-parse main").Out.Trim());
    }

    [Fact]
    public async Task PushShaAsync_InvalidTargetBranch_IsRejectedBeforeAnyGitCall()
    {
        var repo = SeedRepo("push-guard");
        var git = BuildGitService(("Fixture", repo));
        var sha = RunGit(repo, "rev-parse HEAD").Out.Trim();

        var result = await git.PushShaAsync(sha, repo, default, targetBranch: "-not-a-branch");

        Assert.False(result.Success);
        Assert.Equal("invalid-branch", result.Status);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("develop")]
    public async Task PushShaAsync_PushesToRequestedBranch(string targetBranch)
    {
        var repo = SeedRepo("push-" + targetBranch);
        var bare = Path.Combine(_tempDir, "remote-" + targetBranch + ".git");
        Assert.Equal(0, RunGit(_tempDir, $"init -q --bare \"{bare}\"").Code);
        Assert.Equal(0, RunGit(repo, $"remote add origin \"{bare}\"").Code);
        var sha = RunGit(repo, "rev-parse HEAD").Out.Trim();

        var git = BuildGitService(("Fixture", repo));
        var result = await git.PushShaAsync(sha, repo, default, targetBranch: targetBranch);

        Assert.True(result.Success, result.Error);
        // The remote is bare; safe.bareRepository=explicit means we must name
        // the git dir explicitly rather than cd into it.
        Assert.Equal(sha, RunGit(_tempDir, $"--git-dir=\"{bare}\" rev-parse refs/heads/{targetBranch}").Out.Trim());
    }

    private string SeedRepo(string name)
    {
        var repo = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(repo);
        RunGit(repo, "init -q -b main");
        RunGit(repo, "config user.email test@example.com");
        RunGit(repo, "config user.name test");
        File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
        RunGit(repo, "add -A");
        RunGit(repo, "commit -q -m seed");
        return repo;
    }

    private static void Commit(string cwd, string message)
    {
        RunGit(cwd, "add -A");
        RunGit(cwd, $"commit -q -m \"{message}\"");
    }

    private GitService BuildGitService(params (string Name, string RepoPath)[] entries)
    {
        var dict = new Dictionary<string, string?>();
        for (var i = 0; i < entries.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = entries[i].Name;
            dict[$"WatchPaths:{i}:RootPath"] = entries[i].RepoPath;
            dict[$"WatchPaths:{i}:RepositoryPath"] = entries[i].RepoPath;
            dict[$"WatchPaths:{i}:Path"] = Path.Combine(entries[i].RepoPath, ".orchestrator", "jobs");
        }
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
