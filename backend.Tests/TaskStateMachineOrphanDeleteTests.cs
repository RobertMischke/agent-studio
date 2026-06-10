using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression contract for the terminal-lane orphan-folder delete path.
/// Before this branch existed the DELETE endpoint returned 404 for any folder
/// whose <c>task.json</c> was missing - exactly the zombie folders that the
/// AGENTS.md "API first" rule is supposed to keep cleanable, and exactly the
/// case where operators historically reached for forbidden manual deletion.
/// </summary>
public class TaskStateMachineOrphanDeleteTests : IDisposable
{
    private readonly string _watchPath;

    public TaskStateMachineOrphanDeleteTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-orphan-delete-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void DeleteOrphanFolder_RemovesJobJsonLessFolder_UnderArchive()
    {
        var slug = "orphan-zombie";
        var folder = Path.Combine(_watchPath, TaskStates.Archive, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "leftover content");

        var states = BuildStates();
        var outcome = states.DeleteOrphanFolder(_watchPath, TaskStates.Archive, slug);

        Assert.Equal(OrphanFolderDeleteStatus.Success, outcome.Status);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteOrphanFolder_ReturnsNotFound_WhenFolderIsUnknown()
    {
        var states = BuildStates();
        var outcome = states.DeleteOrphanFolder(_watchPath, TaskStates.Archive, "never-existed");
        Assert.Equal(OrphanFolderDeleteStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void DeleteOrphanFolder_RefusesPathTraversalFolder()
    {
        var states = BuildStates();
        Assert.Equal(OrphanFolderDeleteStatus.InvalidRequest,
            states.DeleteOrphanFolder(_watchPath, TaskStates.Archive, "../escape").Status);
        Assert.Equal(OrphanFolderDeleteStatus.InvalidRequest,
            states.DeleteOrphanFolder(_watchPath, TaskStates.Archive, "foo/bar").Status);
        Assert.Equal(OrphanFolderDeleteStatus.InvalidRequest,
            states.DeleteOrphanFolder(_watchPath, TaskStates.Archive, "foo\\bar").Status);
    }

    [Fact]
    public void DeleteOrphanFolder_RefusesWhenFolderHasJobJson()
    {
        // Defensive guard: a folder that does carry a task.json should
        // never be deleted via the orphan branch. If the scanner did not
        // surface it, that is a scanner regression to investigate, not
        // a folder to wipe.
        var slug = "scanner-blind-spot";
        var folder = Path.Combine(_watchPath, TaskStates.Archive, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), "{ this is not valid json");

        var states = BuildStates();
        var outcome = states.DeleteOrphanFolder(_watchPath, TaskStates.Archive, slug);

        Assert.Equal(OrphanFolderDeleteStatus.HasJobJson, outcome.Status);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteOrphanFolder_RefusesNonTerminalLane()
    {
        var slug = "live-lane-orphan";
        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "leftover content");

        var states = BuildStates();
        var outcome = states.DeleteOrphanFolder(_watchPath, TaskStates.Ready, slug);

        Assert.Equal(OrphanFolderDeleteStatus.NonTerminalLane, outcome.Status);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteJob_LiveJobBranch_StillWorks()
    {
        var slug = "live-job";
        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{slug}\",\"state\":\"{TaskStates.Ready}\",\"order\":10,\"agent\":\"copilot\"}}");

        var states = BuildStates();
        Assert.True(states.DeleteJob(slug, _watchPath));
        Assert.False(Directory.Exists(folder));
    }

    private TaskStateMachine BuildStates()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "orphan-delete-test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
    }
}
