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
    public void Aggregate_SubtractsExcludedShas_FromRunRanges()
    {
        // A crash-recovery for another task landed inside this task's run
        // window. Once attribution records it in ExcludedCommits, the
        // aggregate must drop it even though git still reaches it.
        var info = new TaskInfo
        {
            ExcludedCommits =
            [
                new TaskExcludedCommitInfo { Sha = "noise", ShortSha = "noise", Reason = CommitExclusionReasons.CrashRecoveryOfOtherTask }
            ]
        };
        var run = new RunRecord { Index = 1, HeadShaBefore = "h0", HeadShaAfter = "h1" };
        var result = TaskCommitsAggregator.Aggregate(info, [run], (_, _) => new List<GitCommitInfo>
        {
            new("real", "real", DateTime.UtcNow, "Claude", "feat: real", 1, 3, 0),
            new("noise", "noise", DateTime.UtcNow, "boot", "chore(crash-recovery): rescue orphan changes for other", 1, 9, 0)
        });

        Assert.Equal(1, result.Count);
        Assert.Equal("real", result.Commits[0].Sha);
        var excluded = Assert.Single(result.Excluded);
        Assert.Equal("noise", excluded.ShortSha);
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

}
