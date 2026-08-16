using AgentStudio.DemoReplay;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the replay admission decision. The request carries only a
/// cursor, so this policy is the entire authority the <c>demo.replay</c> scope
/// can exercise: what it refuses here cannot be reached any other way.
/// </summary>
public class DemoReplayAdmissionPolicyTests
{
    private const string TraceId = "demo-instanz-cycle-1";
    private const string Digest = "c29d71cc6e748e50e19ce33e6dc9f5fa412e90e7e78a5455141883e945feca38";

    private static readonly DateTimeOffset Now = new(2026, 8, 17, 9, 0, 0, TimeSpan.Zero);

    private static DemoReplayAdmissionLimits Limits(int events = 20, int minEpochSeconds = 360, int maxPerWindow = 10)
        => new(TraceId, Digest, events, minEpochSeconds, maxPerWindow);

    private static DemoReplayAdmissionState State(
        long epoch = 1,
        int lastSequence = 4,
        int acceptedInWindow = 0,
        double epochAgeSeconds = 400)
        => new(epoch, lastSequence, Now.AddSeconds(-epochAgeSeconds), acceptedInWindow);

    private static DemoReplayEventRequest Request(long epoch = 1, int sequence = 5)
        => new(TraceId, Digest, epoch, sequence);

    private static DemoReplayAdmission Decide(
        DemoReplayEventRequest request,
        DemoReplayAdmissionState? state = null,
        DemoReplayAdmissionLimits? limits = null,
        bool enabled = true)
        => DemoReplayAdmissionPolicy.Decide(enabled, request, state ?? State(), limits ?? Limits(), Now);

    [Fact]
    public void The_next_step_of_the_current_epoch_is_admitted()
    {
        var admission = Decide(Request());

        Assert.True(admission.Accepted);
        Assert.Null(admission.Reason);
        Assert.Equal(1, admission.Epoch);
        Assert.Equal(5, admission.Sequence);
    }

    [Fact]
    public void A_disabled_plane_admits_nothing()
        => Assert.Equal(DemoReplayDenials.Disabled, Decide(Request(), enabled: false).Reason);

    [Fact]
    public void A_request_naming_another_trace_is_refused()
        => Assert.Equal(
            DemoReplayDenials.TraceUnknown,
            Decide(Request() with { TraceId = "some-other-trace" }).Reason);

    /// <summary>
    /// The digest is what the release manifest pins. A replay host running a
    /// different bundle cannot feed the current server.
    /// </summary>
    [Fact]
    public void A_request_carrying_a_different_trace_digest_is_refused()
        => Assert.Equal(
            DemoReplayDenials.DigestMismatch,
            Decide(Request() with { TraceDigest = new string('b', 64) }).Reason);

    [Fact]
    public void Replaying_an_earlier_epoch_is_refused()
        => Assert.Equal(DemoReplayDenials.EpochStale, Decide(Request(epoch: 0)).Reason);

    [Fact]
    public void Jumping_more_than_one_epoch_ahead_is_refused()
        => Assert.Equal(DemoReplayDenials.EpochSkipped, Decide(Request(epoch: 3, sequence: 1)).Reason);

    /// <summary>Flipping the epoch early is how a compromised identity would buy extra throughput.</summary>
    [Fact]
    public void Opening_the_next_epoch_before_the_minimum_interval_is_refused()
        => Assert.Equal(
            DemoReplayDenials.EpochTooSoon,
            Decide(Request(epoch: 2, sequence: 1), State(epochAgeSeconds: 10)).Reason);

    [Fact]
    public void Opening_the_next_epoch_after_the_minimum_interval_is_admitted()
    {
        var admission = Decide(Request(epoch: 2, sequence: 1), State(epochAgeSeconds: 400));

        Assert.True(admission.Accepted);
        Assert.Equal(2, admission.Epoch);
    }

    /// <summary>A fresh process has never replayed, so the first cycle must not wait out an interval it never started.</summary>
    [Fact]
    public void The_very_first_epoch_starts_immediately()
    {
        var admission = Decide(
            Request(epoch: 1, sequence: 1),
            State(epoch: 0, lastSequence: 0, epochAgeSeconds: 1));

        Assert.True(admission.Accepted);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void A_step_that_is_not_the_next_one_is_refused(int sequence)
        => Assert.Equal(DemoReplayDenials.SequenceOutOfOrder, Decide(Request(sequence: sequence)).Reason);

    [Fact]
    public void A_new_epoch_must_restart_at_the_first_step()
        => Assert.Equal(
            DemoReplayDenials.SequenceOutOfOrder,
            Decide(Request(epoch: 2, sequence: 5)).Reason);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(21)]
    public void A_step_outside_the_trace_is_refused(int sequence)
        => Assert.Equal(
            DemoReplayDenials.SequenceOutOfRange,
            Decide(Request(sequence: sequence), State(lastSequence: sequence - 1)).Reason);

    /// <summary>Ingestion stays a bounded surface even when every other check passes.</summary>
    [Fact]
    public void A_spent_rate_window_refuses_the_next_step()
        => Assert.Equal(
            DemoReplayDenials.RateLimited,
            Decide(Request(), State(acceptedInWindow: 10)).Reason);

    [Fact]
    public void The_rate_window_admits_the_last_budgeted_step()
        => Assert.True(Decide(Request(), State(acceptedInWindow: 9)).Accepted);
}
