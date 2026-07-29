using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// F45a — end-to-end boot pass: configured WatchPaths populate the
/// registries on first run and are idempotent on subsequent runs. The
/// bootstrap must not write into the watched folders themselves; only
/// <c>&lt;TaskRepository&gt;/.metadata/</c> is touched.
/// </summary>
public class RegistryBootstrapTests : IDisposable
{
    private readonly string _root;
    private readonly string _projectA;
    private readonly string _projectB;

    public RegistryBootstrapTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rdo-boot-" + Guid.NewGuid().ToString("N"));
        _projectA = Path.Combine(_root, "projects", "demo-a");
        _projectB = Path.Combine(_root, "projects", "demo-b");
        Directory.CreateDirectory(_projectA);
        Directory.CreateDirectory(_projectB);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private (WorkspaceRegistry workspaces, ProjectRegistry projects, TaskScannerService scanner) Build(params (string name, string path)[] watchPaths)
    {
        var dict = new Dictionary<string, string?> { ["TaskRepository"] = _root };
        for (var i = 0; i < watchPaths.Length; i++)
        {
            dict[$"WatchPaths:{i}:Name"] = watchPaths[i].name;
            dict[$"WatchPaths:{i}:Path"] = watchPaths[i].path;
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var workspaces = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        return (workspaces, projects, scanner);
    }

    [Fact]
    public void Run_SeedsDefaultWorkspace_AndDiscoversProjects()
    {
        Assert.False(File.Exists(RegistryPaths.ProjectsFilePath(_root)));
        var (workspaces, projects, scanner) = Build(
            ("Demo A", _projectA),
            ("Demo B", _projectB));

        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);

        Assert.Single(workspaces.List());
        Assert.Equal(DefaultWorkspace.Id, workspaces.List()[0].Id);

        var list = projects.List();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, p => p.DisplayName == "Demo A" && p.StorageLocation == _projectA);
        Assert.Contains(list, p => p.DisplayName == "Demo B" && p.StorageLocation == _projectB);
        Assert.All(list, p => Assert.Equal(DefaultWorkspace.Id, p.WorkspaceId));
    }

    [Fact]
    public void Run_ExistingEmptyRegistry_DoesNotRunLegacySeed()
    {
        Directory.CreateDirectory(RegistryPaths.MetadataDir(_root));
        File.WriteAllText(
            RegistryPaths.ProjectsFilePath(_root),
            """
            {
              "Version": 1,
              "NextProjectIdSeq": 1,
              "Projects": []
            }
            """);
        var (workspaces, projects, scanner) = Build(("Demo A", _projectA));

        RegistryBootstrap.Run(
            workspaces,
            projects,
            scanner,
            NullLogger<RegistryBootstrapTests>.Instance);

        Assert.Empty(projects.List());
    }

    [Fact]
    public void Run_InvalidExistingRegistry_AbortsWithoutLegacyReseed()
    {
        Directory.CreateDirectory(RegistryPaths.MetadataDir(_root));
        var corrupt =
            """
            {
              "Version": 1,
              "NextProjectIdSeq": 18,
              "Projects": [
                {
                  "Id": "PROJ-017",
                  "DisplayName": "Protected",
                  "ShortCode": "PRO",
                  "WorkspaceId": "ws-default",
                  "StorageLocation": "/protected",
                  "Urls": null
                }
              ]
            }
            """;
        var projectsFile = RegistryPaths.ProjectsFilePath(_root);
        File.WriteAllText(projectsFile, corrupt);
        var (workspaces, projects, scanner) = Build(("Legacy", _projectA));

        Assert.Throws<ProjectRegistryLoadException>(() =>
            RegistryBootstrap.Run(
                workspaces,
                projects,
                scanner,
                NullLogger<RegistryBootstrapTests>.Instance));

        Assert.Equal(corrupt, File.ReadAllText(projectsFile));
    }

    [Fact]
    public void Run_IsIdempotent_AcrossInvocations()
    {
        var (workspaces, projects, scanner) = Build(("Demo A", _projectA));
        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);
        var firstSnapshot = projects.List();

        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);
        var secondSnapshot = projects.List();

        Assert.Equal(firstSnapshot.Count, secondSnapshot.Count);
        Assert.Equal(firstSnapshot[0].Id, secondSnapshot[0].Id);
        Assert.Equal(firstSnapshot[0].CreatedAt, secondSnapshot[0].CreatedAt);
    }

    [Fact]
    public void Run_SkipsNonExistentWatchPaths()
    {
        var ghost = Path.Combine(_root, "does-not-exist");
        var (workspaces, projects, scanner) = Build(
            ("Real", _projectA),
            ("Ghost", ghost));

        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);

        var list = projects.List();
        Assert.Single(list);
        Assert.Equal("Real", list[0].DisplayName);
    }

    [Fact]
    public void Run_DoesNotWriteToWatchedFolders()
    {
        var (workspaces, projects, scanner) = Build(("Demo A", _projectA));

        var beforeCount = Directory.GetFileSystemEntries(_projectA).Length;
        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);
        var afterCount = Directory.GetFileSystemEntries(_projectA).Length;

        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public void Run_WritesMetadataUnderTaskRepository()
    {
        var (workspaces, projects, scanner) = Build(("Demo A", _projectA));
        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);

        Assert.True(File.Exists(RegistryPaths.WorkspacesFilePath(_root)));
        Assert.True(File.Exists(RegistryPaths.ProjectsFilePath(_root)));
    }

    [Fact]
    public void Run_LogsWarning_WhenWatchPathNameDivergesFromRegistry()
    {
        // First boot: WatchPath "Demo A" seeds the registry record.
        var (workspaces, projects, scanner) = Build(("Demo A", _projectA));
        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);
        var seeded = Assert.Single(projects.List());
        Assert.Equal("Demo A", seeded.DisplayName);

        // Second boot: operator edited WatchPath name to "Demo A Renamed".
        // Registry still has "Demo A" → divergence warning + registry wins.
        var (workspaces2, projects2, scanner2) = Build(("Demo A Renamed", _projectA));
        var capture = new CapturingLogger();
        RegistryBootstrap.Run(workspaces2, projects2, scanner2, capture);

        var unchanged = Assert.Single(projects2.List());
        Assert.Equal("Demo A", unchanged.DisplayName); // registry wins
        Assert.Contains(capture.Warnings, m => m.Contains("registry-bootstrap-watchpath-name-diverges"));
    }

    [Fact]
    public void Run_NoWatchPaths_AllProjectsStillLoadFromRegistry()
    {
        // Seed registry via the first boot.
        var (workspaces, projects, scanner) = Build(("Demo A", _projectA));
        RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance);

        // Operator now wipes WatchPaths from appsettings. Registry stays.
        var (workspaces2, projects2, scanner2) = Build(); // empty
        RegistryBootstrap.Run(workspaces2, projects2, scanner2, NullLogger<RegistryBootstrapTests>.Instance);

        var list = projects2.List();
        Assert.Single(list);
        Assert.Equal("Demo A", list[0].DisplayName);
    }

    private sealed class CapturingLogger : ILogger
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

    [Fact]
    public void Run_NoTaskRepository_SkipsWithoutThrowing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var workspaces = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);

        var exception = Record.Exception(() =>
            RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance));
        Assert.Null(exception);
        Assert.Empty(workspaces.List());
        Assert.Empty(projects.List());
    }
}
