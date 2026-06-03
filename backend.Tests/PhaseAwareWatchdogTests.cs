using Microsoft.Extensions.Configuration;
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
    public void PromptConsumed_StaysQuietUntil120s_ThenSuspiciousThenHungAt420s()
    {
        // The "init then silence" hang sits here, but the same API
        // backpressure that stretches SessionInitializing stretches this
        // window too. The 2026-06 mass-false-positive survey saw kills
        // clustered at 183 s under the old (60s, 180s) budget; the budget
        // is now (120s, 420s). Locked so a regression cannot quietly
        // re-tighten it.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(45, 100, RunPhase.PromptConsumed, Cfg));
        // 65 s used to be Suspicious under the old 60 s threshold; now it is
        // still only Quiet.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.PromptConsumed, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(125, 200, RunPhase.PromptConsumed, Cfg));
        // 183 s - the reported false-positive kill point - is now merely
        // Suspicious, never Hung.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(183, 300, RunPhase.PromptConsumed, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(425, 500, RunPhase.PromptConsumed, Cfg));
    }

    [Fact]
    public void ToolExecuting_RealisticSilence_IsSuspiciousNotKilled()
    {
        // PhaseBudget.For(ToolExecuting) = (300s, 1200s) since the 2026-06
        // Codex-stability re-tune. The card established that ~600 s of
        // stdout silence during a single long tool/reasoning turn is
        // *realistic healthy work*, so the watchdog must NOT kill there
        // (symptom A). Lock that contract: 600 s is Suspicious (loud,
        // visible) but never Hung; a kill only fires past 1200 s.
        Assert.Equal(WatchdogState.Quiet, // global Quiet at 30s still applies
            PhaseAwareWatchdog.DecideState(100, 200, RunPhase.ToolExecuting, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(350, 400, RunPhase.ToolExecuting, Cfg));
        // The realistic-work boundary: visible, not killed.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(600, 700, RunPhase.ToolExecuting, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.ToolExecuting, Cfg));
    }

    private static IConfiguration ConfigFrom(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void PhaseBudgetTable_FromConfig_NoSection_UsesHardcodedDefaults()
    {
        var table = PhaseBudgetTable.FromConfig(ConfigFrom());
        var tool = table.For(RunPhase.ToolExecuting);
        Assert.Equal(300, tool.SuspiciousSeconds);
        Assert.Equal(1200, tool.HungSeconds);
    }

    [Fact]
    public void PhaseBudgetTable_FromConfig_OverridesPerPhaseAndPerField()
    {
        // Operator widens ToolExecuting Hung only; Suspicious must keep its
        // default. Phase keys are case-insensitive.
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:Phase:ToolExecuting:HungSeconds", "2000")));
        var tool = table.For(RunPhase.ToolExecuting);
        Assert.Equal(300, tool.SuspiciousSeconds);   // unchanged default
        Assert.Equal(2000, tool.HungSeconds);        // overridden

        // A phase with no override keeps its full default.
        var spawn = table.For(RunPhase.Spawning);
        Assert.Equal(30, spawn.SuspiciousSeconds);
        Assert.Equal(60, spawn.HungSeconds);
    }

    [Fact]
    public void PhaseBudgetTable_FromConfig_UnknownPhaseKey_Ignored()
    {
        // Forward-compatible: config for a phase this build doesn't know
        // must not throw, and known phases stay at defaults.
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:Phase:NotARealPhase:HungSeconds", "5")));
        Assert.Equal(1200, table.For(RunPhase.ToolExecuting).HungSeconds);
    }

    [Fact]
    public void DecideState_HonorsConfiguredBudgetOverride()
    {
        // With ToolExecuting widened to Hung=2000, the previously-fatal
        // 1250 s silence is now merely Suspicious; the kill moves to 2000 s.
        var table = PhaseBudgetTable.FromConfig(ConfigFrom(
            ("Watchdog:Phase:ToolExecuting:HungSeconds", "2000")));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(1250, 1400, RunPhase.ToolExecuting, Cfg, table));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(2050, 2200, RunPhase.ToolExecuting, Cfg, table));
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
        Assert.Contains("allowed=120/420s", msg);
    }

    [Fact]
    public void SessionInitializing_BudgetIs120And600()
    {
        // Pattern analysis 2026-05-06 originally moved this phase to
        // (60s, 120s) after kills clustered at 31-33s. The 2026-06
        // mass-false-positive survey then showed that budget *still* too
        // tight: healthy `claude -r` resumes of large sessions emit NO
        // stdout while they read+replay the session JSONL and contact the
        // API, and the watchdog was auto-cancelling them at 122s. Resume
        // of a big session can take *minutes*, so the kill threshold now
        // sits at 600s, well past the longest realistic init; the
        // stdout-independent session-file heartbeat is the primary
        // liveness signal here (see ClaudeSessionHeartbeat wiring). Locked
        // so a regression cannot quietly re-tighten it.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(45, 100, RunPhase.SessionInitializing, Cfg));
        // 65s used to be Suspicious under the old 60s threshold; now Quiet.
        Assert.Equal(WatchdogState.Quiet,
            PhaseAwareWatchdog.DecideState(65, 100, RunPhase.SessionInitializing, Cfg));
        // 122s - the reported false-positive kill point - is now merely
        // Suspicious, never Hung.
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(122, 383, RunPhase.SessionInitializing, Cfg));
        Assert.Equal(WatchdogState.Suspicious,
            PhaseAwareWatchdog.DecideState(125, 200, RunPhase.SessionInitializing, Cfg));
        Assert.Equal(WatchdogState.Hung,
            PhaseAwareWatchdog.DecideState(605, 700, RunPhase.SessionInitializing, Cfg));
    }
}
