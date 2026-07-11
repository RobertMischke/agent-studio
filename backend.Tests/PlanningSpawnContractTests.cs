using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2069 — the planning spawn-contract completion gate, the no-follow-up
/// declaration store, and the read-time spawn-visibility projection. These are
/// the guard against the AGT-1915 trap: a planning task accepted plan-only with
/// no follow-up work ever created.
/// </summary>
public class PlanningSpawnContractTests : IDisposable
{
    private readonly string _jobFolder;

    public PlanningSpawnContractTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "agt2069-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    // ---- gate logic -----------------------------------------------------

    [Theory]
    [InlineData("planning", true)]
    [InlineData("Planning", true)]
    [InlineData("coding", false)]
    [InlineData("research", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Applies_OnlyToPlanningMode(string? mode, bool expected)
        => Assert.Equal(expected, PlanningCompletionGate.Applies(mode));

    [Theory]
    [InlineData(0, false, false)] // no spawn, no declaration -> not satisfied (the 1915 trap)
    [InlineData(1, false, true)]  // spawned a follow-up -> satisfied
    [InlineData(3, false, true)]
    [InlineData(0, true, true)]   // deliberate no-follow-up declaration -> satisfied
    [InlineData(2, true, true)]
    public void IsSatisfied_RequiresSpawnOrDeclaration(int spawnedCount, bool declared, bool expected)
        => Assert.Equal(expected, PlanningCompletionGate.IsSatisfied(spawnedCount, declared));

    [Fact]
    public void ShouldWarnOnAccept_OnlyWhenPlanningAndUnsatisfied()
    {
        Assert.True(PlanningCompletionGate.ShouldWarnOnAccept("planning", spawnedCount: 0, noFollowUpDeclared: false));
        Assert.False(PlanningCompletionGate.ShouldWarnOnAccept("planning", spawnedCount: 1, noFollowUpDeclared: false));
        Assert.False(PlanningCompletionGate.ShouldWarnOnAccept("planning", spawnedCount: 0, noFollowUpDeclared: true));
        // A coding task is never gated, even with zero spawns.
        Assert.False(PlanningCompletionGate.ShouldWarnOnAccept("coding", spawnedCount: 0, noFollowUpDeclared: false));
    }

    [Fact]
    public void Summary_ContractSatisfied_FollowsGate()
    {
        Assert.False(new PlanningSpawnSummary().ContractSatisfied);
        Assert.True(new PlanningSpawnSummary
        {
            Spawned = new[] { new PlanningSpawnRef { TargetKey = "WEB-1" } },
        }.ContractSatisfied);
        Assert.True(new PlanningSpawnSummary { NoFollowUpDeclared = true }.ContractSatisfied);
    }

    // ---- declaration store ---------------------------------------------

    [Fact]
    public void ClosureStore_MissingFile_ReadsAsNull()
        => Assert.Null(PlanningClosureStore.Read(_jobFolder));

    [Fact]
    public void ClosureStore_WriteThenRead_Roundtrips()
    {
        Assert.True(PlanningClosureStore.Write(_jobFolder, declared: true, reason: "Concept only; no code intended", declaredBy: "robert"));

        var record = PlanningClosureStore.Read(_jobFolder);
        Assert.NotNull(record);
        Assert.True(record!.NoFollowUpDeclared);
        Assert.Equal("Concept only; no code intended", record.Reason);
        Assert.Equal("robert", record.DeclaredBy);
        Assert.NotNull(record.DeclaredAt);
    }

    [Fact]
    public void ClosureStore_ClearRemovesDeclaration()
    {
        PlanningClosureStore.Write(_jobFolder, declared: true, reason: "x", declaredBy: null);
        Assert.NotNull(PlanningClosureStore.Read(_jobFolder));

        Assert.True(PlanningClosureStore.Write(_jobFolder, declared: false, reason: null, declaredBy: null));
        Assert.Null(PlanningClosureStore.Read(_jobFolder));
    }

    // ---- read-time projection (WithRuntime helper) ---------------------

    [Fact]
    public void BuildSummary_NonPlanning_IsNull()
    {
        var coding = new TaskInfo { Id = "j1", Mode = TaskModes.Coding, FolderPath = _jobFolder };
        Assert.Null(TaskEndpointHelpers.BuildPlanningSpawnSummary(coding));
    }

    [Fact]
    public void BuildSummary_PlanningWithNoSidecars_IsEmptyUnsatisfied()
    {
        var planning = new TaskInfo { Id = "j1", Mode = TaskModes.Planning, FolderPath = _jobFolder };
        var summary = TaskEndpointHelpers.BuildPlanningSpawnSummary(planning);
        Assert.NotNull(summary);
        Assert.Equal(0, summary!.SpawnedCount);
        Assert.False(summary.NoFollowUpDeclared);
        Assert.False(summary.ContractSatisfied);
    }

    [Fact]
    public void BuildSummary_PlanningWithSpawnLedger_SurfacesFollowUps()
    {
        SpawnedTaskLedger.Append(_jobFolder, new SpawnedTaskRecord
        {
            At = DateTime.UtcNow,
            SourceKey = "AGT-2069",
            TargetProject = "web",
            TargetKey = "WEB-42",
            TargetJobId = "web-42-slug",
            Reason = "documented the new endpoint",
        });

        var planning = new TaskInfo { Id = "j1", Mode = TaskModes.Planning, FolderPath = _jobFolder };
        var summary = TaskEndpointHelpers.BuildPlanningSpawnSummary(planning);

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.SpawnedCount);
        Assert.Equal("WEB-42", summary.Spawned[0].TargetKey);
        Assert.True(summary.ContractSatisfied);
    }

    [Fact]
    public void BuildSummary_PlanningWithDeclaration_IsSatisfiedWithoutSpawn()
    {
        PlanningClosureStore.Write(_jobFolder, declared: true, reason: "Intentional plan-only", declaredBy: null);

        var planning = new TaskInfo { Id = "j1", Mode = TaskModes.Planning, FolderPath = _jobFolder };
        var summary = TaskEndpointHelpers.BuildPlanningSpawnSummary(planning);

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.SpawnedCount);
        Assert.True(summary.NoFollowUpDeclared);
        Assert.Equal("Intentional plan-only", summary.NoFollowUpReason);
        Assert.True(summary.ContractSatisfied);
    }
}
