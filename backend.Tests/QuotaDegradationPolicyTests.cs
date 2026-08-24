using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2679. Direct matrix over the pure "probe failed - what does the operator
/// see?" decision. The operator-visible bug was a quota display that replaced its
/// numbers with the raw text "A task was canceled."; these pin both halves of the
/// fix: keep the last-good numbers, and never surface a stock .NET exception
/// message as an operator-facing sentence.
/// </summary>
public class QuotaDegradationPolicyTests
{
    private static readonly DateTime GoodAt = new(2026, 8, 23, 20, 57, 0, DateTimeKind.Utc);
    private static readonly DateTime FailAt = new(2026, 8, 23, 21, 7, 0, DateTimeKind.Utc);

    private static QuotaSnapshot LastGood() => new()
    {
        CliType = "codex",
        FetchedAt = GoodAt,
        Plan = "Pro",
        CliVersion = "codex-cli 0.148.0",
        Windows =
        [
            new QuotaWindow { Label = "5-hour", UsedPct = 42, Unit = "%" },
            new QuotaWindow { Label = "Weekly", UsedPct = 71, Unit = "%" }
        ]
    };

    [Fact]
    public void Degrade_KeepsLastGoodWindowsAndMarksThemStale()
    {
        var result = QuotaDegradationPolicy.Degrade(
            LastGood(), "codex", "probe timed out", "codex-cli 0.149.0", FailAt);

        // The numbers survive - this is the whole point of graceful degradation.
        Assert.Equal(2, result.Windows.Count);
        Assert.Equal(42, result.Windows[0].UsedPct);
        Assert.Equal("Pro", result.Plan);

        // ...but they are explicitly marked as carried over, and dated.
        Assert.True(result.Stale);
        Assert.Equal(GoodAt, result.LastGoodAt);
        Assert.Equal(FailAt, result.FetchedAt);
        Assert.Equal("probe timed out", result.Error);

        // The version recorded is the one the FAILING probe saw, so drift is attributable.
        Assert.Equal("codex-cli 0.149.0", result.CliVersion);
    }

    [Fact]
    public void Degrade_RepeatedFailures_KeepPointingAtTheOriginalMeasurement()
    {
        var first = QuotaDegradationPolicy.Degrade(
            LastGood(), "codex", "probe timed out", "codex-cli 0.149.0", FailAt);
        var second = QuotaDegradationPolicy.Degrade(
            first, "codex", "probe timed out again", "codex-cli 0.149.0", FailAt.AddMinutes(10));

        // LastGoodAt must not creep forward to the previous FAILURE time, otherwise
        // the staleness marker under-reports how old the numbers really are.
        Assert.Equal(GoodAt, second.LastGoodAt);
        Assert.True(second.Stale);
        Assert.Equal(42, second.Windows[0].UsedPct);
    }

    [Fact]
    public void Degrade_WithNoPriorSnapshot_ReportsPlainFailureNotStale()
    {
        var result = QuotaDegradationPolicy.Degrade(
            null, "codex", "probe timed out", "codex-cli 0.149.0", FailAt);

        // Nothing was ever measured, so "stale" would be a lie.
        Assert.False(result.Stale);
        Assert.Null(result.LastGoodAt);
        Assert.Empty(result.Windows);
        Assert.Equal("probe timed out", result.Error);
        Assert.Equal("codex", result.CliType);
    }

    [Fact]
    public void Degrade_WithPriorErrorSnapshotThatHadNoWindows_DoesNotClaimStaleData()
    {
        var priorFailure = new QuotaSnapshot { CliType = "codex", FetchedAt = GoodAt, Error = "earlier failure" };

        var result = QuotaDegradationPolicy.Degrade(
            priorFailure, "codex", "probe timed out", "codex-cli 0.149.0", FailAt);

        Assert.False(result.Stale);
        Assert.Empty(result.Windows);
    }

    // AGT-2064 interlock: a failure must not drop a suspicious flag, or the
    // admission gate would re-open on a CLI that is really at its limit.
    [Fact]
    public void Degrade_LatchesPriorSuspiciousFlag_WhenThereAreNoWindowsToCarry()
    {
        var prior = new QuotaSnapshot
        {
            CliType = "codex",
            FetchedAt = GoodAt,
            Suspicious = true,
            SuspiciousReason = "launch hit a usage limit"
        };

        var result = QuotaDegradationPolicy.Degrade(prior, "codex", "probe timed out", null, FailAt);

        Assert.True(result.Suspicious);
        Assert.Equal("launch hit a usage limit", result.SuspiciousReason);
    }

    [Fact]
    public void Degrade_CarriesSuspiciousFlagAlongWithStaleWindows()
    {
        var prior = LastGood() with { Suspicious = true, SuspiciousReason = "unexplained drop" };

        var result = QuotaDegradationPolicy.Degrade(prior, "codex", "probe timed out", null, FailAt);

        Assert.True(result.Stale);
        Assert.True(result.Suspicious);
        Assert.Equal("unexplained drop", result.SuspiciousReason);
    }

    [Fact]
    public void Degrade_FallsBackToThePreviousVersion_WhenTheFailedProbeNeverLearnedOne()
    {
        // TestCliPath() failing means we never saw a --version string on this run;
        // the last known one is still the best attribution available.
        var result = QuotaDegradationPolicy.Degrade(LastGood(), "codex", "boom", null, FailAt);

        Assert.Equal("codex-cli 0.148.0", result.CliVersion);
    }

    /// <summary>
    /// The operator-facing symptom of AGT-2679, verbatim: a cancelled probe
    /// surfaced <see cref="TaskCanceledException"/>'s stock message. That string
    /// tells the operator nothing, so it must never reach the UI.
    /// </summary>
    [Fact]
    public void DescribeFailure_TurnsCancellationIntoAnOperatorFacingSentence()
    {
        var message = QuotaDegradationPolicy.DescribeFailure(
            new TaskCanceledException(), "codex", "codex-cli 0.149.0");

        Assert.DoesNotContain("A task was canceled", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("timed out", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("codex-cli 0.149.0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_KeepsTheDetailOfNonCancellationFailures()
    {
        var message = QuotaDegradationPolicy.DescribeFailure(
            new InvalidOperationException("codex CLI not available"), "codex", "codex-cli 0.149.0");

        Assert.Contains("codex CLI not available", message, StringComparison.Ordinal);
        Assert.Contains("codex-cli 0.149.0", message, StringComparison.Ordinal);
    }

    [Fact]
    public void DescribeFailure_NamesTheCli_WhenTheVersionIsUnknown()
    {
        var message = QuotaDegradationPolicy.DescribeFailure(new TaskCanceledException(), "codex", null);

        Assert.Contains("codex", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("A task was canceled", message, StringComparison.OrdinalIgnoreCase);
    }
}
