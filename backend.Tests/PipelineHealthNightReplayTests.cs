using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PipelineHealthNightReplayTests
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "pipeline-health",
        "night-2026-07-22-23.normalized.jsonl");

    [Fact]
    public void NightLogReplay_raises_all_three_visibility_alarms_within_their_budgets()
    {
        Assert.True(File.Exists(FixturePath), $"Missing night replay fixture: {FixturePath}");

        var runtimeEvents = new RuntimeEventReader().Read(FixturePath);
        Assert.Empty(runtimeEvents.Warnings);
        Assert.Equal(7, runtimeEvents.Events.Count);

        var detector = new PipelineHealthDetector();
        PipelineHealthAlert? fingerprintAlert = null;
        PipelineLaneDrainHealth? stalledLane = null;
        DateTime? firstFingerprintAt = null;
        DateTime? laneObservedAt = null;
        DateTime? gateAcquiredAt = null;
        DateTime? replayEndedAt = null;
        string? acquiredGateRunId = null;
        var gateCompleted = false;

        foreach (var line in File.ReadLines(FixturePath))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            var at = row.GetProperty("timestamp").GetDateTime().ToUniversalTime();
            var eventName = row.GetProperty("event").GetString();
            var payload = row.GetProperty("payload");

            switch (eventName)
            {
                case "pipeline.build-test-gate.completed":
                    var completedGateRunId = payload.GetProperty("gateRunId").GetString()!;
                    if (string.Equals(completedGateRunId, acquiredGateRunId, StringComparison.Ordinal))
                        gateCompleted = true;
                    firstFingerprintAt ??= at;
                    fingerprintAlert = detector.GateCompleted(new PipelineGateCompletion(
                        completedGateRunId,
                        row.GetProperty("project").GetString()!,
                        payload.GetProperty("watchPath").GetString()!,
                        row.GetProperty("jobId").GetString()!,
                        at,
                        payload.GetProperty("failureFingerprint").GetString()));
                    break;

                case "pipeline.build-test-gate.acquired":
                    gateAcquiredAt = at;
                    acquiredGateRunId = payload.GetProperty("gateRunId").GetString()!;
                    detector.GateAcquired(new PipelineGateContext(
                        acquiredGateRunId,
                        row.GetProperty("project").GetString()!,
                        payload.GetProperty("watchPath").GetString()!,
                        row.GetProperty("jobId").GetString()!,
                        at));
                    break;

                case "pipeline.lane-inventory":
                    laneObservedAt = at;
                    var queueCount = payload.GetProperty("queueCount").GetInt32();
                    var oldestQueuedAtUtc = payload.GetProperty("oldestQueuedAtUtc").GetDateTime();
                    stalledLane = PipelineHealthDetector.MeasureLane(
                        payload.GetProperty("lane").GetString()!,
                        Enumerable.Repeat(oldestQueuedAtUtc, queueCount).ToArray(),
                        payload.GetProperty("completedInPriorHour").GetInt32(),
                        at);
                    break;

                case "pipeline.replay-ended":
                    replayEndedAt = at;
                    break;
            }
        }

        Assert.NotNull(fingerprintAlert);
        Assert.Equal("systemic-gate-failure", fingerprintAlert.Kind);
        Assert.NotNull(firstFingerprintAt);
        var fingerprintLatency = fingerprintAlert.DetectedAtUtc - firstFingerprintAt.Value;
        Assert.Equal(TimeSpan.FromMinutes(8) + TimeSpan.FromSeconds(19), fingerprintLatency);
        var fingerprintHealth = detector.FingerprintHealth();
        Assert.NotNull(fingerprintHealth);
        Assert.Equal(3, fingerprintHealth.ConsecutiveFailures);
        Assert.Equal(["Agent Taskboard", "Website"], fingerprintHealth.Projects);

        Assert.NotNull(stalledLane);
        Assert.NotNull(laneObservedAt);
        Assert.True(stalledLane.IsStalled);
        Assert.Equal(TaskStates.AutoReview, stalledLane.Lane);
        Assert.Equal(0, stalledLane.CompletedPerHour);
        Assert.Equal(65, stalledLane.QueueCount);

        Assert.NotNull(gateAcquiredAt);
        Assert.NotNull(replayEndedAt);
        Assert.False(gateCompleted);
        var gateAlarmAt = gateAcquiredAt.Value + PipelineHealthConventions.GateCompletionBudget;
        var hanging = detector.DetectHangingGates(gateAlarmAt);
        var gateAlert = Assert.Single(hanging);
        Assert.Equal("gate-hanging", gateAlert.Alert.Kind);
        Assert.Equal("7bbed536", gateAlert.Gate.GateRunId);
        Assert.Equal(PipelineHealthConventions.GateCompletionBudget, gateAlarmAt - gateAcquiredAt.Value);

        Assert.All(
            new[] { laneObservedAt.Value, fingerprintAlert.DetectedAtUtc, gateAlert.Alert.DetectedAtUtc },
            detectedAt => Assert.True(
                detectedAt < replayEndedAt,
                "Every visibility alarm must predate the backend restart that released the night gate."));
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
    public void Non_adjacent_retries_of_one_card_count_as_one_cross_card_failure()
    {
        var detector = new PipelineHealthDetector();
        detector.GateCompleted(Completion("one", "same"));
        detector.GateCompleted(Completion("two", "same"));
        detector.GateCompleted(Completion("one", "same"));

        var health = detector.FingerprintHealth();
        Assert.NotNull(health);
        Assert.Equal(2, health.ConsecutiveFailures);
        Assert.False(health.IsSystemic);
    }

    [Fact]
    public void Systemic_fingerprint_alarm_is_appended_to_the_orchestrator_feed()
    {
        var watchPath = Path.Combine(
            Path.GetTempPath(),
            "pipeline-health-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watchPath);
        try
        {
            var configuration = new ConfigurationBuilder().Build();
            var scanner = new TaskScannerService(
                configuration,
                NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(
                    NullLogger<SummaryGenerationService>.Instance,
                    configuration));
            var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
            var service = new PipelineHealthService(
                new PipelineHealthDetector(),
                scanner,
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                orchestratorLog,
                NullLogger<PipelineHealthService>.Instance);

            service.GateCompleted(FeedCompletion("one", watchPath));
            service.GateCompleted(FeedCompletion("two", watchPath));
            service.GateCompleted(FeedCompletion("three", watchPath));

            var entry = Assert.Single(orchestratorLog.Read(watchPath));
            Assert.Equal(OrchestratorLogKinds.Alert, entry.Kind);
            Assert.Equal(OrchestratorLogTopics.PipelineHealth, entry.Topic);
            Assert.Contains("Systemic gate problem", entry.Summary);
        }
        finally
        {
            Directory.Delete(watchPath, recursive: true);
        }
    }

    [Fact]
    public async Task Hanging_gate_and_zero_drain_alarms_are_appended_to_the_orchestrator_feed()
    {
        var watchPath = Path.Combine(
            Path.GetTempPath(),
            "pipeline-health-feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(watchPath);
        try
        {
            var now = new DateTime(2026, 7, 23, 5, 7, 37, DateTimeKind.Utc);
            CreateQueuedTask(watchPath, "review-one", now - TimeSpan.FromMinutes(20));
            CreateQueuedTask(watchPath, "review-two", now - TimeSpan.FromMinutes(18));

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WatchPaths:0:Name"] = "Project",
                    ["WatchPaths:0:Path"] = watchPath,
                    ["WatchPaths:0:RootPath"] = watchPath,
                })
                .Build();
            var scanner = new TaskScannerService(
                configuration,
                NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(
                    NullLogger<SummaryGenerationService>.Instance,
                    configuration));
            var orchestratorLog = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
            var service = new PipelineHealthService(
                new PipelineHealthDetector(),
                scanner,
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                orchestratorLog,
                NullLogger<PipelineHealthService>.Instance);

            service.GateAcquired(new PipelineGateContext(
                "night-gate",
                "Project",
                watchPath,
                "night-card",
                now - PipelineHealthConventions.GateCompletionBudget));

            await service.EvaluateAsync(now);

            var entries = orchestratorLog.Read(watchPath);
            Assert.Equal(2, entries.Count);
            Assert.All(entries, entry =>
            {
                Assert.Equal(OrchestratorLogKinds.Alert, entry.Kind);
                Assert.Equal(OrchestratorLogTopics.PipelineHealth, entry.Topic);
            });
            Assert.Contains(entries, entry => entry.Summary.Contains("hanging", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(entries, entry => entry.Summary.Contains("drain rate is 0/h", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(watchPath, recursive: true);
        }
    }

    private static PipelineGateCompletion Completion(string id, string? fingerprint) =>
        new(
            $"{id}-{Guid.NewGuid():N}",
            "Project",
            "/workspace/project",
            id,
            new DateTime(2026, 7, 23, 4, 0, 0, DateTimeKind.Utc),
            fingerprint);

    private static PipelineGateCompletion FeedCompletion(string id, string watchPath) =>
        new(
            $"feed-{id}",
            "Project",
            watchPath,
            id,
            new DateTime(2026, 7, 23, 4, 0, 0, DateTimeKind.Utc),
            "same");

    private static void CreateQueuedTask(string watchPath, string id, DateTime enteredLaneAt)
    {
        var taskFolder = Path.Combine(watchPath, TaskStates.AutoReview, id);
        Directory.CreateDirectory(taskFolder);
        File.WriteAllText(
            Path.Combine(taskFolder, "task.json"),
            JsonSerializer.Serialize(new
            {
                id,
                title = id,
                state = TaskStates.AutoReview,
                createdAt = enteredLaneAt.AddHours(-1),
                enteredLaneAt,
            }));
    }
}
