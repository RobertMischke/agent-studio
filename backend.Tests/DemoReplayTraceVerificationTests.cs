using System.Text;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the pure trace verification. The replay plane's entire
/// content guarantee rests on this function, so every rejection branch is
/// exercised without a server, a file, or a clock.
/// </summary>
public class DemoReplayTraceVerificationTests
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("demo-replay-development-key");

    private static DemoReplayTrace Valid(Action<List<DemoReplayTraceEvent>>? mutate = null, int cycleSeconds = 720)
    {
        var events = new List<DemoReplayTraceEvent>
        {
            new() { Sequence = 1, OffsetMs = 0, TaskKey = "DEMO-4", Kind = "session.started", Severity = "info", Message = "Simulated run started." },
            new() { Sequence = 2, OffsetMs = 30_000, TaskKey = "DEMO-12", Kind = "turn.started", Severity = "info", Message = "Reading the failing case." },
            new() { Sequence = 3, OffsetMs = 90_000, TaskKey = "DEMO-4", Kind = "session.completed", Severity = "info", Message = "Simulated run finished.", DurationMs = 90_000, InputTokens = 1_200, OutputTokens = 340 },
        };
        mutate?.Invoke(events);

        var unsigned = new DemoReplayTrace
        {
            SchemaVersion = 1,
            TraceId = "demo-instanz-cycle-1",
            CycleSeconds = cycleSeconds,
            Scene = new DemoReplayScene { Projects = ["Demo App"], TaskKeys = ["DEMO-4", "DEMO-12"] },
            Events = events,
        };
        return Sign(unsigned);
    }

    private static DemoReplayTrace Sign(DemoReplayTrace trace) => trace with
    {
        Signature = new DemoReplaySignature
        {
            KeyId = "demo-replay-dev",
            Digest = DemoReplayTraceCanonicalizer.ComputeDigest(trace),
            Value = DemoReplayTraceCanonicalizer.ComputeSignature(trace, Key),
        },
    };

    [Fact]
    public void A_well_formed_signed_trace_is_accepted_with_and_without_the_key()
    {
        var trace = Valid();

        Assert.True(DemoReplayTraceVerification.Verify(trace).Accepted);
        Assert.True(DemoReplayTraceVerification.Verify(trace, trace.Signature!.Digest, Key).Accepted);
    }

    [Fact]
    public void The_canonical_form_is_stable_across_recomputation()
    {
        var trace = Valid();

        Assert.Equal(
            DemoReplayTraceCanonicalizer.ComputeDigest(trace),
            DemoReplayTraceCanonicalizer.ComputeDigest(trace with { Signature = null }));
    }

    [Fact]
    public void A_null_trace_is_refused_rather_than_throwing()
        => Assert.Equal(DemoReplayTraceRejections.SchemaUnsupported, DemoReplayTraceVerification.Verify(null).Reason);

    [Fact]
    public void An_unknown_schema_version_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.SchemaUnsupported,
            DemoReplayTraceVerification.Verify(Sign(Valid() with { SchemaVersion = 2 })).Reason);

    [Theory]
    [InlineData("")]
    [InlineData("no")]
    [InlineData("Demo-Instanz")]
    [InlineData("demo instanz")]
    public void A_malformed_trace_id_is_refused(string traceId)
        => Assert.Equal(
            DemoReplayTraceRejections.TraceIdInvalid,
            DemoReplayTraceVerification.Verify(Sign(Valid() with { TraceId = traceId })).Reason);

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    [InlineData(3601)]
    public void A_cycle_outside_the_supported_range_is_refused(int cycleSeconds)
        => Assert.Equal(
            DemoReplayTraceRejections.CycleOutOfRange,
            DemoReplayTraceVerification.Verify(Sign(Valid() with { CycleSeconds = cycleSeconds })).Reason);

    [Fact]
    public void An_empty_scene_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.SceneEmpty,
            DemoReplayTraceVerification.Verify(Sign(Valid() with { Scene = new DemoReplayScene() })).Reason);

    /// <summary>A scene may name only keys the ADR-0056 demo datastore owns.</summary>
    [Theory]
    [InlineData("AGT-2668")]
    [InlineData("DEMO-")]
    [InlineData("DEMOX-4")]
    public void A_scene_key_outside_the_demo_namespace_is_refused(string key)
        => Assert.Equal(
            DemoReplayTraceRejections.SceneNamespaceDenied,
            DemoReplayTraceVerification.Verify(Sign(Valid() with
            {
                Scene = new DemoReplayScene { Projects = ["Demo App"], TaskKeys = [key] },
            })).Reason);

    [Fact]
    public void An_empty_event_list_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.EventCountOutOfRange,
            DemoReplayTraceVerification.Verify(Sign(Valid() with { Events = [] })).Reason);

    [Fact]
    public void A_gap_in_the_sequence_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.SequenceNotDense,
            DemoReplayTraceVerification.Verify(Valid(events => events[1] = events[1] with { Sequence = 5 })).Reason);

    [Fact]
    public void A_backwards_offset_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.OffsetNotMonotonic,
            DemoReplayTraceVerification.Verify(Valid(events => events[2] = events[2] with { OffsetMs = 10_000 })).Reason);

    [Fact]
    public void An_offset_past_the_cycle_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.OffsetOutsideCycle,
            DemoReplayTraceVerification.Verify(Valid(events => events[2] = events[2] with { OffsetMs = 720_000 })).Reason);

    [Fact]
    public void An_event_naming_a_task_outside_the_scene_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.SceneKeyOutOfScope,
            DemoReplayTraceVerification.Verify(Valid(events => events[0] = events[0] with { TaskKey = "DEMO-9" })).Reason);

    [Theory]
    [InlineData("lane.changed")]
    [InlineData("task.completed")]
    [InlineData("diagnostic")]
    public void An_event_kind_outside_the_allowlist_is_refused(string kind)
        => Assert.Equal(
            DemoReplayTraceRejections.EventKindDenied,
            DemoReplayTraceVerification.Verify(Valid(events => events[0] = events[0] with { Kind = kind })).Reason);

    [Fact]
    public void A_message_carrying_control_characters_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.MessageNotPrintable,
            DemoReplayTraceVerification.Verify(Valid(events => events[0] = events[0] with { Message = "line\nbreak" })).Reason);

    [Fact]
    public void An_over_long_message_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.MessageNotPrintable,
            DemoReplayTraceVerification.Verify(Valid(events => events[0] = events[0] with { Message = new string('x', 501) })).Reason);

    [Fact]
    public void An_unsigned_trace_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.SignatureMissing,
            DemoReplayTraceVerification.Verify(Valid() with { Signature = null }).Reason);

    [Fact]
    public void An_unsupported_signature_algorithm_is_refused()
    {
        var trace = Valid();
        var swapped = trace with { Signature = trace.Signature! with { Algorithm = "md5" } };

        Assert.Equal(DemoReplayTraceRejections.SignatureAlgorithmDenied, DemoReplayTraceVerification.Verify(swapped).Reason);
    }

    /// <summary>Editing a message after signing changes the canonical form, so the digest no longer matches.</summary>
    [Fact]
    public void A_tampered_message_breaks_the_digest()
    {
        var trace = Valid();
        var tampered = trace with
        {
            Events = [trace.Events[0] with { Message = "Deployed to production." }, .. trace.Events.Skip(1)],
        };

        Assert.Equal(DemoReplayTraceRejections.DigestMismatch, DemoReplayTraceVerification.Verify(tampered).Reason);
    }

    [Fact]
    public void A_digest_that_does_not_match_the_release_manifest_is_refused()
        => Assert.Equal(
            DemoReplayTraceRejections.DigestMismatch,
            DemoReplayTraceVerification.Verify(Valid(), expectedDigest: new string('a', 64)).Reason);

    /// <summary>
    /// A trace re-signed with a key the server does not hold keeps a consistent
    /// digest, so only the authenticity check can catch it.
    /// </summary>
    [Fact]
    public void A_trace_signed_with_the_wrong_key_is_refused_when_the_key_is_configured()
    {
        var trace = Valid();
        var forged = trace with
        {
            Signature = trace.Signature! with
            {
                Value = DemoReplayTraceCanonicalizer.ComputeSignature(trace, Encoding.UTF8.GetBytes("attacker-key")),
            },
        };

        Assert.True(DemoReplayTraceVerification.Verify(forged).Accepted);
        Assert.Equal(
            DemoReplayTraceRejections.SignatureInvalid,
            DemoReplayTraceVerification.Verify(forged, signingKey: Key).Reason);
    }

    /// <summary>
    /// Pacing is part of the contract, not of the replay service, so both sides
    /// agree on which step is due at a given offset.
    /// </summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(29_999, 1)]
    [InlineData(30_000, 2)]
    [InlineData(89_999, 2)]
    [InlineData(90_000, 3)]
    [InlineData(long.MaxValue, 3)]
    public void The_due_step_is_the_last_one_whose_offset_has_passed(long positionMs, int expected)
        => Assert.Equal(expected, DemoReplayCycle.DueSequence(Valid().Events, positionMs));

    [Fact]
    public void An_empty_trace_has_no_due_step()
        => Assert.Equal(0, DemoReplayCycle.DueSequence([], 10_000));

    [Theory]
    [InlineData("DEMO-4", true)]
    [InlineData("PLAT-12", true)]
    [InlineData("AGT-2668", false)]
    [InlineData("DEMO-4a", false)]
    [InlineData("DEMO-", false)]
    [InlineData(null, false)]
    public void The_demo_namespace_check_admits_only_demo_and_platform_keys(string? key, bool expected)
        => Assert.Equal(expected, DemoReplayTraceVerification.IsDemoTaskKey(key));
}
