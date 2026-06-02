using System;
using System.Collections.Generic;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// ADR-0052 slice 2: the pure pick-gate that turns the sequential runner's
/// single <c>_activeJobId</c> latch into N bounded slots. Covers slot
/// accounting, the exclusive rule, and the scope-overlap "no cross-talk" gate.
/// </summary>
public sealed class ParallelSlotPolicyTests
{
    private static TaskParallelism Scope(params string[] paths) => new(false, paths);
    private static TaskParallelism Exclusive() => new(true, Array.Empty<string>());

    private static RunningTask Running(string id, params string[] paths) => new(id, Scope(paths));

    [Fact]
    public void MaxOne_AdmitsFirst_ThenSerializes_LikeSequentialRunner()
    {
        // Empty project, max=1: the one candidate is admitted.
        var first = ParallelSlotPolicy.Decide("a", Scope("backend/"), Array.Empty<RunningTask>(), maxParallelism: 1);
        Assert.Equal(SlotDecision.Admit, first.Decision);

        // One running, max=1: every further candidate waits - today's behaviour.
        var running = new[] { Running("a", "backend/") };
        var second = ParallelSlotPolicy.Decide("b", Scope("frontend/"), running, maxParallelism: 1);
        Assert.Equal(SlotDecision.Serialize, second.Decision);
        Assert.Contains("no free slot", second.Reason);
    }

    [Fact]
    public void MaxTwo_DisjointScopes_AdmitInParallel()
    {
        var running = new[] { Running("a", "frontend/") };
        var admission = ParallelSlotPolicy.Decide("b", Scope("backend/Services/Drift/"), running, maxParallelism: 2);

        Assert.Equal(SlotDecision.Admit, admission.Decision);
        Assert.Contains("parallel-ok", admission.Reason);
    }

    [Fact]
    public void MaxTwo_OverlappingScopes_Serialize_NoCrossTalk()
    {
        var running = new[] { Running("a", "backend/Services/") };
        var admission = ParallelSlotPolicy.Decide("b", Scope("backend/Services/Runner/ProjectRunner.cs"), running, maxParallelism: 2);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("scope conflict with 'a'", admission.Reason);
    }

    [Fact]
    public void NoFreeSlot_Serializes_EvenWhenDisjoint()
    {
        var running = new[] { Running("a", "frontend/"), Running("b", "backend/") };
        var admission = ParallelSlotPolicy.Decide("c", Scope("docs/"), running, maxParallelism: 2);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("2/2 occupied", admission.Reason);
    }

    [Fact]
    public void ExclusiveCandidate_RunsAlone_WhenIdle()
    {
        var admission = ParallelSlotPolicy.Decide("big", Exclusive(), Array.Empty<RunningTask>(), maxParallelism: 4);

        Assert.Equal(SlotDecision.RunExclusive, admission.Decision);
        Assert.Contains("runs alone", admission.Reason);
    }

    [Fact]
    public void ExclusiveCandidate_WaitsForDrain_WhenSomethingRuns()
    {
        var running = new[] { Running("a", "frontend/") };
        var admission = ParallelSlotPolicy.Decide("big", Exclusive(), running, maxParallelism: 4);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("waits", admission.Reason);
    }

    [Fact]
    public void RunningExclusive_BlocksEveryOtherCandidate()
    {
        var running = new[] { new RunningTask("big", new TaskParallelism(true, Array.Empty<string>())) };
        var admission = ParallelSlotPolicy.Decide("b", Scope("frontend/"), running, maxParallelism: 4);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("exclusive task 'big' is running", admission.Reason);
    }

    [Fact]
    public void UnknownScope_IsConservative_Serializes()
    {
        // Candidate with no predicted scope cannot be proven disjoint from a
        // running task, so the gate holds it rather than risk a collision.
        var running = new[] { Running("a", "frontend/") };
        var admission = ParallelSlotPolicy.Decide("b", TaskParallelism.Default, running, maxParallelism: 2);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("unknown-scope", admission.Reason);
    }

    [Theory]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(3, 1, 2)]
    [InlineData(3, 3, 0)]
    [InlineData(0, 0, 1)] // clamp: max < 1 behaves as 1
    [InlineData(2, 5, 0)] // never negative
    public void FreeSlots_ClampsAndFloorsAtZero(int max, int occupied, int expected)
    {
        Assert.Equal(expected, ParallelSlotPolicy.FreeSlots(max, occupied));
    }

    [Fact]
    public void ScopeConflict_IsPathPrefixBased_BackslashAndCaseInsensitive()
    {
        Assert.Null(ParallelSlotPolicy.FirstScopeConflict(new[] { "frontend/" }, new[] { "backend/" }));
        Assert.NotNull(ParallelSlotPolicy.FirstScopeConflict(new[] { "Backend\\Services" }, new[] { "backend/services/x.cs" }));
    }
}
