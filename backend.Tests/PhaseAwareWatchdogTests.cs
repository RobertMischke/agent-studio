using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the per-phase watchdog budget matrix. Pure-function library,
/// runs in &lt;1 s on every default <c>dotnet test</c>. The phase-aware
/// budgets are the load-bearing reason ADR-0013 was worth doing - the
/// original silence-only model treats all silences alike, this one
/// reports phase + budget so the user (and the orchestrator) see why.
/// </summary>
public class PhaseAwareWatchdogTests
{
    private static readonly WatchdogConfig Cfg = new(
        Enabled: true,
        WarmUpGraceSeconds: 0,
        QuietSeconds: 30,
        SuspiciousSeconds: 60,
        HungSeconds: 120,
        TickIntervalSeconds: 5);

    [Fact]
    public void Disabled_AlwaysHealthy()
    {
        var disabled = Cfg with { Enabled = false };
        Assert.Equal(WatchdogState.Healthy,
            PhaseAwareWatchdog.DecideState(silenceSeconds: 9999, runAgeSeconds: 9999,
                phase: RunPhase.OutputDelta, config: disabled));
    }

    [Fact]
    public void WarmUp_KeepsHealthy_RegardlessOfPhase()
    {
        var warmup = Cfg with { WarmUpGraceSeconds = 30 };
        Assert.Equal(WatchdogState.Healthy,
            PhaseAwareWatchdog.DecideState(silenceSeconds: 60, runAgeSeconds: 5,
                phase: RunPhase.PromptConsumed, config: warmup));
    }

    [Fact]
    public void Spawning_TightBudget_HungAt60s()
    {
        // PhaseBudget.For(Spawning) = (30s, 60s)
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(35, 100, RunPhase.Spawning, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.Spawning, Cfg));
    }

    [Fact]
    public void PromptConsumed_StaysQuietUntil60s_ThenSuspiciousThenHungAt180s()
    {
        // The "init then silence" hang sits here. Budgets (60s, 180s).
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(45, 100, RunPhase.PromptConsumed, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.PromptConsumed, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(185, 200, RunPhase.PromptConsumed, Cfg));
    }

    [Fact]
    public void ToolExecuting_TolerantBudget_StillHealthyAt100s()
    {
        // PhaseBudget.For(ToolExecuting) = (180s, 600s). A long Bash build
        // is normal and must not trigger a kill.
        Assert.Equal(WatchdogState.Quiet, // global Quiet at 30s still applies
            PhaseAwareWatchdog.DecideState(100, 200, RunPhase.ToolExecuting, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(200, 300, RunPhase.ToolExecuting, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(610, 700, RunPhase.ToolExecuting, Cfg));
    }

    [Fact]
    public void TerminalPhases_NeverEscalate()
    {
        foreach (var phase in new[] {
            RunPhase.TurnCompleted, RunPhase.TurnFailed,
            RunPhase.NeedsInput, RunPhase.Exited, RunPhase.Killed })
        {
            // Even at huge silence, terminal phases are healthy because the
            // runner is about to finalize. The watchdog should not double-
            // dispose them.
            Assert.Equal(WatchdogState.Quiet, // global Quiet still applies
                PhaseAwareWatchdog.DecideState(35, 100, phase, Cfg));
            Assert.Equal(WatchdogState.Quiet,
                PhaseAwareWatchdog.DecideState(9000, 10000, phase, Cfg));
        }
    }

    [Fact]
    public void Unknown_HasDefensiveButNotInfiniteBudget()
    {
        // PhaseBudget.For(Unknown) = (60s, 240s). Adapter could not
        // classify the CLI's output - we still want a kill eventually
        // so a buggy adapter does not pin the runner forever.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.Unknown, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(245, 300, RunPhase.Unknown, Cfg));
    }

    [Fact]
    public void FormatBudgetReason_ReadableAndIncludesPhasePlusBudget()
    {
        var msg = PhaseAwareWatchdog.FormatBudgetReason(RunPhase.PromptConsumed, 70);
        Assert.Contains("phase=PromptConsumed", msg);
        Assert.Contains("silence=70s", msg);
        Assert.Contains("allowed=60/180s", msg);
    }
}
