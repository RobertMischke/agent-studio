using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the AGT-2064 plausibility gate: a window that jumps DOWN by more than
/// the threshold with no reset to explain it is suspicious, and a suspicious
/// reading is only trusted once a second, consistent probe confirms it.
/// </summary>
public sealed class QuotaPlausibilityGateTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 16, 5, 0, DateTimeKind.Utc);

    private static QuotaSnapshot Snap(string label, double usedPct, DateTime? resetAt = null) => new()
    {
        CliType = "codex",
        Windows = new() { new QuotaWindow { Label = label, UsedPct = usedPct, ResetAt = resetAt } }
    };

    [Fact]
    public void Evaluate_NoPrevious_IsTrusted()
    {
        var result = QuotaPlausibilityGate.Evaluate(null, Snap("5-hour", 4), Now);
        Assert.False(result.Suspicious);
    }

    [Fact]
    public void Evaluate_BigDropWithNoReset_IsSuspicious()
    {
        // The operator's exact shape: 5-hour reads ~100% used, next probe ~4%,
        // reset still an hour away -> not physically possible without a reset.
        var prev = Snap("5-hour", 100, resetAt: Now.AddHours(1));
        var cand = Snap("5-hour", 4, resetAt: Now.AddHours(1));

        var result = QuotaPlausibilityGate.Evaluate(prev, cand, Now);

        Assert.True(result.Suspicious);
        Assert.Contains("5-hour", result.Reason);
    }

    [Fact]
    public void Evaluate_SmallDrop_IsTrusted()
    {
        // A 49-point drop is under the 50-point threshold: normal consumption.
        var prev = Snap("Weekly", 60, resetAt: Now.AddDays(1));
        var cand = Snap("Weekly", 11, resetAt: Now.AddDays(1));

        Assert.False(QuotaPlausibilityGate.Evaluate(prev, cand, Now).Suspicious);
    }

    [Fact]
    public void Evaluate_BigDropButPreviousResetHasPassed_IsTrusted()
    {
        // The window's announced reset time is now in the past -> it rolled over,
        // so a big drop is expected, not suspicious.
        var prev = Snap("5-hour", 98, resetAt: Now.AddMinutes(-1));
        var cand = Snap("5-hour", 2, resetAt: Now.AddHours(5));

        Assert.False(QuotaPlausibilityGate.Evaluate(prev, cand, Now).Suspicious);
    }

    [Fact]
    public void Evaluate_BigDropWithLaterResetBoundary_IsTrusted()
    {
        // Candidate reports a later reset than the previous snapshot -> a fresh
        // cycle started, which legitimately explains the drop.
        var prev = Snap("Weekly", 90, resetAt: Now.AddHours(2));
        var cand = Snap("Weekly", 5, resetAt: Now.AddDays(7));

        Assert.False(QuotaPlausibilityGate.Evaluate(prev, cand, Now).Suspicious);
    }

    [Fact]
    public void Evaluate_WindowMissingInCandidate_IsTrusted()
    {
        // No counterpart window to compare against -> nothing to flag here (the
        // conservative handling of a vanished window lives in admission, not the
        // drop gate).
        var prev = Snap("5-hour", 100, resetAt: Now.AddHours(1));
        var cand = Snap("Weekly", 3, resetAt: Now.AddDays(1));

        Assert.False(QuotaPlausibilityGate.Evaluate(prev, cand, Now).Suspicious);
    }

    [Fact]
    public void AreConsistent_TwoAgreeingProbes_IsTrue()
    {
        Assert.True(QuotaPlausibilityGate.AreConsistent(Snap("5-hour", 4), Snap("5-hour", 6)));
    }

    [Fact]
    public void AreConsistent_ProbesDisagreeBeyondTolerance_IsFalse()
    {
        // The glitch case: one probe says 4%, the confirmation says 100%.
        Assert.False(QuotaPlausibilityGate.AreConsistent(Snap("5-hour", 4), Snap("5-hour", 100)));
    }

    [Fact]
    public void AreConsistent_NoSharedWindow_IsFalse()
    {
        Assert.False(QuotaPlausibilityGate.AreConsistent(Snap("5-hour", 4), Snap("Weekly", 4)));
    }
}
