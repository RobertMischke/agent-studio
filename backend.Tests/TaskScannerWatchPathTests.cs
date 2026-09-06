using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression: a misconfigured watch path that resolves to a non-existent
/// folder (the Runbook <c>.orchestrator/jobs</c> case observed 2026-06-02)
/// made <see cref="TaskScannerService.ScanAllJobsRaw"/> log
/// "Watch path does not exist" on every scan. ScanAllJobsRaw runs on every
/// cache refresh, which a busy job's FileSystemWatcher churn triggers many
/// times per second, so the warning spammed the api log endlessly and buried
/// the real crash cause in the last seconds before a silent host death. The
/// warning must be throttled to once per missing path.
/// </summary>
public class TaskScannerWatchPathTests
{
    [Fact]
    public void GetWatchPaths_RelativeStore_JoinsRootBeforeCanonicalising()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), "atp-watch-root-" + Guid.NewGuid().ToString("N"));
        var relativeStore = Path.Combine(".orchestrator", "jobs");
        var expected = Path.GetFullPath(Path.Combine(projectRoot, relativeStore));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Quality Studio",
                ["WatchPaths:0:Path"] = relativeStore,
                ["WatchPaths:0:RootPath"] = projectRoot,
            })
            .Build();

        var scanner = BuildScanner(config, NullLogger<TaskScannerService>.Instance);

        var watchPath = Assert.Single(scanner.GetWatchPaths());
        Assert.Equal(expected, watchPath.Path);
        Assert.True(Path.IsPathFullyQualified(watchPath.Path));
    }

    [Fact]
    public void GetWatchPaths_SeparatorStrippedRegistryStore_RecoversAbsoluteLegacyStore()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "atp-registry-store-" + Guid.NewGuid().ToString("N"));
        var taskRepository = Path.Combine(testRoot, "task-repository");
        var projectRoot = Path.Combine(testRoot, "quality-studio");
        Directory.CreateDirectory(projectRoot);

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = taskRepository,
                })
                .Build();
            var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
            var project = registry.EnsureProjectForStorage(
                "C:Projectsquality-studio.orchestratorjobs",
                "Quality Studio",
                "ws-default");
            registry.SetRootPath(project.Id, projectRoot);
            var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
            var scanner = new TaskScannerService(
                config,
                NullLogger<TaskScannerService>.Instance,
                summary,
                projectRegistry: registry);

            var watchPath = Assert.Single(scanner.GetWatchPaths());
            var expected = Path.GetFullPath(Path.Combine(projectRoot, ".orchestrator", "jobs"));
            Assert.Equal(expected, watchPath.Path);
            Assert.True(Path.IsPathFullyQualified(watchPath.Path));
            Assert.DoesNotContain("Projectsquality-studio.orchestratorjobs", watchPath.Path);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void ScanAllJobsRaw_MissingWatchPath_WarnsOnlyOnceAcrossManyScans()
    {
        var missing = Path.Combine(Path.GetTempPath(), "atp-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        var capture = new CapturingLogger();
        var scanner = BuildScanner(missing, capture);

        for (var i = 0; i < 25; i++)
        {
            var jobs = scanner.ScanAllJobsRaw();
            Assert.Empty(jobs); // missing path contributes nothing
        }

        var warnings = capture.Warnings.Count(w => w.Contains("Watch path does not exist"));
        Assert.Equal(1, warnings);
    }

    [Fact]
    public void ScanAllJobsRaw_InvalidPhase_WarnsOnlyOncePerTaskAndValue()
    {
        var watchPath = Path.Combine(Path.GetTempPath(), "atp-invalid-phase-" + Guid.NewGuid().ToString("N"));
        var taskPath = Path.Combine(watchPath, TaskStates.HumanReview, "bad-phase");
        Directory.CreateDirectory(taskPath);
        File.WriteAllText(
            Path.Combine(taskPath, "task.json"),
            $$"""{"id":"bad-phase","title":"Bad phase","state":"{{TaskStates.HumanReview}}","phase":"{{LifecyclePhases.PostProcessingRunning}}","order":1}""");

        try
        {
            var capture = new CapturingLogger();
            var scanner = BuildScanner(watchPath, capture);

            for (var i = 0; i < 25; i++) scanner.ScanAllJobsRaw();

            var warnings = capture.Warnings.Count(w => w.Contains("is not allowed for state"));
            Assert.Equal(1, warnings);
        }
        finally
        {
            Directory.Delete(watchPath, recursive: true);
        }
    }

    [Fact]
    public void ScanAllJobsRaw_LegacyRepositoryPrefix_IsPersistedOnlyOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "atp-commit-repository-migration-" + Guid.NewGuid().ToString("N"));
        var watchPath = Path.Combine(root, "jobs");
        var taskPath = Path.Combine(watchPath, TaskStates.HumanReview, "agt-2307");
        Directory.CreateDirectory(taskPath);
        var taskJson = Path.Combine(taskPath, "task.json");
        File.WriteAllText(taskJson,
            $$"""{"id":"agt-2307","key":"AGT-2307","title":"Externalization sweep","state":"{{TaskStates.HumanReview}}","order":1,"projectName":"agent-studio","commits":[{"sha":"dcb54c7","shortSha":"dcb54c7","message":"[agent-studio] externalize package","filesChanged":1,"files":["package.json"],"at":"2026-08-04T12:00:00Z"}]}""");

        try
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "agent-studio",
                    ["WatchPaths:0:Path"] = watchPath,
                    ["WatchPaths:0:RootPath"] = root,
                })
                .Build();
            var scanner = BuildScanner(config, NullLogger<TaskScannerService>.Instance);

            var migrated = Assert.Single(scanner.ScanAllJobsRaw());
            var commit = Assert.Single(migrated.Commits);
            Assert.Equal("agent-studio", commit.Repository);
            Assert.Equal("develop", commit.Branch);
            var firstWrite = File.ReadAllText(taskJson);

            scanner.InvalidateCache();
            var rescanned = Assert.Single(scanner.ScanAllJobsRaw());

            Assert.Equal("agent-studio", Assert.Single(rescanned.Commits).Repository);
            Assert.Equal(firstWrite, File.ReadAllText(taskJson));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TaskScannerService BuildScanner(string watchPath, ILogger<TaskScannerService> logger)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "missing",
                ["WatchPaths:0:Path"] = watchPath
            })
            .Build();
        return BuildScanner(config, logger);
    }

    private static TaskScannerService BuildScanner(IConfiguration config, ILogger<TaskScannerService> logger)
    {
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, logger, summary);
    }

    private sealed class CapturingLogger : ILogger<TaskScannerService>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullDisposable.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
