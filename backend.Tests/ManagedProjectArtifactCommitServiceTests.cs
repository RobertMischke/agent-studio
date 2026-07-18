using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ManagedProjectArtifactCommitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "managed-project-artifact-" + Guid.NewGuid().ToString("N"));
    private readonly string _repository;
    private readonly string _watchPath;

    public ManagedProjectArtifactCommitServiceTests()
    {
        _repository = Path.Combine(_root, "repository");
        _watchPath = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(_repository);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        RunGit(_repository, "init", "-q", "-b", "main");
        RunGit(_repository, "config", "user.name", "test");
        RunGit(_repository, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_repository, "README.md"), "seed\n");
        RunGit(_repository, "add", "README.md");
        RunGit(_repository, "commit", "-q", "-m", "seed");
    }

    [Fact]
    public async Task ExecuteAsync_CommitsStampsAndQueuesEveryProducedArtifact()
    {
        var (service, scanner, queue) = Build();
        var task = WriteTask(scanner, "AGT-501");
        var artifact = Path.Combine(_repository, "docs", "learnings", "agt-501.md");

        var result = await service.ExecuteAsync(
            task,
            PipelineCatalogue.WikiLearningsStepId,
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                File.WriteAllText(artifact, "# Durable learning\n");
                return new ManagedProjectArtifactOutput(
                    "Ok", "Wrote one learning.", "docs/learnings/agt-501.md");
            },
            default);

        Assert.True(result.Success, result.Error);
        Assert.True(result.DidCommit);
        Assert.True(result.PushQueued);
        Assert.Matches("^[a-f0-9]{40,64}$", result.CommitSha!);
        Assert.Equal(string.Empty, RunGitCapture(_repository, "status", "--porcelain=v1"));
        Assert.Equal(
            "docs/learnings/agt-501.md",
            RunGitCapture(_repository, "show", "--name-only", "--pretty=format:", "HEAD"));
        Assert.Contains(
            $"docs(pipeline): run {PipelineCatalogue.WikiLearningsStepId} for AGT-501",
            RunGitCapture(_repository, "log", "-1", "--format=%B"));

        var stamped = scanner.FindJob(task.Id, _watchPath);
        Assert.NotNull(stamped?.Commit);
        Assert.Equal(result.CommitSha, stamped!.Commit!.Sha);
        Assert.Contains(stamped.Commits, commit => commit.Sha == result.CommitSha);

        Assert.True(queue.Reader.TryRead(out var push));
        Assert.Equal(task.Id, push!.Job.Id);
        Assert.False(push.RequireCompletedState);
        Assert.Contains(push.Job.Commits, commit => commit.Sha == result.CommitSha);
    }

    [Fact]
    public async Task ExecuteAsync_PreExistingDirtyCheckoutRejectsWriteAndPreservesForeignChanges()
    {
        var (service, scanner, queue) = Build();
        var task = WriteTask(scanner, "AGT-502");
        var foreign = Path.Combine(_repository, "foreign.txt");
        File.WriteAllText(foreign, "operator work\n");
        var callbackRan = false;

        var result = await service.ExecuteAsync(
            task,
            PipelineCatalogue.WikiLearningsStepId,
            () =>
            {
                callbackRan = true;
                return new ManagedProjectArtifactOutput("Ok", "should not run", null);
            },
            default);

        Assert.False(result.Success);
        Assert.Contains("pre-existing change", result.Error);
        Assert.False(callbackRan);
        Assert.Equal("operator work\n", File.ReadAllText(foreign));
        Assert.False(queue.Reader.TryRead(out _));
        Assert.Equal("seed", RunGitCapture(_repository, "log", "-1", "--format=%s"));
    }

    [Fact]
    public async Task ExecuteAsync_FailedWriterResultRollsBackGeneratedPathsWithoutCommitOrPush()
    {
        var (service, scanner, queue) = Build();
        var task = WriteTask(scanner, "AGT-503");
        var artifact = Path.Combine(_repository, "docs", "wiki", "failed.md");

        var result = await service.ExecuteAsync(
            task,
            PipelineCatalogue.WikiLearningsStepId,
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
                File.WriteAllText(artifact, "partial output\n");
                return new ManagedProjectArtifactOutput("Failed", "runner rejected the artifact", "docs/failed.md");
            },
            default);

        Assert.False(result.Success);
        Assert.False(File.Exists(artifact));
        Assert.Equal(string.Empty, RunGitCapture(_repository, "status", "--porcelain=v1"));
        Assert.Equal("seed", RunGitCapture(_repository, "log", "-1", "--format=%s"));
        Assert.False(queue.Reader.TryRead(out _));
    }

    private (ManagedProjectArtifactCommitService Service, TaskScannerService Scanner, CompletedPushQueue Queue) Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "same mutable display name",
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _repository,
                ["WatchPaths:0:RepositoryPath"] = _repository,
                ["TaskRepository"] = _watchPath,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
        var mutations = new TaskMutationService(
            scanner,
            clients,
            registry,
            new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
            NullLogger<TaskMutationService>.Instance);
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config, prompts);
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
        var queue = new CompletedPushQueue();
        var service = new ManagedProjectArtifactCommitService(
            git,
            scanner,
            mutations,
            settings,
            queue,
            NullLogger<ManagedProjectArtifactCommitService>.Instance);
        return (service, scanner, queue);
    }

    private TaskInfo WriteTask(TaskScannerService scanner, string id)
    {
        var folder = Path.Combine(_watchPath, TaskStates.Completed, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key = id,
            title = "Managed artifact task",
            state = TaskStates.Completed,
            order = 1,
            agent = "codex",
        }));
        return scanner.FindJob(id, _watchPath)!;
    }

    private static void RunGit(string cwd, params string[] args)
    {
        var result = RunGitRaw(cwd, args);
        Assert.True(result.Code == 0, $"git {string.Join(' ', args)} failed: {result.Stdout} {result.Stderr}");
    }

    private static string RunGitCapture(string cwd, params string[] args)
    {
        var result = RunGitRaw(cwd, args);
        Assert.True(result.Code == 0, $"git {string.Join(' ', args)} failed: {result.Stdout} {result.Stderr}");
        return result.Stdout.Trim();
    }

    private static (string Stdout, string Stderr, int Code) RunGitRaw(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (stdout, stderr, process.ExitCode);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch (Exception ex) { SilentCatch.Note(ex, "ManagedProjectArtifactCommitServiceTests: normalize file"); }
            }
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ManagedProjectArtifactCommitServiceTests: cleanup");
        }
    }
}
