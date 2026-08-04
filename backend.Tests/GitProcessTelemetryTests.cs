using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The MEASURE half of AGT-2007: <see cref="GitProcessTelemetry"/> is the
/// ambient per-request accounting that lets the git-info endpoints answer "how
/// many git processes ran for this request, and how long did they take". These
/// tests pin the two properties the optimization relies on: spawns accumulate
/// into the active request scope, and that scope flows across the thread-pool
/// boundary so a fanned-out (parallel) request still tallies every spawn.
/// </summary>
public class GitProcessTelemetryTests
{
    [Fact]
    public void Record_OutsideScope_IsNoOp()
    {
        Assert.Null(GitProcessTelemetry.CurrentTally());
        // No ambient scope: recording must be a silent no-op, never throw.
        GitProcessTelemetry.Record("status", 5, 0);
        Assert.Null(GitProcessTelemetry.CurrentTally());
    }

    [Fact]
    public void BeginRequest_AccumulatesSpawns_AndRestoresOnDispose()
    {
        Assert.Null(GitProcessTelemetry.CurrentTally());
        using (GitProcessTelemetry.BeginRequest("test/req", NullLogger.Instance))
        {
            GitProcessTelemetry.Record("status", 10, 0);
            GitProcessTelemetry.Record("diff", 20, 0);

            var tally = GitProcessTelemetry.CurrentTally();
            Assert.NotNull(tally);
            Assert.Equal(2, tally!.Value.Spawns);
            Assert.Equal(30, tally.Value.GitMs);
        }
        // Dispose restores the (absent) outer scope, so nothing leaks.
        Assert.Null(GitProcessTelemetry.CurrentTally());
    }

    [Fact]
    public void BeginRequest_NestedScope_RestoresOuterOnInnerDispose()
    {
        using (GitProcessTelemetry.BeginRequest("outer", NullLogger.Instance))
        {
            GitProcessTelemetry.Record("a", 1, 0);
            using (GitProcessTelemetry.BeginRequest("inner", NullLogger.Instance))
            {
                GitProcessTelemetry.Record("b", 2, 0);
                Assert.Equal(1, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
            }
            // Back in the outer scope, which only saw its own spawn.
            Assert.Equal(1, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }
    }

    [Fact]
    public void BeginRequest_WithNestedAggregation_IncludesChildSpawns()
    {
        using (GitProcessTelemetry.BeginRequest(
                   "outer",
                   NullLogger.Instance,
                   includeNested: true))
        {
            GitProcessTelemetry.Record("a", 1, 0);
            using (GitProcessTelemetry.BeginRequest("inner", NullLogger.Instance))
            {
                GitProcessTelemetry.Record("b", 2, 0);
            }

            var tally = GitProcessTelemetry.CurrentTally();
            Assert.NotNull(tally);
            Assert.Equal(2, tally!.Value.Spawns);
            Assert.Equal(3, tally.Value.GitMs);
        }
    }

    [Fact]
    public async Task BeginRequest_CountsSpawnsRecordedFromParallelTasks()
    {
        using (GitProcessTelemetry.BeginRequest("test/parallel", NullLogger.Instance))
        {
            // The ambient scope must flow into thread-pool work (captured
            // ExecutionContext); otherwise a request that fans its reads out in
            // parallel - the whole point of the optimization - would under-count.
            var tasks = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() => GitProcessTelemetry.Record("rev-parse", 3, 0)))
                .ToArray();
            await Task.WhenAll(tasks);

            var tally = GitProcessTelemetry.CurrentTally();
            Assert.NotNull(tally);
            Assert.Equal(16, tally!.Value.Spawns);
            Assert.Equal(48, tally.Value.GitMs);
        }
    }
}
