using System.Security.Cryptography;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.DemoReplayRunner.Tests;

/// <summary>
/// Trace loading, seal verification, and cycle planning. The service refuses to
/// start on anything it cannot verify, so a swapped bundle fails at boot instead
/// of producing a stream of denials against the public instance.
/// </summary>
public sealed class ReplayTraceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static DemoReplayTrace Trace() => new(
        DemoReplayTraceDigest.CurrentSchemaVersion,
        "demo-scene-reports-export",
        "reports-export",
        ["DEMO-5", "PLAT-3"],
        [
            new DemoReplayFrame(1, 0, "DEMO-5", DemoReplayFrameKinds.SessionStarted, "Simulated run opened"),
            new DemoReplayFrame(2, 30, "DEMO-5", DemoReplayFrameKinds.TurnStarted, "Simulated turn"),
            new DemoReplayFrame(3, 90, "PLAT-3", DemoReplayFrameKinds.TurnCompleted, "Simulated turn done"),
        ]);

    [Fact]
    public void A_correctly_sealed_trace_loads()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = DemoReplayTraceSignature.Sign(Trace(), key, "demo-release-2026-08");

        var loaded = ReplayTraceLoader.Parse(
            JsonSerializer.Serialize(signed, Json),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));

        Assert.Equal(signed.Digest, loaded.Digest);
        Assert.Equal(3, loaded.Trace.Frames.Count);
    }

    [Fact]
    public void The_digest_normalizes_declared_task_key_order_and_whitespace()
    {
        var trace = Trace();
        var equivalent = trace with { TaskKeys = ["PLAT-3 ", " DEMO-5"] };

        Assert.Equal(DemoReplayTraceDigest.Compute(trace), DemoReplayTraceDigest.Compute(equivalent));
    }

    [Fact]
    public void Frames_authored_out_of_order_are_refused_rather_than_silently_sorted()
    {
        var trace = Trace();
        var reordered = trace with { Frames = [.. trace.Frames.Reverse()] };

        Assert.Throws<ArgumentException>(() => DemoReplayTraceDigest.Compute(reordered));
    }

    [Fact]
    public void A_trace_with_an_edited_frame_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = DemoReplayTraceSignature.Sign(Trace(), key, "demo-release-2026-08");
        var tampered = signed with
        {
            Trace = signed.Trace with
            {
                Frames = [signed.Trace.Frames[0] with { Message = "Deploying to production" }, .. signed.Trace.Frames.Skip(1)],
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => ReplayTraceLoader.Parse(
            JsonSerializer.Serialize(tampered, Json),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));

        Assert.Contains("did not verify", ex.Message);
    }

    [Fact]
    public void A_trace_sealed_by_another_key_is_refused()
    {
        using var releaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = DemoReplayTraceSignature.Sign(Trace(), attackerKey, "forged");

        Assert.Throws<InvalidOperationException>(() => ReplayTraceLoader.Parse(
            JsonSerializer.Serialize(signed, Json),
            Convert.ToBase64String(releaseKey.ExportSubjectPublicKeyInfo())));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void Malformed_trace_content_is_refused(string content)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<InvalidOperationException>(() => ReplayTraceLoader.Parse(
            content, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void A_trace_declaring_the_wrong_schema_version_never_seals(int schemaVersion)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<ArgumentException>(
            () => DemoReplayTraceSignature.Sign(Trace() with { SchemaVersion = schemaVersion }, key, "k"));
    }

    [Fact]
    public void A_frame_targeting_an_undeclared_task_never_seals()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trace = Trace();
        var invalid = trace with
        {
            Frames = [.. trace.Frames, new DemoReplayFrame(4, 120, "AGT-2668", DemoReplayFrameKinds.TurnStarted)],
        };

        Assert.Throws<ArgumentException>(() => DemoReplayTraceSignature.Sign(invalid, key, "k"));
    }

    [Fact]
    public void A_cycle_plan_carries_every_seal_at_its_scaled_offset()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = DemoReplayTraceSignature.Sign(Trace(), key, "demo-release-2026-08");
        var start = new DateTime(2026, 8, 9, 8, 0, 0, DateTimeKind.Utc);

        var plan = ReplayCycle.Plan(signed, epoch: 4, speedFactor: 2.0, start);

        Assert.Equal([TimeSpan.Zero, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45)], plan.Select(step => step.Delay));
        Assert.All(plan, step =>
        {
            Assert.Equal(4, step.Request.Epoch);
            Assert.Equal(signed.Digest, step.Request.TraceDigest);
            Assert.Equal(signed.Trace.TraceId, step.Request.TraceId);
            Assert.True(DemoReplayTraceSignature.VerifyFrame(
                signed.Trace.TraceId,
                signed.Digest,
                step.Request.Frame,
                step.Request.Signature,
                Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())));
        });
        Assert.Equal(start.AddSeconds(90), plan[2].Request.OccurredAt);
        Assert.Equal(TimeSpan.FromSeconds(45), ReplayCycle.Duration(signed, speedFactor: 2.0));
    }

    [Fact]
    public void Sequences_inside_a_planned_cycle_increase_strictly()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signed = DemoReplayTraceSignature.Sign(Trace(), key, "demo-release-2026-08");

        var sequences = ReplayCycle.Plan(signed, epoch: 1, speedFactor: 1.0, DateTime.UnixEpoch)
            .Select(step => step.Request.Frame.Sequence)
            .ToList();

        Assert.Equal([1L, 2L, 3L], sequences);
    }
}
