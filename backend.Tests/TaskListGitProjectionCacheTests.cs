using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskListGitProjectionCacheTests
{
    [Theory]
    [InlineData(false, false, false, false, true)]
    [InlineData(true, false, true, false, true)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, true, true, false)]
    public void RefreshPolicy_QueuesOnlyColdChangedOrDueIdleEntry(
        bool hasSnapshot,
        bool refreshing,
        bool inputChanged,
        bool refreshDue,
        bool expected)
    {
        Assert.Equal(
            expected,
            TaskListGitRefreshPolicy.ShouldQueue(
                hasSnapshot,
                refreshing,
                inputChanged,
                refreshDue));
    }

    [Fact]
    public void ReadCacheOnly_DetachesGitWorkAndReturnsWithoutWaiting()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = Job("task-1");
        var signal = new TaskMergeSignal { Branch = "task/task-1" };
        var projection = new TaskListGitProjection(
            new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal)
            {
                [task.TaskKey] = signal,
            },
            new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
            new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
            new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal));
        var cache = new TaskListGitProjectionCache(
            _ =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                GitProcessTelemetry.Record("rev-list", 25, 0);
                return projection;
            },
            NullLogger<TaskListGitProjectionCache>.Instance,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z")));

        using (GitProcessTelemetry.BeginRequest(
                   "tasks/list",
                   NullLogger.Instance,
                   includeNested: true))
        {
            var stopwatch = Stopwatch.StartNew();
            var cold = cache.ReadCacheOnly([task]);
            stopwatch.Stop();

            Assert.Same(TaskListGitProjection.Empty, cold);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            Assert.True(SpinWait.SpinUntil(
                () => !ReferenceEquals(cache.ReadCacheOnly([task]), TaskListGitProjection.Empty),
                TimeSpan.FromSeconds(5)));
            Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }

        var warm = cache.ReadCacheOnly([task]);
        Assert.Same(signal, warm.Merge[task.TaskKey]);
    }

    [Fact]
    public void ReadCacheOnly_OverThreeHundredTasksUnderConcurrentLoad_RemainsMemoryOnlyAndUnderBudget()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 300).Select(index => Job($"task-{index:D3}")).ToArray();
        var refreshCalls = 0;
        var refreshed = new TaskListGitProjection(
            new Dictionary<string, TaskMergeSignal>(StringComparer.Ordinal)
            {
                [tasks[0].TaskKey] = new TaskMergeSignal { Branch = "task/task-000" },
            },
            new Dictionary<string, TaskIntegrationStatus>(StringComparer.Ordinal),
            new Dictionary<string, TaskPublishSignal>(StringComparer.Ordinal),
            new Dictionary<string, TaskTestRunEvidence>(StringComparer.Ordinal));
        var cache = new TaskListGitProjectionCache(
            _ =>
            {
                Interlocked.Increment(ref refreshCalls);
                started.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return refreshed;
            },
            NullLogger<TaskListGitProjectionCache>.Instance,
            new FakeTimeProvider(DateTimeOffset.Parse("2026-08-03T12:00:00Z")));

        try
        {
            using var telemetry = GitProcessTelemetry.BeginRequest(
                "tasks/list",
                NullLogger.Instance,
                includeNested: true);
            _ = cache.ReadCacheOnly(tasks);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            var stopwatch = Stopwatch.StartNew();
            Parallel.For(0, 64, _ => cache.ReadCacheOnly(tasks));
            stopwatch.Stop();

            Assert.Equal(1, Volatile.Read(ref refreshCalls));
            Assert.Equal(0, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"64 concurrent cache-only Git reads over 300 tasks took {stopwatch.ElapsedMilliseconds}ms.");
        }
        finally
        {
            release.Set();
            Assert.True(SpinWait.SpinUntil(
                () => ReferenceEquals(cache.ReadCacheOnly(tasks), refreshed),
                TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task BuildProjectionAsync_StartsAllLookupsBeforeWaitingForResults()
    {
        using var started = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        var task = Job("parallel");

        Dictionary<string, TValue> Lookup<TValue>()
        {
            started.Signal();
            release.Wait(TimeSpan.FromSeconds(5));
            GitProcessTelemetry.Record("rev-list", 25, 0);
            return new Dictionary<string, TValue>(StringComparer.Ordinal);
        }

        Task<TaskListGitProjection> projectionTask;
        using (GitProcessTelemetry.BeginRequest(
                   "tasks/list-refresh-test",
                   NullLogger.Instance,
                   includeNested: true))
        {
            projectionTask = TaskListGitProjectionCache.BuildProjectionAsync(
                [task],
                _ => Lookup<TaskMergeSignal>(),
                _ => Lookup<TaskIntegrationStatus>(),
                _ => Lookup<TaskPublishSignal>(),
                _ => Lookup<TaskTestRunEvidence>());

            var allStarted = started.Wait(TimeSpan.FromSeconds(5));
            release.Set();
            var projection = await projectionTask;

            Assert.True(allStarted, "All four lookup projections should start concurrently.");
            Assert.NotNull(projection);
            Assert.Equal(4, GitProcessTelemetry.CurrentTally()!.Value.Spawns);
        }
    }

    private static TaskInfo Job(string id)
        => new()
        {
            Id = id,
            TaskKey = $"watch::{id}",
            Title = id,
            State = TaskStates.Completed,
            ProjectName = "project",
            WatchPath = "watch",
            Commits =
            [
                new TaskCommitInfo
                {
                    Sha = "0123456789abcdef0123456789abcdef01234567",
                    FilesChanged = 1,
                },
            ],
        };
}
