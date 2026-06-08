using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks in the contract for <see cref="TaskStateMachine.MoveJob"/>.
/// The pre-existing-target-folder case used to surface as a generic 404 in the
/// UI, then briefly as a <c>TargetFolderExists</c> 409. Both stranded
/// Archive-all behind a stale namesake. It is now collision-safe: the move
/// auto-suffixes the moved folder to a globally-unique slug and succeeds,
/// leaving the pre-existing folder untouched. See the duplicate-slug
/// root-cause fix (Layer 2).
/// </summary>
public class TaskScannerMoveTests : IDisposable
{
    private readonly string _watchPath;

    public TaskScannerMoveTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "agent-taskboard-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private TaskStateMachine BuildStateMachine()
        => new(BuildScanner(), NullLogger<TaskStateMachine>.Instance);

    private void WriteJob(string state, string slug)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"copilot\"}}");
    }

    [Fact]
    public void MoveJob_HappyPath_ReturnsSuccess()
    {
        WriteJob(TaskStates.Completed, "demo-task");

        var outcome = BuildStateMachine().MoveJob("demo-task", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "demo-task")));
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "demo-task")));
    }

    [Fact]
    public void MoveJob_StampsEnteredLaneAtOnTargetJobJson()
    {
        // WriteJob deliberately omits enteredLaneAt (a legacy folder). The move
        // must stamp it so the lane-entry sort has an anchor in the new lane.
        WriteJob(TaskStates.Completed, "stamp-task");

        var outcome = BuildStateMachine().MoveJob("stamp-task", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        var json = File.ReadAllText(
            Path.Combine(_watchPath, TaskStates.Archive, "stamp-task", "task.json"));
        Assert.Contains("\"enteredLaneAt\"", json);
    }

    [Fact]
    public void Scanner_LegacyJobWithoutEnteredLaneAt_FallsBackToLastActivity()
    {
        // A legacy folder has no enteredLaneAt field. The scanner must not throw
        // and must surface a non-default EnteredLaneAt, falling back to the
        // folder's LastActivity so the lane-entry sort degrades gracefully.
        WriteJob(TaskStates.Ready, "legacy-task");

        var jobs = BuildScanner().ScanAllJobsRaw();
        var legacy = jobs.Single(j => j.Id == "legacy-task");

        Assert.NotEqual(default, legacy.EnteredLaneAt);
        Assert.Equal(legacy.LastActivity, legacy.EnteredLaneAt);
    }

    [Fact]
    public void MoveJob_UnknownId_ReturnsNotFound()
    {
        var outcome = BuildStateMachine().MoveJob("ghost", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void MoveJob_TargetFolderAlreadyExists_AutoSuffixesAndSucceeds()
    {
        // Completed/duplicate-slug is the real task; Archive/duplicate-slug is
        // a stale namesake left behind in another lane. FindJob resolves the
        // earliest-state copy (Completed) as the move source. The move into
        // 7-archive collides on the occupied slug; Layer 2 resolves it by
        // suffixing the moved folder rather than failing with a 409.
        WriteJob(TaskStates.Completed, "duplicate-slug");
        WriteJob(TaskStates.Archive, "duplicate-slug");

        var outcome = BuildStateMachine().MoveJob("duplicate-slug", TaskStates.Archive, _watchPath);

        Assert.Equal(MoveJobStatus.Success, outcome.Status);
        Assert.Equal(
            Path.Combine(_watchPath, TaskStates.Archive, "duplicate-slug-2"),
            outcome.NewFolderPath);

        // Source lane is drained; the pre-existing namesake is untouched; the
        // moved folder lives under the suffixed slug with its task.json intact.
        Assert.False(Directory.Exists(Path.Combine(_watchPath, TaskStates.Completed, "duplicate-slug")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "duplicate-slug")));
        Assert.True(Directory.Exists(Path.Combine(_watchPath, TaskStates.Archive, "duplicate-slug-2")));
        Assert.True(File.Exists(Path.Combine(_watchPath, TaskStates.Archive, "duplicate-slug-2", "task.json")));

        // The canonical id was rewritten to match the new folder name so the
        // scanner does not have to self-heal a divergent id on the next pass.
        var movedJson = File.ReadAllText(
            Path.Combine(_watchPath, TaskStates.Archive, "duplicate-slug-2", "task.json"));
        Assert.Contains("\"duplicate-slug-2\"", movedJson);
    }
}
