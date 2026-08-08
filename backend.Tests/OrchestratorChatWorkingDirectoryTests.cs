using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class OrchestratorChatWorkingDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"orchestrator-chat-cwd-{Guid.NewGuid():N}");
    private readonly ProjectRegistry _registry;

    public OrchestratorChatWorkingDirectoryTests()
    {
        Directory.CreateDirectory(_root);
        var registryConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            })
            .Build();
        _registry = new ProjectRegistry(registryConfig, NullLogger<ProjectRegistry>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ResolveWorkingDirectory_PrefersWatchRootOverRegistryRepository()
    {
        var fixture = CreateProject("Project Root", watchRoot: "working", withRepository: true);

        var resolved = fixture.Service.ResolveWorkingDirectory(fixture.ProjectName, fixture.StoragePath);

        Assert.Equal(fixture.WatchRoot, resolved);
    }

    [Fact]
    public void ResolveWorkingDirectory_WithoutWatchRoot_UsesRegistryRepository()
    {
        var fixture = CreateProject("Project Repository", watchRoot: null, withRepository: true);

        var resolved = fixture.Service.ResolveWorkingDirectory(fixture.ProjectName, fixture.StoragePath);

        Assert.Equal(fixture.RepositoryPath, resolved);
        Assert.DoesNotContain(fixture.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void ResolveWorkingDirectory_WithoutConfiguredPaths_LogsTemporaryFallback()
    {
        var fixture = CreateProject("Project Temporary", watchRoot: null, withRepository: false);

        var resolved = fixture.Service.ResolveWorkingDirectory(fixture.ProjectName, fixture.StoragePath);

        Assert.Equal(Path.GetTempPath(), resolved);
        var warning = Assert.Single(fixture.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("Project Temporary", warning.Message);
        Assert.Contains("missing-watch-root-path-and-registry-repository-path", warning.Message);
        Assert.Contains(Path.GetTempPath(), warning.Message);
    }

    private Fixture CreateProject(string projectName, string? watchRoot, bool withRepository)
    {
        var slug = Guid.NewGuid().ToString("N");
        var storagePath = Path.Combine(_root, $"storage-{slug}");
        Directory.CreateDirectory(storagePath);

        string? resolvedWatchRoot = null;
        if (watchRoot != null)
        {
            resolvedWatchRoot = Path.Combine(_root, $"{watchRoot}-{slug}");
            Directory.CreateDirectory(resolvedWatchRoot);
        }

        string? repositoryPath = null;
        var project = _registry.EnsureProjectForStorage(
            storagePath, projectName, workspaceId: "workspace-tests");
        if (withRepository)
        {
            repositoryPath = Path.Combine(_root, $"repository-{slug}");
            Directory.CreateDirectory(Path.Combine(repositoryPath, ".git"));
            _registry.SetRepositoryPath(project.Id, repositoryPath);
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = projectName,
                ["WatchPaths:0:Path"] = storagePath,
                ["WatchPaths:0:RootPath"] = resolvedWatchRoot,
            })
            .Build();
        var summary = new AgentStudio.Review.SummaryGenerationService(
            NullLogger<AgentStudio.Review.SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(
            config,
            NullLogger<TaskScannerService>.Instance,
            summary,
            projectRegistry: _registry);
        var logger = new RecordingLogger<OrchestratorChatService>();
        var service = new OrchestratorChatService(
            chat: null!,
            runner: null!,
            sessionStore: null!,
            bootstrap: null!,
            scanner,
            config,
            logger,
            projects: _registry);

        return new Fixture(
            projectName,
            storagePath,
            resolvedWatchRoot,
            repositoryPath,
            service,
            logger);
    }

    private sealed record Fixture(
        string ProjectName,
        string StoragePath,
        string? WatchRoot,
        string? RepositoryPath,
        OrchestratorChatService Service,
        RecordingLogger<OrchestratorChatService> Logger);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
