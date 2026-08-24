using AgentStudio.Cli;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct policy-matrix test for <see cref="CliRepairCooldownPolicy"/> - no
/// filesystem, process, clock, or DI setup, per the repo's "pure policy
/// first" convention (docs/quality/dotnet-backend.md). The stateful
/// check-then-act around this decision is covered separately in
/// <c>CliRepairGateTests</c>.
/// </summary>
public sealed class CliRepairCooldownPolicyTests
{
    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);

    [Fact]
    public void NoPriorAttempt_Allowed()
    {
        var decision = CliRepairCooldownPolicy.Decide(lastAttemptUtc: null, DateTime.UtcNow, OneHour);
        Assert.Equal(CliRepairCooldownDecision.Allowed, decision);
    }

    [Fact]
    public void WellWithinWindow_OnCooldown()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddMinutes(-30);
        Assert.Equal(CliRepairCooldownDecision.OnCooldown, CliRepairCooldownPolicy.Decide(last, now, OneHour));
    }

    [Fact]
    public void OneSecondAgo_OnCooldown()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddSeconds(-1);
        Assert.Equal(CliRepairCooldownDecision.OnCooldown, CliRepairCooldownPolicy.Decide(last, now, OneHour));
    }

    [Fact]
    public void ExactlyAtWindowBoundary_Allowed()
    {
        // now - last == window exactly: the boundary belongs to "allowed"
        // (strict less-than for the cooldown branch), so a caller polling
        // right at the hour mark is not starved by a rounding edge.
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddHours(-1);
        Assert.Equal(CliRepairCooldownDecision.Allowed, CliRepairCooldownPolicy.Decide(last, now, OneHour));
    }

    [Fact]
    public void JustOverWindow_Allowed()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddHours(-1).AddSeconds(-1);
        Assert.Equal(CliRepairCooldownDecision.Allowed, CliRepairCooldownPolicy.Decide(last, now, OneHour));
    }

    [Fact]
    public void JustUnderWindow_OnCooldown()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var last = now.AddHours(-1).AddSeconds(1);
        Assert.Equal(CliRepairCooldownDecision.OnCooldown, CliRepairCooldownPolicy.Decide(last, now, OneHour));
    }

    [Fact]
    public void DefaultWindow_IsOneHour()
    {
        Assert.Equal(TimeSpan.FromHours(1), CliRepairCooldownPolicy.DefaultWindow);
    }
}
