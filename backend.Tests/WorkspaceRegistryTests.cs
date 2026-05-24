using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F45a — covers the load / seed / persist contract for
/// <see cref="WorkspaceRegistry"/>. Uses a temporary TaskRepository so the
/// production registry file is never touched.
/// </summary>
public class WorkspaceRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _config;

    public WorkspaceRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rdo-ws-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
            }).Build();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private WorkspaceRegistry Build() => new(_config, NullLogger<WorkspaceRegistry>.Instance);

    [Fact]
    public void EnsureDefaultWorkspace_WritesFile_AndIsIdempotent()
    {
        var reg = Build();
        var first = reg.EnsureDefaultWorkspace();
        var second = reg.EnsureDefaultWorkspace();

        Assert.Equal(DefaultWorkspace.Id, first.Id);
        Assert.Equal(first.CreatedAt, second.CreatedAt); // not re-stamped
        Assert.Single(reg.List());
        Assert.True(File.Exists(RegistryPaths.WorkspacesFilePath(_root)));
    }

    [Fact]
    public void DefaultWorkspace_RoundTrips_ThroughFreshInstance()
    {
        Build().EnsureDefaultWorkspace();

        // A second instance backed by the same TaskRepository loads the
        // persisted state on first call.
        var reloaded = Build();
        var list = reloaded.List();

        Assert.Single(list);
        var ws = Assert.Single(list);
        Assert.Equal(DefaultWorkspace.Id, ws.Id);
        Assert.True(ws.IsDefault);
        Assert.Equal(DefaultWorkspace.DisplayName, ws.DisplayName);
    }

    [Fact]
    public void Find_ReturnsNullForUnknownId()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        Assert.Null(reg.Find("ws-does-not-exist"));
    }

    [Fact]
    public void NoTaskRepository_OperatesInMemoryOnly()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var reg = new WorkspaceRegistry(config, NullLogger<WorkspaceRegistry>.Instance);
        Assert.False(reg.IsPersistent);
        // EnsureDefaultWorkspace still seeds the in-memory state but does
        // not throw when there is no backing path.
        var ws = reg.EnsureDefaultWorkspace();
        Assert.Equal(DefaultWorkspace.Id, ws.Id);
        Assert.Single(reg.List());
    }

    [Fact]
    public void List_ReturnsCopy_NotInternalReference()
    {
        var reg = Build();
        reg.EnsureDefaultWorkspace();
        var first = reg.List();
        first.Clear();
        Assert.NotEmpty(reg.List());
    }

    [Fact]
    public void List_SortsBySortOrderThenDisplayName()
    {
        var reg = Build();
        // Seed three workspaces via the internal Replace hook used by F45b.
        var now = DateTime.UtcNow;
        reg.GetType().GetMethod("Replace", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(reg, [new WorkspacesFile
            {
                Workspaces =
                [
                    new WorkspaceRecord { Id = "c", DisplayName = "Charlie", SortOrder = 1, CreatedAt = now },
                    new WorkspaceRecord { Id = "a", DisplayName = "Alpha",   SortOrder = 0, CreatedAt = now },
                    new WorkspaceRecord { Id = "b", DisplayName = "Bravo",   SortOrder = 0, CreatedAt = now },
                ]
            }]);

        var list = reg.List();
        Assert.Equal(["Alpha", "Bravo", "Charlie"], list.Select(w => w.DisplayName));
    }
}
