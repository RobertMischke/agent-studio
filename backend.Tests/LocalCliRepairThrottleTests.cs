using AgentStudio.HostHealth;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The rate limit that keeps an automatic repair from becoming an install
/// loop on a host that stays broken.
/// </summary>
public class LocalCliRepairThrottleTests
{
    private static readonly DateTime Now = new(2026, 8, 18, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    [Fact]
    public void First_attempt_is_always_allowed()
    {
        var decision = LocalCliRepairThrottle.Decide(null, Now, Window);

        Assert.True(decision.Allowed);
        Assert.Equal(TimeSpan.Zero, decision.RetryAfter);
    }

    [Fact]
    public void A_second_attempt_inside_the_window_is_refused_with_a_retry_hint()
    {
        var decision = LocalCliRepairThrottle.Decide(Now.AddMinutes(-13), Now, Window);

        Assert.False(decision.Allowed);
        Assert.Equal(TimeSpan.FromMinutes(47), decision.RetryAfter);
        Assert.Contains("47m", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_attempt_exactly_one_window_later_is_allowed()
    {
        var decision = LocalCliRepairThrottle.Decide(Now - Window, Now, Window);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public void An_operator_request_bypasses_the_window()
    {
        var decision = LocalCliRepairThrottle.Decide(Now.AddMinutes(-1), Now, Window, operatorRequested: true);

        Assert.True(decision.Allowed);
        Assert.Contains("operator-requested", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_last_attempt_stamped_in_the_future_waits_out_a_full_window()
    {
        // Host clock moved backwards (time sync, suspend/resume). Without this
        // branch the negative elapsed time would read as "window expired" and
        // reinstall on every tick.
        var decision = LocalCliRepairThrottle.Decide(Now.AddHours(3), Now, Window);

        Assert.False(decision.Allowed);
        Assert.Equal(Window, decision.RetryAfter);
    }

    [Fact]
    public void A_zero_window_disables_the_rate_limit()
    {
        var decision = LocalCliRepairThrottle.Decide(Now.AddSeconds(-1), Now, TimeSpan.Zero);

        Assert.True(decision.Allowed);
    }
}
