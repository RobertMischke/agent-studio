using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Pure-function tests for <see cref="JobCommitsAggregation.WithReconstructedInProgressCommits(TaskInfo, TaskCommitsAggregate)"/>
/// - the ASS-1712 projection that folds the reconstructed task-branch history
/// into an in-progress task's <see cref="TaskInfo.Commits"/> so Task-Detail
/// shows the full history instead of one collapsed commit. The git-touching
/// <see cref="JobCommitsAggregation.Build"/> binding is covered indirectly by
/// the existing <c>TaskCommitsAggregatorTests</c>; here we pin the guard +
/// mapping + ordering rules without a git repo.
/// </summary>
public class JobCommitsAggregationTests
{
    private static TaskCommitsAggregate Agg(params TaskCommitRecord[] commits)
        => new() { Count = commits.Length, Commits = commits.ToList() };

    private static TaskCommitRecord Rec(string sha, DateTime at, string subject)
        => new() { Sha = sha, ShortSha = sha, AuthorDateUtc = at, Subject = subject, FilesChanged = 1, Added = 2, Removed = 0 };

    [Fact]
    public void Project_InProgressEmptyChain_FoldsReconstructedHistoryOldestFirst()
    {
        var info = new TaskInfo { Id = "j", State = TaskStates.Progress };
        var t0 = DateTime.UtcNow.AddMinutes(-30);
        // The aggregate arrives newest-first (aggregator convention).
        var agg = Agg(
            Rec("r3", t0.AddMinutes(20), "feat: run 3"),
            Rec("r2", t0.AddMinutes(10), "feat: run 2"),
            Rec("r1", t0.AddMinutes(1), "feat: run 1"));

        var result = JobCommitsAggregation.WithReconstructedInProgressCommits(info, agg);

        Assert.NotSame(info, result);
        Assert.Equal(3, result.Commits.Count);
        Assert.Equal("r1", result.Commits[0].Sha); // re-ordered oldest -> newest
        Assert.Equal("r3", result.Commits[2].Sha);
        Assert.Equal("feat: run 1", result.Commits[0].Message); // Subject -> Message
        Assert.Empty(result.Commits[0].Files);
    }

    [Fact]
    public void Project_InProgressSingularChain_IsReplacedByRicherHistory()
    {
        var info = new TaskInfo
        {
            Id = "j",
            State = TaskStates.Progress,
            Commits = [new TaskCommitInfo { Sha = "snap", ShortSha = "snap", Message = "chore: snapshot", At = DateTime.UtcNow }],
        };
        var agg = Agg(Rec("r2", DateTime.UtcNow, "b"), Rec("r1", DateTime.UtcNow.AddMinutes(-5), "a"));

        var result = JobCommitsAggregation.WithReconstructedInProgressCommits(info, agg);

        Assert.Equal(2, result.Commits.Count);
        Assert.Equal("r1", result.Commits[0].Sha);
    }

    [Fact]
    public void Project_ChainAlreadyMultiEntry_IsUnchanged()
    {
        var info = new TaskInfo
        {
            Id = "j",
            State = TaskStates.Progress,
            Commits = [new TaskCommitInfo { Sha = "a" }, new TaskCommitInfo { Sha = "b" }],
        };
        var agg = Agg(Rec("x", DateTime.UtcNow, "x"), Rec("y", DateTime.UtcNow, "y"), Rec("z", DateTime.UtcNow, "z"));

        var result = JobCommitsAggregation.WithReconstructedInProgressCommits(info, agg);

        Assert.Same(info, result); // already has a real chain; never narrowed
    }

    [Fact]
    public void Project_NotInProgress_IsUnchanged()
    {
        var info = new TaskInfo { Id = "j", State = TaskStates.AutoReview };
        var agg = Agg(Rec("r1", DateTime.UtcNow, "a"), Rec("r2", DateTime.UtcNow, "b"));

        var result = JobCommitsAggregation.WithReconstructedInProgressCommits(info, agg);

        Assert.Same(info, result); // outside 3-progress the persisted chain is authoritative
    }

    [Fact]
    public void Project_AggregateNotRicherThanChain_IsUnchanged()
    {
        var info = new TaskInfo { Id = "j", State = TaskStates.Progress };

        var result = JobCommitsAggregation.WithReconstructedInProgressCommits(info, Agg());

        Assert.Same(info, result); // nothing to add
    }

    [Fact]
    public void Project_CarriesAttributionAndConfidence()
    {
        var info = new TaskInfo { Id = "j", State = TaskStates.Progress };
        var rec = new TaskCommitRecord
        {
            Sha = "r1", ShortSha = "r1", AuthorDateUtc = DateTime.UtcNow, Subject = "feat",
            FilesChanged = 2, Attribution = CommitAttributionKinds.Automatic, Confidence = 0.7,
        };

        var result = JobCommitsAggregation.WithReconstructedInProgressCommits(info, Agg(rec));

        var c = Assert.Single(result.Commits);
        Assert.Equal(CommitAttributionKinds.Automatic, c.Attribution);
        Assert.Equal(0.7, c.Confidence);
    }
}
