using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Phase 2b of the API cleanup (ASS-1760): the read / update / delete task
/// endpoints resolve a path-free project handle — a short code / Kürzel
/// (<c>ASS</c>) or a stable <c>PROJ-NNN</c> id — to the project's watchPath
/// server-side via <see cref="TaskEndpointHelpers.ResolveWatchPath"/>, so the
/// filesystem layout no longer has to be sent as a <c>?watchPath=</c> query
/// param. The deprecated <c>watchPath</c> param still works for legacy callers.
/// </summary>
public class TaskEndpointProjectHandleResolutionTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public TaskEndpointProjectHandleResolutionTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "resolve-watchpath-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ResolveWatchPath_ByShortCode_ReturnsStorageLocation()
    {
        var registry = BuildRegistryWithProject(out var record);
        registry.SetShortCode(record.Id, "ASS");

        Assert.Equal(record.StorageLocation, TaskEndpointHelpers.ResolveWatchPath(registry, "ASS", watchPath: null));
        // Case-insensitive: the Kürzel is a human handle, not a byte-exact key.
        Assert.Equal(record.StorageLocation, TaskEndpointHelpers.ResolveWatchPath(registry, "ass", watchPath: null));
    }

    [Fact]
    public void ResolveWatchPath_ByProjectId_ReturnsStorageLocation()
    {
        var registry = BuildRegistryWithProject(out var record);

        Assert.Equal(record.StorageLocation, TaskEndpointHelpers.ResolveWatchPath(registry, record.Id, watchPath: null));
    }

    [Fact]
    public void ResolveWatchPath_ProjectHandle_WinsOverWatchPath()
    {
        var registry = BuildRegistryWithProject(out var record);
        registry.SetShortCode(record.Id, "ASS");

        var resolved = TaskEndpointHelpers.ResolveWatchPath(registry, "ASS", watchPath: @"C:\bogus\path");
        Assert.Equal(record.StorageLocation, resolved);
    }

    [Fact]
    public void ResolveWatchPath_NoProject_ReturnsWatchPathVerbatim()
    {
        var registry = BuildRegistryWithProject(out _);

        Assert.Equal(_watchPath, TaskEndpointHelpers.ResolveWatchPath(registry, project: null, watchPath: _watchPath));
        Assert.Equal(_watchPath, TaskEndpointHelpers.ResolveWatchPath(registry, project: "  ", watchPath: _watchPath));
        Assert.Null(TaskEndpointHelpers.ResolveWatchPath(registry, project: null, watchPath: null));
    }

    [Fact]
    public void ResolveWatchPath_UnknownHandle_FallsBackToWatchPath()
    {
        var registry = BuildRegistryWithProject(out _);

        // Unknown short code / id must not silently target another project: it
        // falls through to the (here null) watchPath, so the caller lands on its
        // own not-found path rather than a wrong-project mutation.
        Assert.Null(TaskEndpointHelpers.ResolveWatchPath(registry, "NOPE", watchPath: null));
        Assert.Equal(_watchPath, TaskEndpointHelpers.ResolveWatchPath(registry, "PROJ-999", watchPath: _watchPath));
    }

    private ProjectRegistry BuildRegistryWithProject(out ProjectRecord record)
    {
        var registry = new ProjectRegistry(BuildConfig(), NullLogger<ProjectRegistry>.Instance);
        record = registry.EnsureProjectForStorage(_watchPath, "Demo Project", "default");
        return registry;
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
