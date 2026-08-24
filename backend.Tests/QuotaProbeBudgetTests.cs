using System.Text.RegularExpressions;
using Xunit;

using ProbeStep = AgentStudio.Cli.QuotaProbeBase.ProbeStep;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2679 root-cause guard. The operator saw
/// "TaskCanceledException at PtySession.WaitForPatternAsync" - a cancellation,
/// not a parse failure. The cause was arithmetic: <c>QuotaService</c> imposed a
/// flat 45 s deadline while the probes' own step timeouts could sum to far more
/// (codex ~57 s, claude ~68 s). Whenever enough pattern waits ran to timeout -
/// which is exactly what a CLI that changed its startup screen causes - the
/// probe was killed mid-step and reported a bare cancellation.
///
/// These tests pin the invariant that makes that impossible: every probe's
/// declared budget covers its own worst case.
/// </summary>
public class QuotaProbeBudgetTests
{
    private static readonly Regex AnyPattern = new("x");

    [Fact]
    public void WorstCase_SumsPatternWaitsPreSendDelaysAndSettleTimeouts()
    {
        ProbeStep[] steps =
        [
            new ProbeStep("wait-and-send", WaitForPattern: AnyPattern, WaitTimeoutMs: 10000,
                          PreSendDelayMs: 300, SendKeys: "<Enter>", SettleTimeoutMs: 6000)
        ];

        Assert.Equal(8000 + 10000 + 300 + 6000, AgentStudio.Cli.QuotaProbeBase.WorstCaseDurationMs(steps, 8000));
    }

    [Fact]
    public void WorstCase_IgnoresSettleTimeoutForStepsThatSendNothing()
    {
        // A pure "wait for the panel to render" step never sends keys, so it
        // cannot pay a post-send settle. Counting one would inflate every budget.
        ProbeStep[] steps =
        [
            new ProbeStep("await-only", WaitForPattern: AnyPattern, WaitTimeoutMs: 10000, SettleTimeoutMs: 6000)
        ];

        Assert.Equal(10000, AgentStudio.Cli.QuotaProbeBase.WorstCaseDurationMs(steps, 0));
    }

    [Fact]
    public void WorstCase_IgnoresWaitTimeoutForStepsWithNoPattern()
    {
        ProbeStep[] steps =
        [
            new ProbeStep("send-only", WaitTimeoutMs: 10000, PreSendDelayMs: 500,
                          SendKeys: "<Enter>", SettleTimeoutMs: 8000)
        ];

        Assert.Equal(500 + 8000, AgentStudio.Cli.QuotaProbeBase.WorstCaseDurationMs(steps, 0));
    }

    [Fact]
    public void WorstCase_OfAnEmptySequenceIsJustTheInitialIdle()
    {
        Assert.Equal(8000, AgentStudio.Cli.QuotaProbeBase.WorstCaseDurationMs([], 8000));
    }

    /// <summary>
    /// The regression itself. Before the fix these probes declared no budget and
    /// the caller applied a flat 45 s; codex's step sequence alone can consume
    /// ~57 s. Assert each probe's budget really does cover its steps, and that it
    /// exceeds the old flat deadline - i.e. that the old deadline was genuinely
    /// too small and would have cancelled a legitimate slow run.
    /// </summary>
    [Fact]
    public void CodexProbeBudget_ExceedsTheOldFlatDeadline()
    {
        // 8.0 s initial idle + 16.3 s await-trust + 13.8 s await-welcome
        // + 8.5 s submit-status + 10.0 s await-status.
        Assert.Equal(56_600, AgentStudio.Cli.CodexQuotaProbe.StepBudgetMs);
        Assert.True(AgentStudio.Cli.CodexQuotaProbe.StepBudgetMs > 45_000,
            "the old flat 45 s deadline would cancel the codex probe mid-step");
    }

    [Fact]
    public void ClaudeProbeBudget_ExceedsTheOldFlatDeadline()
    {
        // 8.0 s initial idle + 10.0 + 8.5 + 8.5 + 7.5 s of startup-gate dismissals
        // + 18.0 s send-usage + 8.0 s await-usage.
        Assert.Equal(68_500, AgentStudio.Cli.ClaudeQuotaProbe.StepBudgetMs);
        Assert.True(AgentStudio.Cli.ClaudeQuotaProbe.StepBudgetMs > 45_000,
            "the old flat 45 s deadline would cancel the claude probe mid-step");
    }
}
