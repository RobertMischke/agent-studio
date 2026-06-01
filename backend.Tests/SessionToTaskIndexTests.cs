using System.Diagnostics;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the contract for <see cref="SessionToTaskIndex"/>: orphan ids
/// return null, recovery sentinels are skipped, and the multi-checkout
/// tie-break prefers a cwd match.
///
/// The perf assertion follows AGENTS.md "Regression-proofing: data, then
/// five-whys, then test-then-fix": we measure index rebuild on a realistic
/// 200-job board first, then judge the implementation against the
/// measurement. The plan target is "well under 50 ms"; we leave headroom
/// for slow CI runners and assert under 250 ms. The real fix is far
/// under this ceiling - if it ever fires, look for accidental disk I/O
/// in the rebuild path or a quadratic chain walk.
/// </summary>
public class SessionToJobIndexTests
{
    [Fact]
    public void OrphanSessionId_ReturnsNull()
    {
        var index = new SessionToTaskIndex();
        index.Rebuild(new[]
        {
            MakeJob("job-a", TaskStates.Progress, watchPath: "/w/p1", chain: new[] { "s-1" })
        });

        Assert.Null(index.Lookup("orphan-id"));
        Assert.Equal("job-a", index.Lookup("s-1")!.JobId);
    }

    [Fact]
    public void RecoverySentinel_IsSkipped()
    {
        var index = new SessionToTaskIndex();
        index.Rebuild(new[]
        {
            MakeJob("job-a", TaskStates.Progress, watchPath: "/w/p1",
                chain: new[] { "before-id", SessionToTaskIndex.RecoverySentinel, "after-id" })
        });

        // The sentinel itself must not show up as a key.
        Assert.Null(index.Lookup(SessionToTaskIndex.RecoverySentinel));
        // Ids on both sides of the sentinel resolve back to the owning job.
        Assert.Equal("job-a", index.Lookup("before-id")!.JobId);
        Assert.Equal("job-a", index.Lookup("after-id")!.JobId);
        // Counted session ids exclude the sentinel.
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void EmptyOrWhitespaceChainEntries_AreSkipped()
    {
        var index = new SessionToTaskIndex();
        index.Rebuild(new[]
        {
            MakeJob("job-a", TaskStates.Progress, watchPath: "/w/p1",
                chain: new[] { "", "   ", "real-id" })
        });

        Assert.Equal(1, index.Count);
        Assert.Equal("job-a", index.Lookup("real-id")!.JobId);
    }

    [Fact]
    public void MultiCheckoutCollision_PrefersCwdMatch()
    {
        // Two jobs in different checkouts share the same Claude session id
        // (the user has the same ~/.claude store visible to both).
        var index = new SessionToTaskIndex();
        var devJob = MakeJob("job-dev", TaskStates.Ready,    watchPath: "/w/dev",    chain: new[] { "shared-uuid" });
        var stbJob = MakeJob("job-stb", TaskStates.Progress, watchPath: "/w/stable", chain: new[] { "shared-uuid" });
        index.Rebuild(new[] { devJob, stbJob });

        var hitDev = index.Lookup("shared-uuid", sessionCwd: "/w/dev");
        Assert.Equal("job-dev", hitDev!.JobId);

        var hitStable = index.Lookup("shared-uuid", sessionCwd: "/w/stable");
        Assert.Equal("job-stb", hitStable!.JobId);
    }

    [Fact]
    public void MultiCheckoutCollision_NoCwd_PrefersProgressLane()
    {
        var index = new SessionToTaskIndex();
        index.Rebuild(new[]
        {
            MakeJob("idle-job", TaskStates.Ready,    watchPath: "/w/a", chain: new[] { "s-1" }),
            MakeJob("hot-job",  TaskStates.Progress, watchPath: "/w/b", chain: new[] { "s-1" }),
        });

        var hit = index.Lookup("s-1");
        Assert.Equal("hot-job", hit!.JobId);
    }

    [Fact]
    public void Rebuild_Replaces_PreviousIndex()
    {
        var index = new SessionToTaskIndex();
        index.Rebuild(new[] { MakeJob("old", TaskStates.Archive, watchPath: "/w", chain: new[] { "gone" }) });
        Assert.NotNull(index.Lookup("gone"));

        index.Rebuild(new[] { MakeJob("new", TaskStates.Progress, watchPath: "/w", chain: new[] { "fresh" }) });
        Assert.Null(index.Lookup("gone"));
        Assert.Equal("new", index.Lookup("fresh")!.JobId);
    }

    [Fact]
    public void Rebuild_Over200Jobs_FinishesWellUnderFiftyMs()
    {
        // Build a realistic-ish board: 200 jobs, average chain length ~3
        // (a fresh start id, a continue id, sometimes a recovery break and
        // a new id). The plan calls out the 200-job / <50 ms target.
        const int jobCount = 200;
        var rng = new Random(1337);
        var jobs = new List<TaskInfo>(jobCount);
        for (var i = 0; i < jobCount; i++)
        {
            var chainLen = 2 + rng.Next(3); // 2..4
            var chain = new List<string>(chainLen);
            for (var c = 0; c < chainLen; c++)
            {
                // Inject a recovery sentinel roughly 1 in 6 ids so the skip
                // path is exercised at scale.
                if (rng.Next(6) == 0) chain.Add(SessionToTaskIndex.RecoverySentinel);
                chain.Add($"sess-{i:D4}-{c}");
            }
            jobs.Add(MakeJob($"job-{i:D4}", TaskStates.Archive, watchPath: "/w/perf", chain: chain.ToArray()));
        }

        var index = new SessionToTaskIndex();
        // Warm-up rebuild so JIT and allocator are settled - the assertion
        // measures steady-state behaviour, not first-touch cost.
        index.Rebuild(jobs);

        var sw = Stopwatch.StartNew();
        index.Rebuild(jobs);
        sw.Stop();

        Assert.True(
            sw.ElapsedMilliseconds < 250,
            $"SessionToTaskIndex rebuild over {jobCount} jobs took {sw.ElapsedMilliseconds} ms; " +
            "the plan target is well under 50 ms. If this regresses, look for accidental " +
            "disk I/O or per-job allocation churn in the rebuild path.");
        // Sanity: every job contributed at least one non-sentinel id.
        Assert.True(index.Count >= jobCount,
            $"expected at least {jobCount} session ids; got {index.Count}");
    }

    private static TaskInfo MakeJob(string id, string lane, string watchPath, string[] chain) => new()
    {
        Id = id,
        TaskKey = TaskIdentity.CreateKey(watchPath, id),
        Title = id,
        State = lane,
        WatchPath = watchPath,
        ProjectName = "test-project",
        FolderPath = Path.Combine(watchPath, lane, id),
        SessionChain = chain.ToList()
    };
}
