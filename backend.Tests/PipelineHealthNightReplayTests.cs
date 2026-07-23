using Xunit;

namespace AgentStudio.Tests;

public sealed class PipelineHealthNightReplayTests
{
    private static readonly DateTime NightStart =
        new(2026, 7, 22, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NightReplay_raises_all_three_visibility_alarms_at_their_budgets()
    {
        var detector = new PipelineHealthDetector();
        const string fingerprint = "lock:9c2f19e4a88c73ab";
        PipelineHealthAlert? fingerprintAlert = null;

        for (var index = 0; index < 3; index++)
        {
            var at = NightStart.AddMinutes(1 + index * 4);
            fingerprintAlert = detector.GateCompleted(new PipelineGateCompletion(
                $"failed-{index + 1}",
                index == 1 ? "Website" : "Agent Taskboard",
                $"/workspace/project-{index}",
                $"AGT-{2180 + index}",
                at,
                fingerprint));
        }

        Assert.NotNull(fingerprintAlert);
        Assert.Equal("systemic-gate-failure", fingerprintAlert.Kind);
        Assert.Equal(NightStart.AddMinutes(9), fingerprintAlert.DetectedAtUtc);

        detector.GateAcquired(new PipelineGateContext(
            "hung-gate",
            "Agent Taskboard",
            "/workspace/agent-taskboard",
            "AGT-2183",
            NightStart.AddMinutes(10)));
        var hanging = detector.DetectHangingGates(NightStart.AddMinutes(40));
        var gateAlert = Assert.Single(hanging);
        Assert.Equal("gate-hanging", gateAlert.Alert.Kind);
        Assert.Contains("30 min", gateAlert.Alert.Summary);

        var lane = PipelineHealthDetector.MeasureLane(
            TaskStates.AutoReview,
            [
                NightStart.AddMinutes(12),
                NightStart.AddMinutes(13),
                NightStart.AddMinutes(14),
                NightStart.AddMinutes(15),
            ],
            completedInWindow: 0,
            nowUtc: NightStart.AddMinutes(30));
        Assert.True(lane.IsStalled);
        Assert.Equal(0, lane.CompletedPerHour);
        Assert.Equal(4, lane.QueueCount);
    }

    [Fact]
    public void Passing_gate_resets_the_cross_card_fingerprint_sequence()
    {
        var detector = new PipelineHealthDetector();
        detector.GateCompleted(Completion("one", "same"));
        detector.GateCompleted(Completion("two", "same"));
        detector.GateCompleted(Completion("pass", null));
        detector.GateCompleted(Completion("three", "same"));

        var health = detector.FingerprintHealth();
        Assert.NotNull(health);
        Assert.Equal(1, health.ConsecutiveFailures);
        Assert.False(health.IsSystemic);
    }

    [Fact]
    public void Retries_of_one_card_count_as_one_cross_card_failure()
    {
        var detector = new PipelineHealthDetector();
        detector.GateCompleted(Completion("one", "same"));
        detector.GateCompleted(Completion("one", "same"));
        detector.GateCompleted(Completion("one", "same"));

        var health = detector.FingerprintHealth();
        Assert.NotNull(health);
        Assert.Equal(1, health.ConsecutiveFailures);
        Assert.False(health.IsSystemic);
    }

    private static PipelineGateCompletion Completion(string id, string? fingerprint) =>
        new(id, "Project", "/workspace/project", id, NightStart, fingerprint);
}
