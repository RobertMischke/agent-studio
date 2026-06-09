using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ASS-1724 (commit-provenance & landed-state). Two halves, matching the
/// service split:
///  - the pure append semantics of <see cref="TaskProvenanceService.AppendTransition"/>
///    (base write-once, ordered accumulation, merge carried through), which need
///    no git at all, and
///  - the graph-derived landed-state + merge-set, exercised end to end against a
///    throwaway temp repo so the <c>merge-base --is-ancestor</c> ancestry is real,
///    not mocked - the same SeedRepo/RunGit harness as GitWorktreePrimitivesTests.
/// </summary>
public sealed class TaskProvenanceServiceTests : IDisposable
{
    private readonly string _tempDir;

    public TaskProvenanceServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "task-provenance-" + Guid.NewGuid().ToString("N"));
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

    // --- AppendTransition: pure record semantics ---------------------------

    [Fact]
    public void AppendTransition_FirstAnchor_SetsBranchBaseAndTransition()
    {
        var t = Anchor(TaskStates.Progress, branchTip: "aaaaaaa", workBranchHead: "bbbbbbb");

        var result = TaskProvenanceService.AppendTransition(existing: null, "task/1", "base000", t);

        Assert.Equal("task/1", result.Branch);
        Assert.Equal("base000", result.Base);
        Assert.Single(result.Transitions);
        Assert.Equal(TaskStates.Progress, result.Transitions[0].Lane);
        Assert.Equal("aaaaaaa", result.Transitions[0].BranchTip);
        Assert.Null(result.Merge);
    }

    [Fact]
    public void AppendTransition_BaseIsWriteOnce()
    {
        var first = TaskProvenanceService.AppendTransition(
            existing: null, "task/1", "base000", Anchor(TaskStates.Progress));

        // A later transition reports a different fork point (e.g. branch was
        // re-cut); the originally captured base must NOT be overwritten.
        var second = TaskProvenanceService.AppendTransition(
            first, "task/1", "DIFFERENT", Anchor(TaskStates.HumanReview));

        Assert.Equal("base000", second.Base);
        Assert.Equal(2, second.Transitions.Count);
    }

    [Fact]
    public void AppendTransition_AccumulatesOldestToNewest()
    {
        var p = TaskProvenanceService.AppendTransition(null, "task/1", "base000", Anchor(TaskStates.Progress));
        p = TaskProvenanceService.AppendTransition(p, "task/1", null, Anchor(TaskStates.HumanReview));
        p = TaskProvenanceService.AppendTransition(p, "task/1", null, Anchor(TaskStates.Completed));

        Assert.Equal(
            new[] { TaskStates.Progress, TaskStates.HumanReview, TaskStates.Completed },
            p.Transitions.Select(t => t.Lane).ToArray());
    }

    [Fact]
    public void AppendTransition_DoesNotMutateExistingList()
    {
        var first = TaskProvenanceService.AppendTransition(null, "task/1", "base000", Anchor(TaskStates.Progress));

        var second = TaskProvenanceService.AppendTransition(first, "task/1", null, Anchor(TaskStates.HumanReview));

        // The append is non-destructive: the earlier snapshot still has one entry.
        Assert.Single(first.Transitions);
        Assert.Equal(2, second.Transitions.Count);
    }

    [Fact]
    public void AppendTransition_CarriesMergeBlockThrough()
    {
        var withMerge = new TaskProvenance
        {
            Branch = "task/1",
            Base = "base000",
            Transitions = [Anchor(TaskStates.Progress)],
            Merge = new TaskProvenanceMerge { MergeCommit = "merge00", AtUtc = DateTime.UtcNow },
        };

        var result = TaskProvenanceService.AppendTransition(withMerge, "task/1", null, Anchor(TaskStates.HumanReview));

        Assert.NotNull(result.Merge);
        Assert.Equal("merge00", result.Merge!.MergeCommit);
    }

    // --- DeriveLandedState: graph ancestry, real repo ---------------------

    [Fact]
    public void DeriveLandedState_NullAnchor_IsOnBranchOnly()
    {
        var repo = SeedRepo("derive-null");
        var git = BuildGitService(("Fixture", repo));

        var state = TaskProvenanceService.DeriveLandedState(git, repo, anchorSha: null, "develop", "main");

        Assert.Equal(LandedStates.OnBranchOnly, state);
    }

    [Fact]
    public void DeriveLandedState_BranchNotMerged_IsOnBranchOnly()
    {
        var repo = SeedRepo("derive-onbranch");
        var git = BuildGitService(("Fixture", repo));
        RunGit(repo, "checkout -q -b develop");
        // task/100 cut off develop with its own commit; develop does NOT contain it.
        RunGit(repo, "checkout -q -b task/100");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        var taskTip = RunGit(repo, "rev-parse task/100").Out.Trim();
        RunGit(repo, "checkout -q develop");

        var state = TaskProvenanceService.DeriveLandedState(git, repo, taskTip, "develop", "main");

        Assert.Equal(LandedStates.OnBranchOnly, state);
    }

    [Fact]
    public void DeriveLandedState_AfterMergeToDevelop_IsMergedToDevelop()
    {
        var repo = SeedRepo("derive-merged");
        var git = BuildGitService(("Fixture", repo));
        RunGit(repo, "checkout -q -b develop");
        RunGit(repo, "checkout -q -b task/101");
        File.WriteAllText(Path.Combine(repo, "task.txt"), "task work");
        Commit(repo, "feat: task work");
        var taskTip = RunGit(repo, "rev-parse task/101").Out.Trim();
        // Fold the branch into develop; now develop contains taskTip but main does not.
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff --no-edit task/101");

        var state = TaskProvenanceService.DeriveLandedState(git, repo, taskTip, "develop", "main");

        Assert.Equal(LandedStates.MergedToDevelop, state);
    }

    [Fact]
    public void DeriveLandedState_OnReleaseBranch_IsReleasedToMain()
    {
        var repo = SeedRepo("derive-released");
        var git = BuildGitService(("Fixture", repo));
        // develop branches off main; the seed commit is reachable from main, so it
        // is "released". Release takes precedence over the develop check.
        RunGit(repo, "checkout -q -b develop");
        var seedSha = RunGit(repo, "rev-parse main").Out.Trim();

        var state = TaskProvenanceService.DeriveLandedState(git, repo, seedSha, "develop", "main");

        Assert.Equal(LandedStates.ReleasedToMain, state);
    }

    // --- Merge-set: task/<id> ahead of base (graph) ----------------------

    [Fact]
    public void MergeSet_IsExactlyTheCommitsBranchIsAheadOfBase()
    {
        var repo = SeedRepo("mergeset");
        var git = BuildGitService(("Fixture", repo));
        var baseSha = RunGit(repo, "rev-parse main").Out.Trim();

        // task/200 adds two commits past the fork point; nothing else should appear.
        RunGit(repo, "checkout -q -b task/200");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Commit(repo, "feat: a");
        File.WriteAllText(Path.Combine(repo, "b.txt"), "b");
        Commit(repo, "feat: b");

        var range = git.GetCommitsInRangeAtRoot(repo, baseSha, "task/200");

        Assert.Equal(2, range.Count);
        // Newest first; the seed commit (== base) is excluded from the half-open range.
        Assert.Equal(new[] { "feat: b", "feat: a" }, range.Select(c => c.Subject).ToArray());
        Assert.DoesNotContain(range, c => c.Sha == baseSha);
    }

    [Fact]
    public void MergeSet_MembershipFlipsAfterMerge()
    {
        var repo = SeedRepo("mergeset-membership");
        var git = BuildGitService(("Fixture", repo));
        RunGit(repo, "checkout -q -b develop");
        var baseSha = RunGit(repo, "rev-parse develop").Out.Trim();
        RunGit(repo, "checkout -q -b task/201");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Commit(repo, "feat: a");
        var commitSha = RunGit(repo, "rev-parse task/201").Out.Trim();

        // Before merge: the commit is task-only (not reachable from develop).
        Assert.False(git.IsAncestor(repo, commitSha, "develop"));

        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff --no-edit task/201");

        // After merge: the same commit is now reachable from develop.
        Assert.True(git.IsAncestor(repo, commitSha, "develop"));
        Assert.False(git.IsAncestor(repo, commitSha, "main"));
    }

    // --- Helpers (shared shape with GitWorktreePrimitivesTests) -----------

    private static TaskProvenanceTransition Anchor(
        string lane, string? branchTip = null, string? workBranchHead = null) => new()
    {
        Lane = lane,
        AtUtc = DateTime.UtcNow,
        BranchTip = branchTip,
        WorkBranchHead = workBranchHead,
    };

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
