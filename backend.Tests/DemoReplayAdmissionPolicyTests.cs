using AgentStudio.DemoReplay;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct matrix over the public-demo replay admission decision. The policy is
/// the single place that decides whether a simulated frame may touch the demo
/// scene, so every denial reason is pinned here rather than inferred from HTTP.
/// </summary>
public sealed class DemoReplayAdmissionPolicyTests
{
    private const string PinnedTrace = "demo-scene-2026-08";
    private const string PinnedDigest = "8f1e0a5c7b2d4e6f9a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f";

    private static DemoReplayOptions Options(bool enabled = true) => new()
    {
        Enabled = enabled,
        TraceId = PinnedTrace,
        TraceDigest = PinnedDigest,
        SigningKeyId = "demo-release-2026-08",
        PublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE",
    };

    private static DemoReplayAdmissionRequest Frame(
        string? traceId = PinnedTrace,
        string? digest = PinnedDigest,
        long epoch = 7,
        long sequence = 3,
        string? taskKey = "DEMO-5",
        string? kind = DemoReplayFrameKinds.TurnStarted,
        bool signatureValid = true)
        => new(traceId, digest, epoch, sequence, taskKey, kind, signatureValid);

    [Fact]
    public void A_sealed_frame_for_the_pinned_scene_is_admitted()
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(Options(), cursor: null, Frame());

        Assert.True(admission.Admitted);
        Assert.Null(admission.DenialCode);
    }

    [Fact]
    public void An_instance_without_a_pinned_trace_admits_nothing()
    {
        var unpinned = Options() with { TraceDigest = "" };

        var admission = DemoReplayAdmissionPolicy.Evaluate(unpinned, cursor: null, Frame());

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.Disabled, admission.DenialCode);
    }

    [Fact]
    public void Replay_stays_closed_until_it_is_explicitly_enabled()
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(Options(enabled: false), cursor: null, Frame());

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.Disabled, admission.DenialCode);
    }

    [Theory]
    [InlineData(null, PinnedDigest, 7, 3, "DEMO-5", DemoReplayFrameKinds.TurnStarted)]
    [InlineData(PinnedTrace, null, 7, 3, "DEMO-5", DemoReplayFrameKinds.TurnStarted)]
    [InlineData(PinnedTrace, PinnedDigest, 0, 3, "DEMO-5", DemoReplayFrameKinds.TurnStarted)]
    [InlineData(PinnedTrace, PinnedDigest, 7, 0, "DEMO-5", DemoReplayFrameKinds.TurnStarted)]
    [InlineData(PinnedTrace, PinnedDigest, 7, -1, "DEMO-5", DemoReplayFrameKinds.TurnStarted)]
    [InlineData(PinnedTrace, PinnedDigest, 7, 3, " ", DemoReplayFrameKinds.TurnStarted)]
    [InlineData(PinnedTrace, PinnedDigest, 7, 3, "DEMO-5", null)]
    public void Structurally_incomplete_frames_are_rejected_before_anything_else(
        string? traceId, string? digest, long epoch, long sequence, string? taskKey, string? kind)
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(
            Options(), cursor: null, Frame(traceId, digest, epoch, sequence, taskKey, kind));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.RequestInvalid, admission.DenialCode);
    }

    [Fact]
    public void A_frame_from_another_trace_is_denied()
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(
            Options(), cursor: null, Frame(traceId: "some-other-scene"));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.TraceMismatch, admission.DenialCode);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("not-hex-at-all")]
    public void A_frame_that_does_not_carry_the_pinned_digest_is_denied(string digest)
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(Options(), cursor: null, Frame(digest: digest));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.DigestMismatch, admission.DenialCode);
    }

    [Fact]
    public void The_pinned_digest_comparison_ignores_hex_casing()
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(
            Options(), cursor: null, Frame(digest: PinnedDigest.ToUpperInvariant()));

        Assert.True(admission.Admitted);
    }

    [Theory]
    [InlineData("AGT-2668")]
    [InlineData("PROD-1")]
    [InlineData("DEMO-")]
    [InlineData("demonstration")]
    public void Replay_cannot_reach_a_task_outside_the_demo_scene(string taskKey)
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(Options(), cursor: null, Frame(taskKey: taskKey));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.SceneKeyDenied, admission.DenialCode);
    }

    [Theory]
    [InlineData("DEMO-5")]
    [InlineData("PLAT-3")]
    [InlineData("plat-3")]
    public void Both_seeded_fixture_namespaces_stay_reachable(string taskKey)
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(Options(), cursor: null, Frame(taskKey: taskKey));

        Assert.True(admission.Admitted);
    }

    [Theory]
    [InlineData("task.moved")]
    [InlineData("run.completed")]
    [InlineData("decision.recorded")]
    public void Replay_cannot_emit_a_kind_outside_the_simulatable_set(string kind)
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(Options(), cursor: null, Frame(kind: kind));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.KindDenied, admission.DenialCode);
    }

    [Fact]
    public void An_unsealed_frame_is_denied_even_when_everything_else_matches()
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(
            Options(), cursor: null, Frame(signatureValid: false));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.SignatureInvalid, admission.DenialCode);
    }

    [Fact]
    public void A_frame_from_a_retired_epoch_is_denied()
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(
            Options(), new DemoReplayCursor(Epoch: 9, Sequence: 2), Frame(epoch: 8));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.EpochStale, admission.DenialCode);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(2)]
    [InlineData(1)]
    public void Sequences_must_increase_strictly_inside_one_epoch(long sequence)
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(
            Options(), new DemoReplayCursor(Epoch: 7, Sequence: 3), Frame(epoch: 7, sequence: sequence));

        Assert.False(admission.Admitted);
        Assert.Equal(DemoReplayDenialCodes.SequenceNotMonotonic, admission.DenialCode);
    }

    [Fact]
    public void A_new_epoch_restarts_the_sequence_window()
    {
        var admission = DemoReplayAdmissionPolicy.Evaluate(
            Options(), new DemoReplayCursor(Epoch: 7, Sequence: 40), Frame(epoch: 8, sequence: 1));

        Assert.True(admission.Admitted);
    }

    [Fact]
    public void The_ledger_settles_concurrent_frames_that_the_policy_snapshot_both_admitted()
    {
        var ledger = new DemoReplayEpochLedger();

        Assert.Null(ledger.Peek());
        Assert.True(ledger.TryAdvance(epoch: 4, sequence: 1));
        Assert.False(ledger.TryAdvance(epoch: 4, sequence: 1));
        Assert.True(ledger.TryAdvance(epoch: 4, sequence: 2));
        Assert.False(ledger.TryAdvance(epoch: 3, sequence: 99));
        Assert.True(ledger.TryAdvance(epoch: 5, sequence: 1));
        Assert.Equal(new DemoReplayCursor(5, 1), ledger.Peek());
    }
}
