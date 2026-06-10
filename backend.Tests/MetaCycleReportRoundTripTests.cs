using System.Text.Json;
using System.Text.Json.Serialization;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the report write + read round-trip. Two simulated batches:
/// (1) a healthy 3-job batch resumes; (2) a batch with a crash marker
/// triggers a fix-task. The hosted service writes per-cycle JSON files; the
/// supervisor endpoint reads them back. If either path drifts (rename of a
/// field, change of enum casing) the timeline view in the frontend breaks
/// silently. The disk shape is normative.
/// </summary>
public class MetaCycleReportRoundTripTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public void HealthyThreeJobBatch_RoundTripsThroughDisk_WithResumeAction()
    {
        using var tmp = new TempWorkspace();
        var project = "agent-taskboard";

        var jobs = new[]
        {
            new MetaCycleJobObservation("job-1", "First", 1, true),
            new MetaCycleJobObservation("job-2", "Second", 2, true),
            new MetaCycleJobObservation("job-3", "Third", 1, true),
        };

        var inspection = new MetaCycleInspection(
            CommitLogDiff: new MetaCycleCommitLogDiff(4, "abc", "def"),
            LastCrashMarker: new MetaCycleCrashMarker(false, null, null),
            SupervisorAdvisories: new MetaCycleAdvisorySummary(0, Array.Empty<string>()),
            StuckInProgress: new MetaCycleStuckInProgress(0, Array.Empty<string>()),
            ExpectedArtefacts: new MetaCycleExpectedArtefacts(0, Array.Empty<string>()),
            RunnerModeDrift: new MetaCycleRunnerModeDrift(false, "auto-continuous", "auto-continuous"));

        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-202605051200-aaaa1111",
            project: project,
            startedAt: new DateTime(2026, 5, 5, 12, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 5, 5, 12, 0, 5, DateTimeKind.Utc),
            config: MetaCycleConfig.Defaults() with { CycleLengthN = 3 },
            jobs: jobs,
            inspection: inspection,
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 0);

        // Sanity on the rules layer.
        Assert.Equal(MetaCycleVerdict.Healthy, report.Verdict);
        Assert.Equal(MetaCycleActionKind.Resume, report.Action.Kind);

        // Write to the canonical path on disk and round-trip the JSON the
        // same way the supervisor endpoint does.
        var dir = SupervisorLogPaths.MetaCycleDir(tmp.Path, project);
        Directory.CreateDirectory(dir);
        var reportPath = SupervisorLogPaths.MetaCycleReportFile(tmp.Path, project, report.CycleId);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));

        Assert.True(File.Exists(reportPath));
        var raw = File.ReadAllText(reportPath);
        var rehydrated = JsonSerializer.Deserialize<MetaCycleReport>(raw, Json);

        Assert.NotNull(rehydrated);
        Assert.Equal(report.CycleId, rehydrated!.CycleId);
        Assert.Equal(report.Project, rehydrated.Project);
        Assert.Equal(3, rehydrated.JobsObserved.Count);
        Assert.Equal(MetaCycleVerdict.Healthy, rehydrated.Verdict);
        Assert.Equal(MetaCycleActionKind.Resume, rehydrated.Action.Kind);
        Assert.Equal("healthy", rehydrated.Action.Reason);
    }

    [Fact]
    public void CrashMarkerBatch_RoundTripsThroughDisk_WithQueueFixAction()
    {
        using var tmp = new TempWorkspace();
        var project = "agent-taskboard";

        // Drop a last-crash marker into the workspace the way the runtime
        // would. The rules pipeline does not read from disk; we pass the
        // crash-marker block directly. The disk write proves the schema
        // tolerates the crash details that the inspection captures.
        var crashMarkerPath = Path.Combine(tmp.Path, "logs", "last-crash.json");
        Directory.CreateDirectory(Path.GetDirectoryName(crashMarkerPath)!);
        File.WriteAllText(crashMarkerPath, "{\"reason\":\"orphan changes rescued\"}");

        var jobs = new[]
        {
            new MetaCycleJobObservation("job-a", "A", 1, true),
            new MetaCycleJobObservation("job-b", "B", 0, false),
            new MetaCycleJobObservation("job-c", "C", 1, true),
        };

        var inspection = new MetaCycleInspection(
            CommitLogDiff: new MetaCycleCommitLogDiff(2, "abc", "def"),
            LastCrashMarker: new MetaCycleCrashMarker(true, new DateTime(2026, 5, 5, 11, 50, 0, DateTimeKind.Utc), "orphan changes rescued"),
            SupervisorAdvisories: new MetaCycleAdvisorySummary(0, Array.Empty<string>()),
            StuckInProgress: new MetaCycleStuckInProgress(0, Array.Empty<string>()),
            ExpectedArtefacts: new MetaCycleExpectedArtefacts(1, new[] { "job-b" }),
            RunnerModeDrift: new MetaCycleRunnerModeDrift(false, "auto-continuous", "auto-continuous"));

        var report = MetaCycleRules.BuildReport(
            cycleId: "mc-202605051210-bbbb2222",
            project: project,
            startedAt: new DateTime(2026, 5, 5, 12, 10, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 5, 5, 12, 10, 5, DateTimeKind.Utc),
            config: MetaCycleConfig.Defaults() with { CycleLengthN = 3 },
            jobs: jobs,
            inspection: inspection,
            autoCommitEnabled: true,
            autoFixesInTrailingHour: 0);

        Assert.Equal(MetaCycleVerdict.FixTriggering, report.Verdict);
        Assert.Equal(MetaCycleActionKind.QueueFix, report.Action.Kind);
        Assert.Contains("last-crash-marker", report.Action.Reason);

        var dir = SupervisorLogPaths.MetaCycleDir(tmp.Path, project);
        Directory.CreateDirectory(dir);
        var reportPath = SupervisorLogPaths.MetaCycleReportFile(tmp.Path, project, report.CycleId);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, Json));

        var raw = File.ReadAllText(reportPath);
        var rehydrated = JsonSerializer.Deserialize<MetaCycleReport>(raw, Json);

        Assert.NotNull(rehydrated);
        Assert.True(rehydrated!.Inspection.LastCrashMarker.Present);
        Assert.Equal(MetaCycleVerdict.FixTriggering, rehydrated.Verdict);
        Assert.Equal(MetaCycleActionKind.QueueFix, rehydrated.Action.Kind);
        Assert.Contains("last-crash-marker", rehydrated.Action.Reason);

        // The endpoint enumerates *.json under the meta-cycle dir; the file
        // we just wrote must show up there.
        var files = Directory.EnumerateFiles(dir, "*.json").ToList();
        Assert.Single(files);
        Assert.Equal(reportPath, files[0]);
    }

    private sealed class TempWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "metacycle-tests-" + Guid.NewGuid().ToString("N"));
        public TempWorkspace() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
