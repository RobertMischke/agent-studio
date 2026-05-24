using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Registry;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// F45a — id allocation, lookup, and per-project task-key counter
/// behaviour for <see cref="ProjectRegistry"/>. Tests use a temp
/// TaskRepository so the production state is never touched.
/// </summary>
public class ProjectRegistryTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _config;

    public ProjectRegistryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "rdo-proj-reg-" + Guid.NewGuid().ToString("N"));
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

    private ProjectRegistry Build() => new(_config, NullLogger<ProjectRegistry>.Instance);

    [Fact]
    public void EnsureProjectForStorage_AllocatesPROJ001_FirstTime()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(
            storageLocation: Path.Combine(_root, "projects", "demo"),
            initialDisplayName: "Agent Task Processor",
            workspaceId: DefaultWorkspace.Id);

        Assert.Equal("PROJ-001", p.Id);
        Assert.Equal("ATP", p.ShortCode);
        Assert.Equal("Agent Task Processor", p.DisplayName);
        Assert.Equal(DefaultWorkspace.Id, p.WorkspaceId);
        Assert.Equal(1, p.NextTaskKeySeq);
        Assert.False(p.Archived);
    }

    [Fact]
    public void EnsureProjectForStorage_Idempotent_ForSamePath()
    {
        var reg = Build();
        var path = Path.Combine(_root, "projects", "demo");
        var first = reg.EnsureProjectForStorage(path, "Demo", DefaultWorkspace.Id);
        var second = reg.EnsureProjectForStorage(path, "Demo Renamed", DefaultWorkspace.Id);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Demo", second.DisplayName); // initial name preserved
        Assert.Single(reg.List());
    }

    [Fact]
    public void EnsureProjectForStorage_NormalisesPath()
    {
        var reg = Build();
        var asWindows = "C:\\Demo\\Path";
        var asPosix = "C:/Demo/Path";
        var first = reg.EnsureProjectForStorage(asWindows, "Demo", DefaultWorkspace.Id);
        var lookup = reg.FindByStorageLocation(asPosix);

        Assert.NotNull(lookup);
        Assert.Equal(first.Id, lookup!.Id);
    }

    [Fact]
    public void AllocateIds_AreMonotonic_AcrossProjects()
    {
        var reg = Build();
        var a = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "One",   DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "Two",   DefaultWorkspace.Id);
        var c = reg.EnsureProjectForStorage(Path.Combine(_root, "p3"), "Three", DefaultWorkspace.Id);

        Assert.Equal(["PROJ-001", "PROJ-002", "PROJ-003"], new[] { a.Id, b.Id, c.Id });
    }

    [Fact]
    public void ShortCode_Collision_GetsNumericSuffix()
    {
        var reg = Build();
        var a = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Agent Task Processor", DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "Agent Task Processor", DefaultWorkspace.Id);

        Assert.Equal("ATP", a.ShortCode);
        Assert.Equal("ATP2", b.ShortCode);
    }

    [Fact]
    public void IssueNextTaskKey_IsMonotonic_PerProject()
    {
        var reg = Build();
        var a = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "A", DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "B", DefaultWorkspace.Id);

        var a1 = reg.IssueNextTaskKey(a.Id);
        var a2 = reg.IssueNextTaskKey(a.Id);
        var b1 = reg.IssueNextTaskKey(b.Id);
        var a3 = reg.IssueNextTaskKey(a.Id);

        Assert.Equal(1, a1);
        Assert.Equal(2, a2);
        Assert.Equal(3, a3);
        Assert.Equal(1, b1);
    }

    [Fact]
    public void IssueNextTaskKey_ThrowsForUnknownProjectId()
    {
        var reg = Build();
        Assert.Throws<KeyNotFoundException>(() => reg.IssueNextTaskKey("PROJ-999"));
    }

    [Fact]
    public void EnsureTaskKeyFloor_RaisesButNeverLowers()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        reg.EnsureTaskKeyFloor(p.Id, 50);
        Assert.Equal(50, reg.IssueNextTaskKey(p.Id));

        // Floor below current counter is a no-op.
        reg.EnsureTaskKeyFloor(p.Id, 10);
        Assert.Equal(51, reg.IssueNextTaskKey(p.Id));
    }

    [Fact]
    public void State_RoundTrips_ThroughFreshInstance()
    {
        var reg = Build();
        reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "A", DefaultWorkspace.Id);
        var b = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "B", DefaultWorkspace.Id);
        reg.IssueNextTaskKey(b.Id);
        reg.IssueNextTaskKey(b.Id);
        reg.IssueNextTaskKey(b.Id);

        var reloaded = Build();
        var pa = reloaded.FindById("PROJ-001")!;
        var pb = reloaded.FindById("PROJ-002")!;

        Assert.Equal("A", pa.DisplayName);
        Assert.Equal(1, pa.NextTaskKeySeq);
        Assert.Equal("B", pb.DisplayName);
        Assert.Equal(4, pb.NextTaskKeySeq);
    }

    // ------------------------------------------------------------------
    // F45b mutation tests (ADR-0042)
    // ------------------------------------------------------------------

    [Fact]
    public void Rename_ChangesDisplayName_KeepsIdAndShortCode()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Old Name", DefaultWorkspace.Id);

        var renamed = reg.Rename(p.Id, "New Name");

        Assert.Equal(p.Id, renamed.Id);
        Assert.Equal(p.ShortCode, renamed.ShortCode);
        Assert.Equal("New Name", renamed.DisplayName);
    }

    [Fact]
    public void SetShortCode_NormalisesUppercase_AndValidates()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        var updated = reg.SetShortCode(p.Id, "rbk");
        Assert.Equal("RBK", updated.ShortCode);

        Assert.Throws<ArgumentException>(() => reg.SetShortCode(p.Id, "a"));         // too short
        Assert.Throws<ArgumentException>(() => reg.SetShortCode(p.Id, "abcdefg"));   // too long
        Assert.Throws<ArgumentException>(() => reg.SetShortCode(p.Id, "ab-cd"));     // invalid char
    }

    [Fact]
    public void SetShortCode_RejectsDuplicate()
    {
        var reg = Build();
        reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Project One", DefaultWorkspace.Id);
        var second = reg.EnsureProjectForStorage(Path.Combine(_root, "p2"), "Project Two", DefaultWorkspace.Id);
        reg.SetShortCode(second.Id, "ZZZ");
        var third = reg.EnsureProjectForStorage(Path.Combine(_root, "p3"), "Project Three", DefaultWorkspace.Id);

        Assert.Throws<InvalidOperationException>(() => reg.SetShortCode(third.Id, "ZZZ"));
    }

    [Fact]
    public void SetColor_SetsAndClears()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        Assert.Null(p.Color);
        Assert.Equal("#abcdef", reg.SetColor(p.Id, "#abcdef").Color);
        Assert.Null(reg.SetColor(p.Id, null).Color);
    }

    [Fact]
    public void SetWorkspace_ReassignsAndRefusesUnknownWorkspace()
    {
        var workspaces = new WorkspaceRegistry(_config, NullLogger<WorkspaceRegistry>.Instance);
        workspaces.EnsureDefaultWorkspace();
        var frontendWs = workspaces.Create("Frontend");

        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);

        var moved = reg.SetWorkspace(p.Id, frontendWs.Id, workspaces);
        Assert.Equal(frontendWs.Id, moved.WorkspaceId);

        Assert.Throws<KeyNotFoundException>(() =>
            reg.SetWorkspace(p.Id, "ws-does-not-exist", workspaces));
    }

    [Fact]
    public void SetArchived_TogglesFlag()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Demo", DefaultWorkspace.Id);
        Assert.False(p.Archived);
        Assert.True(reg.SetArchived(p.Id, true).Archived);
        Assert.False(reg.SetArchived(p.Id, false).Archived);
    }

    [Fact]
    public void Mutations_UnknownId_Throws()
    {
        var reg = Build();
        Assert.Throws<KeyNotFoundException>(() => reg.Rename("PROJ-999", "x"));
        Assert.Throws<KeyNotFoundException>(() => reg.SetColor("PROJ-999", "#fff"));
        Assert.Throws<KeyNotFoundException>(() => reg.SetArchived("PROJ-999", true));
    }

    [Fact]
    public void FindByIdOrDisplayName_AcceptsBothForms()
    {
        var reg = Build();
        var p = reg.EnsureProjectForStorage(Path.Combine(_root, "p1"), "Runbook", DefaultWorkspace.Id);
        Assert.Equal(p.Id, reg.FindByIdOrDisplayName("PROJ-001")?.Id);
        Assert.Equal(p.Id, reg.FindByIdOrDisplayName("Runbook")?.Id);
        Assert.Equal(p.Id, reg.FindByIdOrDisplayName("runbook")?.Id);
        Assert.Null(reg.FindByIdOrDisplayName("does-not-exist"));
    }
}
