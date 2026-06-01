using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F21: 100 parallel lane-mutating writes against the same project must
/// serialise via <see cref="LaneMutexRegistry"/>. The contract this test
/// pins down:
///
/// <list type="number">
/// <item>Each input write either succeeds exactly once, or fails with a
/// recoverable status (<c>NotFound</c> when another writer already moved
/// the source, <c>TargetFolderExists</c> when the slug is taken). No
/// silent partial states.</item>
/// <item>The final lane layout shows each slug exactly once, in exactly
/// one lane. No <c>-2</c> collision-suffix sibling, no leftover skeleton
/// folders in the source lane.</item>
/// </list>
///
/// The test is deliberately filesystem-level: hammering the actual
/// <see cref="TaskStateMachine.MoveJob"/> entry point catches both the
/// mutex wiring and any future regression that re-introduces a direct
/// <see cref="Directory.Move"/> call inside the move path.
/// </summary>
public class LaneMutexRegistryConcurrencyTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "concurrency-demo";

    public LaneMutexRegistryConcurrencyTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "lane-mutex-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Acquire_SerialisesTwoConcurrentHolders_OnTheSameKey()
    {
        var registry = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);

        var inside = 0;
        var maxInside = 0;
        var iterations = 200;

        Parallel.For(0, iterations, _ =>
        {
            using (registry.Acquire(_watchPath))
            {
                var current = Interlocked.Increment(ref inside);
                // Record the high-water mark without locking; the
                // assertion below tolerates a stale read because the
                // mutex contract means it can never exceed 1.
                var snapshot = maxInside;
                if (current > snapshot) maxInside = current;
                Thread.Sleep(1);
                Interlocked.Decrement(ref inside);
            }
        });

        Assert.Equal(0, inside);
        Assert.True(maxInside <= 1, $"Expected at most 1 concurrent holder; saw {maxInside}.");
    }

    [Fact]
    public void Acquire_DoesNotSerialise_AcrossDifferentKeys()
    {
        var registry = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var keyA = Path.Combine(_workspace, "projects", "project-a");
        var keyB = Path.Combine(_workspace, "projects", "project-b");

        using var holdA = registry.Acquire(keyA);
        // If keyB blocked on keyA we'd hang here; the test's wall clock
        // bound is xunit's default.
        using var holdB = registry.Acquire(keyB, TimeSpan.FromSeconds(2));

        // Reaching here is the assertion.
        Assert.True(true);
    }

    [Fact]
    public void OneHundredParallelMovesOnSameProject_ProduceNoCollisionFoldersOrOrphans()
    {
        // Seed 100 distinct jobs under 3-progress so they all race to
        // move out of the same lane on the same project.
        const int N = 100;
        for (int i = 0; i < N; i++)
        {
            SeedJob(TaskStates.Progress, $"slug-{i:000}");
        }

        var states = BuildStateMachine();

        // Round-robin every move to one of the legal targets so the
        // serialisation also has to handle multiple distinct targets,
        // not just the trivial single-target case.
        var targets = new[] { TaskStates.AutoReview, TaskStates.HumanReview, TaskStates.Completed };

        var outcomes = new MoveJobStatus[N];
        Parallel.For(0, N, i =>
        {
            var slug = $"slug-{i:000}";
            var target = targets[i % targets.Length];
            var outcome = states.MoveJob(slug, target, _watchPath);
            outcomes[i] = outcome.Status;
        });

        // Every outcome is a successful move. With the mutex in place
        // there is no situation where a job is "stuck mid-move" - the
        // serialisation makes each move atomic w.r.t. the next.
        Assert.All(outcomes, status => Assert.Equal(MoveJobStatus.Success, status));

        // Source lane is empty.
        var leftoverInProgress = Directory.GetDirectories(Path.Combine(_watchPath, TaskStates.Progress));
        Assert.Empty(leftoverInProgress);

        // Each slug appears exactly once across the destination lanes,
        // with no `-2` collision-suffix folder anywhere on the tree.
        var allDestinationFolders = targets
            .SelectMany(t => Directory.GetDirectories(Path.Combine(_watchPath, t)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(N, allDestinationFolders.Count);
        Assert.Equal(N, allDestinationFolders.Distinct().Count());
        Assert.DoesNotContain(allDestinationFolders, name => name != null && name.Contains("-2-", StringComparison.Ordinal));
        Assert.DoesNotContain(allDestinationFolders, name => name != null && name.EndsWith("-2", StringComparison.Ordinal));

        // Every moved folder still has a job.json file. The "reader
        // catches mid-rename" race would leave an empty skeleton.
        foreach (var target in targets)
        {
            foreach (var folder in Directory.GetDirectories(Path.Combine(_watchPath, target)))
            {
                Assert.True(File.Exists(Path.Combine(folder, "job.json")),
                    $"Expected job.json in moved folder {folder}.");
            }
        }
    }

    [Fact]
    public void ConcurrentMoveAndDelete_OnSameSlug_NeverProducesPartialFolder()
    {
        // Two threads race: one moves slug-0 from 3-progress to
        // 4-auto-review, the other deletes the folder outright. The
        // mutex guarantees a single winner; the loser either fails
        // cleanly (NotFound) or is a no-op.
        SeedJob(TaskStates.Progress, "race-target");

        var states = BuildStateMachine();

        var moved = false;
        var deleted = false;

        Parallel.Invoke(
            () => { moved = states.MoveJob("race-target", TaskStates.AutoReview, _watchPath).Status == MoveJobStatus.Success; },
            () => { deleted = states.DeleteJob("race-target", _watchPath); });

        // Exactly one of the two operations succeeded.
        Assert.True(moved ^ deleted,
            $"Expected exactly one of move/delete to succeed; moved={moved}, deleted={deleted}.");

        var progressFolders = Directory.GetDirectories(Path.Combine(_watchPath, TaskStates.Progress));
        var autoReviewFolders = Directory.GetDirectories(Path.Combine(_watchPath, TaskStates.AutoReview));

        Assert.Empty(progressFolders);
        if (moved)
        {
            Assert.Single(autoReviewFolders);
            Assert.True(File.Exists(Path.Combine(autoReviewFolders[0], "job.json")));
        }
        else
        {
            Assert.Empty(autoReviewFolders);
        }
    }

    private void SeedJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "job.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}");
    }

    private TaskStateMachine BuildStateMachine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        return new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, registry);
    }
}
