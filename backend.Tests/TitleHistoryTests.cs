using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Feature: in-task iteration with evolving title + history.
/// Renames recorded through <see cref="TaskMutationService.SetJobTitle"/>
/// append a row to <c>title-history.json</c> in the job folder; the
/// scanner surfaces the history on <see cref="TaskDetail.TitleHistory"/>.
/// </summary>
public class TitleHistoryTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TitleHistoryTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-title-history-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SetJobTitle_AppendsHistoryEntryAndSurfacesOnDetail()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "rename-me",
            Title = "Original title",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            TargetState = TaskStates.Ready
        });

        Assert.True(mutations.SetJobTitle("rename-me", "Second title", _watchPath));
        Assert.True(mutations.SetJobTitle("rename-me", "Final title", _watchPath));

        var detail = scanner.GetJobDetail("rename-me", _watchPath);
        Assert.NotNull(detail);
        Assert.Equal("Final title", detail!.Info.Title);

        Assert.Equal(2, detail.TitleHistory.Count);
        Assert.Equal("Original title", detail.TitleHistory[0].OldTitle);
        Assert.Equal("Second title", detail.TitleHistory[0].NewTitle);
        Assert.Equal("api", detail.TitleHistory[0].Source);
        Assert.True(detail.TitleHistory[0].At <= detail.TitleHistory[1].At);

        Assert.Equal("Second title", detail.TitleHistory[1].OldTitle);
        Assert.Equal("Final title", detail.TitleHistory[1].NewTitle);
    }

    [Fact]
    public void SetJobTitle_NoOpRenameDoesNotAppend()
    {
        var (machine, scanner, mutations) = Build();
        machine.EnsureStateFoldersAndMigrate();

        mutations.CreateJob(new CreateTaskRequest
        {
            Id = "stable",
            Title = "Same",
            WatchPath = _watchPath,
            Agent = "claude",
            CliType = "claude",
            TargetState = TaskStates.Ready
        });

        Assert.True(mutations.SetJobTitle("stable", "Same", _watchPath));
        Assert.True(mutations.SetJobTitle("stable", "  Same  ", _watchPath));

        var detail = scanner.GetJobDetail("stable", _watchPath);
        Assert.NotNull(detail);
        Assert.Empty(detail!.TitleHistory);
    }

    [Fact]
    public void GetJobDetail_LegacyJobWithoutHistoryFileReturnsEmptyHistory()
    {
        var (machine, scanner, _) = Build();
        machine.EnsureStateFoldersAndMigrate();

        var jobDir = Path.Combine(_watchPath, TaskStates.Ready, "legacy");
        Directory.CreateDirectory(jobDir);
        File.WriteAllText(Path.Combine(jobDir, "task.json"), """
            {
              "id": "legacy",
              "title": "Legacy job",
              "state": "2-ready",
              "order": 10,
              "agent": "claude",
              "cliType": "claude"
            }
            """);

        var detail = scanner.GetJobDetail("legacy", _watchPath);
        Assert.NotNull(detail);
        Assert.Empty(detail!.TitleHistory);
    }

    private (TaskStateMachine machine, TaskScannerService scanner, TaskMutationService mutations) Build()
    {
        var config = BuildConfig();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        return (machine, scanner, mutations);
    }

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();
}
