using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

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

    // --- WithMerge: pure write-once merge anchor (ASS-1752) -----------------

    [Fact]
    public void WithMerge_NoExisting_SetsMergeAndBranch()
    {
        var merge = new TaskProvenanceMerge { MergeCommit = "ddddddd", AtUtc = DateTime.UtcNow };

        var result = TaskProvenanceService.WithMerge(existing: null, "task/1", merge);

        Assert.Equal("task/1", result.Branch);
        Assert.NotNull(result.Merge);
        Assert.Equal("ddddddd", result.Merge!.MergeCommit);
        Assert.Empty(result.Transitions);
    }

    [Fact]
    public void WithMerge_CarriesBranchBaseAndTransitionsThrough()
    {
        var existing = new TaskProvenance
        {
            Branch = "task/keep",
            Base = "base000",
            Transitions = [Anchor(TaskStates.Progress, branchTip: "aaaaaaa"), Anchor(TaskStates.AutoReview, branchTip: "aaaaaaa")],
            Merge = null,
        };
        var merge = new TaskProvenanceMerge { MergeCommit = "ddddddd", AtUtc = DateTime.UtcNow };

        var result = TaskProvenanceService.WithMerge(existing, "task/IGNORED", merge);

        // Branch + base + the full transition ladder survive; the passed-in branch
        // is only a fallback used when the record had none.
        Assert.Equal("task/keep", result.Branch);
        Assert.Equal("base000", result.Base);
        Assert.Equal(2, result.Transitions.Count);
        Assert.Equal("ddddddd", result.Merge!.MergeCommit);
    }

    [Fact]
    public void WithMerge_IsWriteOnce_DoesNotOverwriteExistingMerge()
    {
        var existing = new TaskProvenance
        {
            Branch = "task/1",
            Base = "base000",
            Transitions = [Anchor(TaskStates.Progress)],
            Merge = new TaskProvenanceMerge { MergeCommit = "FIRST00", AtUtc = DateTime.UtcNow },
        };
        var second = new TaskProvenanceMerge { MergeCommit = "SECOND0", AtUtc = DateTime.UtcNow };

        var result = TaskProvenanceService.WithMerge(existing, "task/1", second);

        // An already-recorded merge fact is append-only: the first SHA wins.
        Assert.Equal("FIRST00", result.Merge!.MergeCommit);
    }

    [Fact]
    public void WithMerge_DoesNotMutateExistingTransitionList()
    {
        var existing = new TaskProvenance
        {
            Branch = "task/1",
            Base = "base000",
            Transitions = [Anchor(TaskStates.Progress)],
            Merge = null,
        };

        var result = TaskProvenanceService.WithMerge(
            existing, "task/1", new TaskProvenanceMerge { MergeCommit = "ddddddd", AtUtc = DateTime.UtcNow });

        result.Transitions.Add(Anchor(TaskStates.Completed));
        Assert.Single(existing.Transitions); // the source snapshot is untouched
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

    // --- Anchor write-order: must run BEFORE worktree teardown (ASS-1752) -

    [Fact]
    public void RecordTransition_CapturesLiveBranchTip_ButLosesItOnceTornDown()
    {
        // The bug: the card showed a dead worktree because the provenance anchor
        // was read at the wrong moment. RecordTransition pins whatever
        // `GetBranchTip(task/<id>)` returns AT THE INSTANT it runs. This test
        // proves the ordering contract end to end against a real repo:
        //  - recorded while the task/<id> branch is alive -> the live tip is
        //    captured, so the card can name the worktree, and
        //  - recorded after the branch was torn down -> the tip is null, which is
        //    exactly the "landed / no worktree" read.
        // Therefore production MUST record the anchor before TeardownWorktreeForJob
        // (it does: RecordTransition runs in MoveAsync right after the move lands,
        // ahead of any integration teardown). Run this with the branch torn down
        // FIRST and the worktree fact is gone forever.
        var (repoRoot, watchPath) = SeedWorktreeRepo("order-1");
        var taskTip = RunGit(repoRoot, "rev-parse task/order-1").Out.Trim();
        Assert.False(string.IsNullOrWhiteSpace(taskTip));

        var prov = BuildProvenanceService(repoRoot, watchPath, "demo");

        // 1) Record WHILE the worktree branch is alive: the live tip is anchored.
        var live = Scan(repoRoot, watchPath, "order-1");
        Assert.NotNull(live);
        prov.RecordTransition(live!, TaskStates.AutoReview);

        var afterLive = Scan(repoRoot, watchPath, "order-1");
        var liveAnchor = afterLive!.Provenance!.Transitions[^1];
        Assert.Equal(TaskStates.AutoReview, liveAnchor.Lane);
        Assert.Equal(taskTip, liveAnchor.BranchTip);

        // 2) Tear the worktree branch down, THEN record again. The same call now
        //    sees no branch and anchors a null tip - the worktree fact is lost.
        RunGit(repoRoot, "checkout -q develop");
        RunGit(repoRoot, "branch -D task/order-1");

        prov.RecordTransition(afterLive!, TaskStates.HumanReview);

        var afterTeardown = Scan(repoRoot, watchPath, "order-1");
        var deadAnchor = afterTeardown!.Provenance!.Transitions[^1];
        Assert.Equal(TaskStates.HumanReview, deadAnchor.Lane);
        Assert.Null(deadAnchor.BranchTip);

        // The earlier live anchor is still on record (append-only), so the ladder
        // can reconstruct that the work once lived in a worktree.
        Assert.Contains(afterTeardown.Provenance!.Transitions, t => t.BranchTip == taskTip);
    }

    // --- Batch membership: rev-list set == per-commit ancestry (AGT-2007) -

    [Fact]
    public void GetReachableShaSet_MatchesPerCommitAncestry_BeforeAndAfterMerge()
    {
        // The provenance perf fix replaces one `merge-base --is-ancestor` spawn
        // per commit with a single `rev-list base..branch` set lookup. This
        // pins the equivalence the substitution relies on: for every commit,
        // set.Contains(sha) must equal IsAncestor(sha, branch) - before and
        // after the branch is folded in.
        var repo = SeedRepo("reachable-parity");
        var git = BuildGitService(("Fixture", repo));
        RunGit(repo, "checkout -q -b develop");
        var baseSha = RunGit(repo, "rev-parse develop").Out.Trim();
        RunGit(repo, "checkout -q -b task/300");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Commit(repo, "feat: a");
        var shaA = RunGit(repo, "rev-parse task/300").Out.Trim();
        File.WriteAllText(Path.Combine(repo, "b.txt"), "b");
        Commit(repo, "feat: b");
        var shaB = RunGit(repo, "rev-parse task/300").Out.Trim();

        // The range set is exactly the branch commits ahead of the fork point.
        var branchSet = git.GetReachableShaSet(repo, baseSha, "task/300");
        Assert.Contains(shaA, branchSet);
        Assert.Contains(shaB, branchSet);
        Assert.DoesNotContain(baseSha, branchSet);

        // Before merge: develop's set excludes the task commits, and the batch
        // answer agrees with per-commit merge-base --is-ancestor for each.
        var devBefore = git.GetReachableShaSet(repo, baseSha, "develop");
        foreach (var sha in new[] { shaA, shaB })
            Assert.Equal(git.IsAncestor(repo, sha, "develop"), devBefore.Contains(sha));
        Assert.DoesNotContain(shaA, devBefore);

        // After merge: the same commits are now reachable from develop; batch
        // and per-commit answers still agree.
        RunGit(repo, "checkout -q develop");
        RunGit(repo, "merge -q --no-ff --no-edit task/300");
        var devAfter = git.GetReachableShaSet(repo, baseSha, "develop");
        foreach (var sha in new[] { shaA, shaB })
            Assert.Equal(git.IsAncestor(repo, sha, "develop"), devAfter.Contains(sha));
        Assert.Contains(shaA, devAfter);
        Assert.Contains(shaB, devAfter);
    }

    [Fact]
    public void GetReachableShaSet_MissingRef_ReturnsEmpty()
    {
        var repo = SeedRepo("reachable-missing");
        var git = BuildGitService(("Fixture", repo));
        var baseSha = RunGit(repo, "rev-parse main").Out.Trim();

        // A ref that does not exist must resolve to "not contained" (empty set),
        // matching the conservative fallback of the per-commit ancestry checks.
        Assert.Empty(git.GetReachableShaSet(repo, baseSha, "no-such-branch"));
    }

    [Fact]
    public void BuildView_MultiCommitTask_BatchMembershipTracksMergeState()
    {
        // End-to-end over the canonical attributed membership path. A live task
        // branch has two commits and both are explicitly attributed to the card.
        // Branch-only WIP outside this set must never drive integration fields.
        var (repoRoot, watchPath) = SeedWorktreeRepo("multi-1");
        File.WriteAllText(Path.Combine(repoRoot, "work2.txt"), "more task work");
        Commit(repoRoot, "feat: more task work");
        var attributed = RunGit(repoRoot, "rev-list --reverse develop..task/multi-1")
            .Out
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var prov = BuildProvenanceService(repoRoot, watchPath, "demo");
        var info = Scan(repoRoot, watchPath, "multi-1");
        Assert.NotNull(info);
        // Record a transition so provenance.Base (the fork point) is captured -
        // that base is what the batch membership walks from.
        prov.RecordTransition(info!, TaskStates.AutoReview);
        info = Scan(repoRoot, watchPath, "multi-1");
        info = info! with
        {
            Commits = attributed.Select(sha => new TaskCommitInfo
            {
                Sha = sha,
                ShortSha = sha[..7],
                Message = "attributed task work",
                FilesChanged = 1,
                Files = ["work.txt"],
            }).ToList(),
        };

        var before = prov.BuildView(info);
        Assert.Equal(LandedStates.OnBranchOnly, before.LandedState);
        Assert.Equal(2, before.Commits.Count);
        Assert.All(before.Commits, c => Assert.True(c.OnTaskBranch));
        Assert.All(before.Commits, c => Assert.False(c.AlsoOnIntegration));
        Assert.All(before.Commits, c => Assert.False(c.AlsoOnRelease));

        // Fold the branch into develop; the batch membership must now report
        // both commits as merged-to-develop while main stays clean.
        RunGit(repoRoot, "checkout -q develop");
        RunGit(repoRoot, "merge -q --no-ff --no-edit task/multi-1");

        var after = prov.BuildView(info);
        Assert.Equal(LandedStates.MergedToDevelop, after.LandedState);
        Assert.Equal(2, after.Commits.Count);
        Assert.All(after.Commits, c => Assert.True(c.AlsoOnIntegration));
        Assert.All(after.Commits, c => Assert.False(c.AlsoOnRelease));
    }

    // --- Helpers (shared shape with GitWorktreePrimitivesTests) -----------

    /// <summary>
    /// A repo configured as a single watch path with a job-folder layout, plus a
    /// live <c>task/&lt;id&gt;</c> branch cut off develop (simulating an isolated
    /// worktree run). Returns (repoRoot, watchPath); the job sits in 3-progress.
    /// </summary>
    private (string RepoRoot, string WatchPath) SeedWorktreeRepo(string jobId)
    {
        var root = Path.Combine(_tempDir, "wt-" + jobId);
        var repoRoot = Path.Combine(root, "repo");
        var watchPath = Path.Combine(root, "jobs");
        Directory.CreateDirectory(repoRoot);
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watchPath, state));

        RunGit(repoRoot, "init -q -b main");
        RunGit(repoRoot, "config user.email test@example.com");
        RunGit(repoRoot, "config user.name test");
        File.WriteAllText(Path.Combine(repoRoot, "README.md"), "seed");
        RunGit(repoRoot, "add -A");
        RunGit(repoRoot, "commit -q -m seed");
        RunGit(repoRoot, "checkout -q -b develop");
        // The task branch carries its own commit, so its tip differs from develop.
        RunGit(repoRoot, "checkout -q -b task/" + jobId);
        File.WriteAllText(Path.Combine(repoRoot, "work.txt"), "task work");
        Commit(repoRoot, "feat: task work");

        var dir = Path.Combine(watchPath, TaskStates.Progress, jobId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{jobId}\",\"title\":\"{jobId}\",\"state\":\"{TaskStates.Progress}\",\"order\":1,\"agent\":\"copilot\"}}");

        return (repoRoot, watchPath);
    }

    private static IConfiguration WatchConfig(string repoRoot, string watchPath, string projectName)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = projectName,
            ["WatchPaths:0:Path"] = watchPath,
            ["WatchPaths:0:RootPath"] = repoRoot,
            ["WatchPaths:0:RepositoryPath"] = repoRoot,
            ["TaskRepository"] = watchPath,
        }).Build();

    private static TaskProvenanceService BuildProvenanceService(string repoRoot, string watchPath, string projectName)
    {
        var config = WatchConfig(repoRoot, watchPath, projectName);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new TaskProvenanceService(git, settings, mutations, NullLogger<TaskProvenanceService>.Instance);
    }

    /// <summary>Fresh scanner each call so the read sees the latest on-disk stamp.</summary>
    private static TaskInfo? Scan(string repoRoot, string watchPath, string jobId)
    {
        var config = WatchConfig(repoRoot, watchPath, "demo");
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return scanner.FindJob(jobId, watchPath);
    }

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
