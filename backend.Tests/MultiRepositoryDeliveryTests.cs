using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using AgentStudio.Registry;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>AGT-2307 regression fixture for repository-scoped delivery truth.</summary>
public sealed class MultiRepositoryDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "multi-repository-delivery-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BuildLookup_EvaluatesEachAttributedRepositoryAgainstItsOwnBranches()
    {
        Directory.CreateDirectory(_root);
        var studio = SeedRepository("agent-studio", "develop", out var studioSha);
        var runner = SeedRepository("runner", "main", out var runnerSha);
        var runnerUrl = new Uri(runner).AbsoluteUri;
        RunGit(runner, "checkout -q -b task/missing");
        File.WriteAllText(Path.Combine(runner, "missing.txt"), "not integrated");
        Commit(runner, "feat: missing runner delivery");
        var missingRunnerSha = RunGit(runner, "rev-parse HEAD").Out.Trim();

        var taskRepository = Path.Combine(_root, "task-store");
        Directory.CreateDirectory(taskRepository);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = taskRepository,
            ["WatchPaths:0:Name"] = "Agent Studio",
            ["WatchPaths:0:RootPath"] = studio,
            ["WatchPaths:0:RepositoryPath"] = studio,
            ["WatchPaths:0:Path"] = Path.Combine(taskRepository, "tasks"),
        }).Build();
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        registry.Append(Project("PROJ-001", "Agent Studio", "AGT", studio, "agent-studio"));
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        settings.SetIntegrationBranch("Agent Studio", "develop");
        settings.SetIntegrationBranch("Runner", "main");
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var service = new TaskIntegrationStatusService(
            git,
            settings,
            new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
            NullLogger<TaskIntegrationStatusService>.Instance,
            registry);
        var job = new TaskInfo
        {
            Id = "externalization-sweep",
            Key = "AGT-2307",
            ProjectName = "Agent Studio",
            WatchPath = Path.Combine(taskRepository, "tasks"),
            FolderPath = Path.Combine(taskRepository, "tasks", "externalization-sweep"),
            State = TaskStates.HumanReview,
            Commits =
            [
                Delivery(studioSha, "[agent-studio] feat: externalize studio"),
                Delivery(runnerSha, "[runner] feat: externalize runner", runnerUrl, "main"),
                Delivery(missingRunnerSha, "[runner] fix: follow-up not delivered", runnerUrl, "main"),
            ],
        };

        var status = service.BuildLookup([job])[job.TaskKey];

        Assert.Equal(IntegrationStatuses.Partial, status.Status);
        Assert.Equal(2, status.Repositories.Count);
        var studioStatus = Assert.Single(status.Repositories, entry => entry.Label == "agent-studio");
        Assert.True(studioStatus.OnIntegrationBranch);
        var runnerStatus = Assert.Single(status.Repositories, entry => entry.Label == "runner");
        Assert.False(runnerStatus.OnIntegrationBranch);
        Assert.Contains(missingRunnerSha[..7], runnerStatus.Detail);
        Assert.DoesNotContain(studioSha[..7], runnerStatus.Detail);
        Assert.Contains("runner", status.Detail);
    }

    private static ProjectRecord Project(
        string id,
        string name,
        string code,
        string repositoryPath,
        string repositoryName) => new()
    {
        Id = id,
        DisplayName = name,
        ShortCode = code,
        WorkspaceId = "ws-default",
        StorageLocation = Path.Combine(repositoryPath, ".orchestrator", "jobs"),
        RepositoryPath = repositoryPath,
        Urls = [new ProjectUrlRecord { Id = "repo", Label = "repo", Url = $"https://example.test/{repositoryName}.git" }],
        CreatedAt = DateTime.UtcNow,
    };

    private string SeedRepository(string name, string branch, out string sha)
    {
        var repository = Path.Combine(_root, name);
        Directory.CreateDirectory(repository);
        RunGit(repository, $"init -q -b {branch}");
        RunGit(repository, "config user.email test@example.com");
        RunGit(repository, "config user.name test");
        RunGit(repository, $"remote add origin https://example.test/{name}.git");
        File.WriteAllText(Path.Combine(repository, "README.md"), name);
        RunGit(repository, "add -A");
        Commit(repository, $"feat: seed {name}");
        sha = RunGit(repository, "rev-parse HEAD").Out.Trim();
        if (branch == "develop") RunGit(repository, "branch main");
        return repository;
    }

    private static TaskCommitInfo Delivery(
        string sha,
        string message,
        string? repository = null,
        string? branch = null) => new()
    {
        Sha = sha,
        ShortSha = sha[..7],
        Message = message,
        Repository = repository,
        Branch = branch,
        FilesChanged = 1,
        Files = ["README.md"],
        At = DateTime.UtcNow,
        Attribution = CommitAttributionKinds.Automatic,
    };

    private static void Commit(string repository, string message)
    {
        RunGit(repository, "add -A");
        RunGit(repository, $"commit -q -m \"{message}\"");
    }

    private static (string Out, int Code) RunGit(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {arguments} failed: {error}");
        return (output, process.ExitCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
