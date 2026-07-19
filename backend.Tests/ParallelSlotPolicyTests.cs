using System;
using System.Collections.Generic;

using Xunit;

namespace AgentStudio.Tests;

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

    public static TheoryData<string, string, string[]> NightUnknownScopeFixtures => new()
    {
        {
            "memory---dev-wiki-migrieren",
            "docs/wiki/",
            new[] { "pulse-1", "pulse-2", "remote-hosts", "orch-1", "task-server", "screen-tooling", "cli-probes", "runner-health" }
        },
        {
            "run-liveness-c-subzustaende",
            "backend/Features/Runner/",
            new[] { "pulse-2", "remote-hosts", "orch-1", "task-server", "screen-tooling" }
        },
    };

    [Theory]
    [MemberData(nameof(NightUnknownScopeFixtures))]
    public void NightRegression_UnknownScopeCandidates_AdmitAlongsideScopedRun(
        string runningId,
        string runningScope,
        string[] candidateIds)
    {
        var running = new[] { Running(runningId, runningScope) };

        foreach (var candidateId in candidateIds)
        {
            var admission = ParallelSlotPolicy.Decide(
                candidateId, TaskParallelism.Default, running, maxParallelism: 6);

            Assert.Equal(SlotDecision.Admit, admission.Decision);
            Assert.Contains("optimistic: unknown scope, worktree-isolated", admission.Reason);
        }
    }

    [Fact]
    public void UnknownRunningScope_AdmitsScopedCandidate_Symmetrically()
    {
        var running = new[] { Running("unknown-running") };
        var admission = ParallelSlotPolicy.Decide(
            "scoped-candidate", Scope("frontend/"), running, maxParallelism: 2);

        Assert.Equal(SlotDecision.Admit, admission.Decision);
        Assert.Contains("optimistic: unknown scope, worktree-isolated", admission.Reason);
    }

    [Fact]
    public void UnknownRunningScope_DoesNotHideLaterDeclaredConflict()
    {
        var running = new[]
        {
            Running("unknown-running"),
            Running("declared-running", "backend/Services/"),
        };
        var admission = ParallelSlotPolicy.Decide(
            "candidate", Scope("backend/Services/Runner/"), running, maxParallelism: 4);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("scope conflict with 'declared-running'", admission.Reason);
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

    [Fact]
    public void ReadOnlyCandidate_AdmitsAlongsideOverlappingScope_NoScopeComputation()
    {
        // A planning / research run writes no files, so it has no scope to prove
        // disjoint. The gate admits it as parallel-ok even when a running task's
        // scope would have collided for a coding candidate - the scope loop is
        // skipped entirely.
        var running = new[] { Running("a", "backend/Services/") };
        var admission = ParallelSlotPolicy.Decide(
            "ro", TaskParallelism.ReadOnlyTask, running, maxParallelism: 2);

        Assert.Equal(SlotDecision.Admit, admission.Decision);
        Assert.Contains("read-only task (no file scope)", admission.Reason);
    }

    [Fact]
    public void ReadOnlyCandidate_WithUnknownScope_StillAdmits()
    {
        // A read-only candidate still uses its stronger short-circuit rationale:
        // it never writes, so no worktree-based scope fallback is needed.
        var running = new[] { Running("a", "frontend/") };
        var admission = ParallelSlotPolicy.Decide(
            "ro", TaskParallelism.ReadOnlyTask, running, maxParallelism: 2);

        Assert.Equal(SlotDecision.Admit, admission.Decision);
        Assert.Contains("read-only task (no file scope)", admission.Reason);
    }

    [Fact]
    public void ReadOnlyCandidate_StillCountsAgainstQuota_SerializesWhenNoFreeSlot()
    {
        // Slot / quota are unchanged: a read-only run still consumes a slot, so
        // when none is free it waits like any other candidate (it does not get a
        // free pass past the budget).
        var running = new[] { Running("a", "frontend/") };
        var admission = ParallelSlotPolicy.Decide(
            "ro", TaskParallelism.ReadOnlyTask, running, maxParallelism: 1);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("no free slot", admission.Reason);
    }

    [Fact]
    public void ReadOnlyCandidate_WaitsBehindRunningExclusive()
    {
        // A running exclusive task is a hard "run alone" guarantee that even a
        // read-only candidate respects (the exclusive-running check precedes the
        // read-only short-circuit).
        var running = new[] { new RunningTask("big", Exclusive()) };
        var admission = ParallelSlotPolicy.Decide(
            "ro", TaskParallelism.ReadOnlyTask, running, maxParallelism: 4);

        Assert.Equal(SlotDecision.Serialize, admission.Decision);
        Assert.Contains("exclusive task 'big' is running", admission.Reason);
    }

    [Fact]
    public void ReadOnlyTask_Factory_IsNotExclusive_AndHasNoScope()
    {
        Assert.True(TaskParallelism.ReadOnlyTask.ReadOnly);
        Assert.False(TaskParallelism.ReadOnlyTask.Exclusive);
        Assert.Empty(TaskParallelism.ReadOnlyTask.PredictedScope);
    }
}
