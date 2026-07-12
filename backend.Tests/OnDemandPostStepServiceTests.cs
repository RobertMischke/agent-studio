using AgentStudio.Persistence;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrchestratorApi.Tests;

public sealed class OnDemandPostStepServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "on-demand-post-step-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AddAndRerun_AppendsCardPlanAndVersionedAttempts()
    {
        Directory.CreateDirectory(_root);
        var jobFolder = Path.Combine(_root, "tasks", "done", "AGT-1");
        Directory.CreateDirectory(jobFolder);
        var service = CreateService();
        var task = new TaskInfo
        {
            Id = "AGT-1",
            Title = "Completed task",
            State = TaskStates.Completed,
            FolderPath = jobFolder,
            ProjectName = "demo",
        };
        var project = new WatchPathEntry { Name = "demo", Path = _root, RootPath = _root };

        var first = await service.RunAsync(task, project, PipelineCatalogue.AgentsWikiSyncStepId, addToCard: true, default);
        var second = await service.RunAsync(task, project, PipelineCatalogue.AgentsWikiSyncStepId, addToCard: true, default);

        Assert.Equal(1, first.Attempt);
        Assert.Equal(2, second.Attempt);
        Assert.Equal([PipelineCatalogue.AgentsWikiSyncStepId], service.ReadPlan(jobFolder));
        Assert.Equal(2, service.ReadAttempts(jobFolder).Count);
        Assert.True(File.Exists(Path.Combine(jobFolder, "logs", OnDemandPostStepService.AttemptsFileName)));
        Assert.True(File.Exists(Path.Combine(jobFolder, "results", "post-steps", "post-agents-wiki-sync-attempt-001.md")));
        Assert.True(File.Exists(Path.Combine(jobFolder, "results", "post-steps", "post-agents-wiki-sync-attempt-002.md")));
        Assert.True(File.Exists(Path.Combine(_root, AgentsWikiSyncPostStepRunner.IndexRepoRel.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task UnknownStep_IsRejectedWithoutWritingAPlan()
    {
        Directory.CreateDirectory(_root);
        var task = new TaskInfo { Id = "AGT-2", FolderPath = _root, State = TaskStates.Completed };
        var project = new WatchPathEntry { Name = "demo", Path = _root, RootPath = _root };

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            CreateService().RunAsync(task, project, "post-arbitrary-shell", addToCard: true, default));

        Assert.Empty(CreateService().ReadPlan(_root));
    }

    private static OnDemandPostStepService CreateService() => new(
        new WikiMaintenancePostStepRunner(NullLogger<WikiMaintenancePostStepRunner>.Instance),
        new WikiLearningsPostStepRunner(NullLogger<WikiLearningsPostStepRunner>.Instance),
        new AgentsWikiSyncPostStepRunner(NullLogger<AgentsWikiSyncPostStepRunner>.Instance),
        new JsonlAppender());

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "OnDemandPostStepServiceTests cleanup"); }
    }
}
