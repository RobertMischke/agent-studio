using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression contract for the orphan-folder branch of
/// <see cref="TaskStateMachine.DeleteJob"/>. Before this branch existed
/// the DELETE endpoint returned 404 for any folder whose <c>job.json</c>
/// was missing - exactly the zombie folders that the AGENTS.md "API
/// first" rule is supposed to keep cleanable, and exactly the case
/// where operators historically reached for the forbidden manual
/// <c>rm -rf</c>.
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
    public void DeleteJob_RemovesOrphanFolder_WithoutJobJson()
    {
        var slug = "orphan-zombie";
        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "leftover content");

        var states = BuildStates();
        var ok = states.DeleteJob(slug, _watchPath);

        Assert.True(ok);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteJob_OrphanFolder_ReturnsFalse_WhenSlugIsUnknown()
    {
        var states = BuildStates();
        Assert.False(states.DeleteJob("never-existed", _watchPath));
    }

    [Fact]
    public void DeleteJob_OrphanFolder_RefusesPathTraversalSlug()
    {
        var states = BuildStates();
        Assert.False(states.DeleteJob("../escape", _watchPath));
        Assert.False(states.DeleteJob("foo/bar", _watchPath));
        Assert.False(states.DeleteJob("foo\\bar", _watchPath));
    }

    [Fact]
    public void DeleteJob_OrphanFolder_RefusesWhenFolderHasJobJson()
    {
        // Defensive guard: a folder that does carry a job.json should
        // never be deleted via the orphan branch. If the scanner did not
        // surface it, that is a scanner regression to investigate, not
        // a folder to wipe.
        var slug = "scanner-blind-spot";
        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "job.json"), "{ this is not valid json");

        var states = BuildStates();
        Assert.False(states.DeleteJob(slug, _watchPath));
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void DeleteJob_LiveJobBranch_StillWorks()
    {
        var slug = "live-job";
        var folder = Path.Combine(_watchPath, TaskStates.Ready, slug);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "job.json"),
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
