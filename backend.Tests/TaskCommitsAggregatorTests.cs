using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pure-function tests for <see cref="TaskCommitsAggregator"/>. The
/// aggregator is the load-bearing piece behind the protocol-pane
/// "Commits and change set" panel and the kanban "+N commits" hint;
/// the tests pin the dedup-by-SHA, ordering, deletion-only, and
/// auto-commit-merging rules without standing up a real git repo.
/// </summary>
public class TaskCommitsAggregatorTests
{
    [Fact]
    public void Aggregate_NoRunsAndNoAutoCommit_ReturnsEmpty()
    {
        var info = new TaskInfo();
        var result = TaskCommitsAggregator.Aggregate(info, [], (_, _) => []);
        Assert.Equal(0, result.Count);
        Assert.Empty(result.Commits);
        Assert.Equal(0, result.TotalAdded);
        Assert.Equal(0, result.TotalRemoved);
    }

    [Fact]
    public void Aggregate_OneRunOneCommit_ReturnsThatCommit()
    {
        var info = new TaskInfo();
        var run = new RunRecord { Index = 1, HeadShaBefore = "aaa", HeadShaAfter = "bbb" };
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => new List<GitCommitInfo>
        {
            new("bbb", "bbb", DateTime.UtcNow, "Alice", "feat: thing", 2, 10, 1)
        });
        Assert.Equal(1, result.Count);
        Assert.Equal("bbb", result.Commits[0].Sha);
        Assert.Equal(1, result.Commits[0].RunIndex);
        Assert.Equal(10, result.TotalAdded);
        Assert.Equal(1, result.TotalRemoved);
        Assert.Equal(2, result.TotalFilesChanged);
    }

    [Fact]
    public void Aggregate_MultipleRunsMultipleCommits_OrderedNewestFirst()
    {
        var info = new TaskInfo();
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        var run1 = new RunRecord { Index = 1, HeadShaBefore = "h0", HeadShaAfter = "h1" };
        var run2 = new RunRecord { Index = 2, HeadShaBefore = "h1", HeadShaAfter = "h2" };

        var result = TaskCommitsAggregator.Aggregate(info, [run1, run2], (before, after) => after switch
        {
            "h1" => [new GitCommitInfo("c1", "c1", t0.AddMinutes(1), "A", "first", 1, 1, 0)],
            "h2" => [new GitCommitInfo("c2", "c2", t0.AddMinutes(20), "A", "second", 1, 2, 0)],
            _ => []
        });

        Assert.Equal(2, result.Count);
        Assert.Equal("second", result.Commits[0].Subject); // newest first
        Assert.Equal("first", result.Commits[1].Subject);
        Assert.Equal(2, result.Commits[0].RunIndex);
        Assert.Equal(1, result.Commits[1].RunIndex);
    }

    [Fact]
    public void Aggregate_DedupsCommitAcrossOverlappingRuns()
    {
        // Two runs whose SHA ranges happen to overlap (e.g. a recovery
        // run that re-claims the previous range). The same commit must
        // not double-count.
        var info = new TaskInfo();
        var run1 = new RunRecord { Index = 1, HeadShaBefore = "h0", HeadShaAfter = "h1" };
        var run2 = new RunRecord { Index = 2, HeadShaBefore = "h0", HeadShaAfter = "h1" };
        var sharedCommit = new GitCommitInfo("dup", "dup", DateTime.UtcNow, "A", "shared", 1, 5, 0);
        var result = TaskCommitsAggregator.Aggregate(info, [run1, run2], (_, _) => [sharedCommit]);

        Assert.Equal(1, result.Count);
        Assert.Equal("dup", result.Commits[0].Sha);
        Assert.Equal(1, result.Commits[0].RunIndex); // earlier run wins
    }

    [Fact]
    public void Aggregate_TrivialRangesAreSkipped()
    {
        var info = new TaskInfo();
        var trivial = new RunRecord { Index = 1, HeadShaBefore = "same", HeadShaAfter = "same" };
        var missing = new RunRecord { Index = 2, HeadShaBefore = null, HeadShaAfter = "x" };
        var fetched = false;
        var result = TaskCommitsAggregator.Aggregate(info, [trivial, missing], (_, _) =>
        {
            fetched = true;
            return [];
        });
        Assert.False(fetched);
        Assert.Equal(0, result.Count);
    }

    [Fact]
    public void Aggregate_DeletionOnlyCommitIsIncluded()
    {
        var info = new TaskInfo();
        var run = new RunRecord { Index = 1, HeadShaBefore = "h0", HeadShaAfter = "h1" };
        var deletion = new GitCommitInfo("del", "del", DateTime.UtcNow, "A", "remove old", 3, 0, 42);
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => [deletion]);

        Assert.Equal(1, result.Count);
        Assert.Equal(0, result.Commits[0].Added);
        Assert.Equal(42, result.Commits[0].Removed);
        Assert.Equal(0, result.TotalAdded);
        Assert.Equal(42, result.TotalRemoved);
    }

    [Fact]
    public void Aggregate_AutoCommitIsAddedWhenNotAlreadyCovered()
    {
        var info = new TaskInfo
        {
            Commit = new TaskCommitInfo
            {
                Sha = "auto",
                ShortSha = "auto",
                Message = "chore: snapshot",
                FilesChanged = 4,
                Files = ["a", "b", "c", "d"],
                At = DateTime.UtcNow
            }
        };
        var result = TaskCommitsAggregator.Aggregate(info, [], (_, _) => []);
        Assert.Equal(1, result.Count);
        Assert.Equal("auto", result.Commits[0].Sha);
        Assert.Null(result.Commits[0].RunIndex);
    }

    [Fact]
    public void Aggregate_AutoCommitNotDoubleCountedWhenAlreadyInRunRange()
    {
        var info = new TaskInfo
        {
            Commit = new TaskCommitInfo { Sha = "auto", ShortSha = "auto", Message = "chore: snap", At = DateTime.UtcNow }
        };
        var run = new RunRecord { Index = 1, HeadShaBefore = "h0", HeadShaAfter = "auto" };
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => new List<GitCommitInfo>
        {
            new("auto", "auto", DateTime.UtcNow, "A", "real subject", 2, 5, 1)
        });
        Assert.Equal(1, result.Count);
        // The richer run-derived metadata wins.
        Assert.Equal("real subject", result.Commits[0].Subject);
        Assert.Equal(1, result.Commits[0].RunIndex);
    }

    [Fact]
    public void Aggregate_SurfacesEveryInRangeCommit()
    {
        // The operator-override exclusion was removed: the aggregate no longer
        // subtracts any SHA, so every commit a run range reaches is surfaced.
        var info = new TaskInfo();
        var run = new RunRecord { Index = 1, HeadShaBefore = "h0", HeadShaAfter = "h1" };
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => new List<GitCommitInfo>
        {
            new("real", "real", DateTime.UtcNow, "Claude", "feat: real", 1, 3, 0),
            new("noise", "noise", DateTime.UtcNow, "boot", "chore(crash-recovery): rescue orphan changes for other", 1, 9, 0)
        });

        Assert.Equal(2, result.Count);
        Assert.Contains(result.Commits, c => c.Sha == "real");
        Assert.Contains(result.Commits, c => c.Sha == "noise");
    }

    [Fact]
    public void Aggregate_OverlaysAttributionFromPersistedChain()
    {
        var info = new TaskInfo
        {
            Commits =
            [
                new TaskCommitInfo { Sha = "bbb", ShortSha = "bbb", Message = "feat", At = DateTime.UtcNow, Attribution = CommitAttributionKinds.Automatic, Confidence = 0.9 }
            ]
        };
        var run = new RunRecord { Index = 1, HeadShaBefore = "aaa", HeadShaAfter = "bbb" };
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => new List<GitCommitInfo>
        {
            new("bbb", "bbb", DateTime.UtcNow, "Claude", "feat: thing", 2, 10, 1)
        });

        var c = Assert.Single(result.Commits);
        Assert.Equal(CommitAttributionKinds.Automatic, c.Attribution);
        Assert.Equal(0.9, c.Confidence);
    }

    [Fact]
    public void Aggregate_RangeCommitWithoutPersistedEntry_DefaultsToLegacyAttribution()
    {
        var info = new TaskInfo();
        var run = new RunRecord { Index = 1, HeadShaBefore = "aaa", HeadShaAfter = "bbb" };
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => new List<GitCommitInfo>
        {
            new("bbb", "bbb", DateTime.UtcNow, "A", "feat: thing", 1, 1, 0)
        });

        Assert.Equal(CommitAttributionKinds.Legacy, Assert.Single(result.Commits).Attribution);
    }

    [Fact]
    public void Aggregate_TaskBranchRunCommitsSurfaceWhenRangesCollapse()
    {
        // ASS-1712: an in-progress per-task-worktree job. Every run's SHA range
        // collapsed to before==after (the ranges track the shared develop HEAD,
        // not task/<id>) and the attribution chain is still empty (attribution
        // only runs when the task leaves 3-progress). Before the fix this
        // surfaced 0 commits; the durable run-trailer reconstruction must now
        // surface the full per-run history.
        var info = new TaskInfo();
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        var trivial1 = new RunRecord { Index = 1, HeadShaBefore = "dev", HeadShaAfter = "dev" };
        var trivial2 = new RunRecord { Index = 2, HeadShaBefore = "dev", HeadShaAfter = null };
        var reconstructed = new List<GitCommitInfo>
        {
            new("r3", "r3", t0.AddMinutes(20), "Claude", "feat: run 3", 1, 3, 0),
            new("r2", "r2", t0.AddMinutes(10), "Claude", "feat: run 2", 2, 5, 1),
            new("r1", "r1", t0.AddMinutes(1), "Claude", "feat: run 1", 1, 2, 0),
        };

        var result = TaskCommitsAggregator.Aggregate(
            info, [trivial1, trivial2], (_, _) => [], reconstructed);

        Assert.Equal(3, result.Count);
        Assert.Equal("feat: run 3", result.Commits[0].Subject); // newest first
        Assert.Equal("feat: run 1", result.Commits[2].Subject);
        Assert.All(result.Commits, c => Assert.Null(c.RunIndex));
        Assert.Equal(10, result.TotalAdded);
        Assert.Equal(1, result.TotalRemoved);
    }

    [Fact]
    public void Aggregate_TaskBranchRunCommitDoesNotDoubleCountWithRange()
    {
        // When a run range DID surface a commit, the same SHA arriving via
        // reconstruction must not double-count, and the richer run-derived
        // record (with RunIndex) wins.
        var info = new TaskInfo();
        var run = new RunRecord { Index = 1, HeadShaBefore = "h0", HeadShaAfter = "shared" };
        var reconstructed = new List<GitCommitInfo>
        {
            new("shared", "shared", DateTime.UtcNow, "Claude", "feat: shared", 1, 4, 0)
        };
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => new List<GitCommitInfo>
        {
            new("shared", "shared", DateTime.UtcNow, "Claude", "feat: shared", 1, 4, 0)
        }, reconstructed);

        Assert.Equal(1, result.Count);
        Assert.Equal(1, result.Commits[0].RunIndex); // range record wins
    }

    [Fact]
    public void Aggregate_TaskBranchRunCommitOverlaysAttributionWhenChainHasIt()
    {
        // A reconstructed commit that also has a persisted attribution entry
        // picks up that attribution/confidence overlay.
        var info = new TaskInfo
        {
            Commits =
            [
                new TaskCommitInfo { Sha = "r1", ShortSha = "r1", Message = "feat", At = DateTime.UtcNow, Attribution = CommitAttributionKinds.Automatic, Confidence = 0.8 }
            ]
        };
        var reconstructed = new List<GitCommitInfo>
        {
            new("r1", "r1", DateTime.UtcNow, "Claude", "feat: run 1", 1, 2, 0)
        };
        var result = TaskCommitsAggregator.Aggregate(info, [], (_, _) => [], reconstructed);

        var c = Assert.Single(result.Commits);
        Assert.Equal(CommitAttributionKinds.Automatic, c.Attribution);
        Assert.Equal(0.8, c.Confidence);
        Assert.Null(c.RunIndex);
    }

}
