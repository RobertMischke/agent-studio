using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F46 — contract for <see cref="ProjectRegistry.Delete"/>, the metadata half
/// of the destructive project-delete flow behind
/// <c>DELETE /api/projects/{PROJ-NNN}</c>. The endpoint deletes the on-disk
/// storage (covered by the WorkspaceManagementService path) and then calls
/// this to drop the registry row; here we lock in that the row is removed,
/// the removal is persisted, siblings are untouched, and unknown ids throw.
/// </summary>
public class ProjectRegistryDeleteTests : IDisposable
{
    private readonly string _repoRoot;

    public ProjectRegistryDeleteTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), "atp-proj-delete-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repoRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { /* best-effort */ }
    }

    private ProjectRegistry BuildRegistry()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["TaskRepository"] = _repoRoot })
            .Build();
        return new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
    }

    [Fact]
    public void Delete_RemovesRecord_AndReturnsIt()
    {
        var reg = BuildRegistry();
        var created = reg.EnsureProjectForStorage(
            storageLocation: Path.Combine(_repoRoot, "projects", "alpha"),
            initialDisplayName: "Alpha",
            workspaceId: "ws-default");

        var removed = reg.Delete(created.Id);

        Assert.Equal(created.Id, removed.Id);
        Assert.Null(reg.FindById(created.Id));
        Assert.DoesNotContain(reg.List(), p => p.Id == created.Id);
    }

    [Fact]
    public void Delete_IsPersisted_AcrossReload()
    {
        var reg = BuildRegistry();
        var created = reg.EnsureProjectForStorage(
            storageLocation: Path.Combine(_repoRoot, "projects", "beta"),
            initialDisplayName: "Beta",
            workspaceId: "ws-default");

        reg.Delete(created.Id);

        // A fresh instance reads projects.json from disk: the removal must
        // have been flushed, not just dropped from the in-memory cache.
        var reloaded = BuildRegistry();
        Assert.Null(reloaded.FindById(created.Id));
    }

    [Fact]
    public void Delete_UnknownId_Throws()
    {
        var reg = BuildRegistry();
        Assert.Throws<KeyNotFoundException>(() => reg.Delete("PROJ-999"));
    }

    [Fact]
    public void Delete_LeavesSiblingProjectsIntact()
    {
        var reg = BuildRegistry();
        var a = reg.EnsureProjectForStorage(Path.Combine(_repoRoot, "projects", "a"), "A", "ws-default");
        var b = reg.EnsureProjectForStorage(Path.Combine(_repoRoot, "projects", "b"), "B", "ws-default");

        reg.Delete(a.Id);

        Assert.Null(reg.FindById(a.Id));
        Assert.NotNull(reg.FindById(b.Id));
    }
}
