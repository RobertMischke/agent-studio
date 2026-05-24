using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

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

    private (WorkspaceRegistry workspaces, ProjectRegistry projects, JobScannerService scanner) Build(params (string name, string path)[] watchPaths)
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
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);
        return (workspaces, projects, scanner);
    }

    [Fact]
    public void Run_SeedsDefaultWorkspace_AndDiscoversProjects()
    {
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
    public void Run_NoTaskRepository_SkipsWithoutThrowing()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var workspaces = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        var projects = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new JobScannerService(config, NullLogger<JobScannerService>.Instance, summary);

        var exception = Record.Exception(() =>
            RegistryBootstrap.Run(workspaces, projects, scanner, NullLogger<RegistryBootstrapTests>.Instance));
        Assert.Null(exception);
        Assert.Empty(workspaces.List());
        Assert.Empty(projects.List());
    }
}
