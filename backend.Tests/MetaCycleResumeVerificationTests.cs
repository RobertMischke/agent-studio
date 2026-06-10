using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the post-restart resume contract for the meta-cycle. After
/// <c>UpdateStableThenResume</c> (or any other resume action) the cycle must
/// verify the project actually flipped back to <c>auto-continuous</c>; if it
/// did not, retry, and on persistent failure raise a high-severity
/// <c>cycle-resume-failed</c> advisory plus a <c>[supervisor]</c> chat-note.
/// Without this loop a single transient SetMode failure (e.g. a backend
/// restart race or a runner not yet wired for the project) leaves the project
/// silently paused, which is the regression that motivated this task.
/// </summary>
public class MetaCycleResumeVerificationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ModeFlipsImmediately_ReturnsVerifiedFirstTry_AfterOneAttempt()
    {
        var time = new FakeTimeProvider();
        var attempts = 0;

        var outcome = await MetaCycleHostedService.VerifyResumeWithRetryAsync(
            resumeAttempt: (_, _) => { attempts++; return Task.CompletedTask; },
            getCurrentMode: () => "auto-continuous",
            expectedMode: "auto-continuous",
            maxAttempts: 5,
            baseBackoff: TimeSpan.FromMilliseconds(50),
            time: time,
            ct: CancellationToken.None);

        Assert.Equal(MetaCycleHostedService.ResumeVerificationResult.VerifiedFirstTry, outcome.Result);
        Assert.Equal(1, outcome.AttemptsMade);
        Assert.Equal(1, attempts);
        Assert.Equal("auto-continuous", outcome.LastObservedMode);
        Assert.Null(outcome.LastException);
    }

    [Fact]
    public async Task ModeFlipsOnThirdProbe_ReturnsVerifiedAfterRetries_AndStopsAttemptingFurther()
    {
        var time = new FakeTimeProvider();
        var probes = 0;
        var resumeCalls = 0;

        var waitTask = MetaCycleHostedService.VerifyResumeWithRetryAsync(
            resumeAttempt: (_, _) => { resumeCalls++; return Task.CompletedTask; },
            getCurrentMode: () =>
            {
                probes++;
                // Probes 1 and 2 see the runner still paused (e.g. the new
                // backend has not finished wiring the runner yet); probe 3
                // observes the resume took.
                return probes >= 3 ? "auto-continuous" : "paused";
            },
            expectedMode: "auto-continuous",
            maxAttempts: 5,
            baseBackoff: TimeSpan.FromMilliseconds(50),
            time: time,
            ct: CancellationToken.None);

        // Drive the fake clock forward so the inter-attempt Task.Delay calls
        // resolve. Keep advancing until the loop completes.
        while (!waitTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }

        var outcome = await waitTask;
        Assert.Equal(MetaCycleHostedService.ResumeVerificationResult.VerifiedAfterRetries, outcome.Result);
        Assert.Equal(3, outcome.AttemptsMade);
        Assert.Equal(3, resumeCalls);
        Assert.Equal("auto-continuous", outcome.LastObservedMode);
    }

    [Fact]
    public async Task ModeNeverFlips_ReturnsExhaustedRetries_AfterMaxAttempts()
    {
        var time = new FakeTimeProvider();
        var resumeCalls = 0;

        var waitTask = MetaCycleHostedService.VerifyResumeWithRetryAsync(
            resumeAttempt: (_, _) => { resumeCalls++; return Task.CompletedTask; },
            getCurrentMode: () => "paused",
            expectedMode: "auto-continuous",
            maxAttempts: 5,
            baseBackoff: TimeSpan.FromMilliseconds(50),
            time: time,
            ct: CancellationToken.None);

        while (!waitTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Yield();
        }

        var outcome = await waitTask;
        Assert.Equal(MetaCycleHostedService.ResumeVerificationResult.ExhaustedRetries, outcome.Result);
        Assert.Equal(5, outcome.AttemptsMade);
        Assert.Equal(5, resumeCalls);
        Assert.Equal("paused", outcome.LastObservedMode);
    }

    [Fact]
    public async Task ResumeAttemptThrows_StillProbesMode_AndCarriesLastExceptionOnExhaustion()
    {
        var time = new FakeTimeProvider();

        var waitTask = MetaCycleHostedService.VerifyResumeWithRetryAsync(
            resumeAttempt: (_, _) => throw new InvalidOperationException("SetMode rejected"),
            getCurrentMode: () => "paused",
            expectedMode: "auto-continuous",
            maxAttempts: 3,
            baseBackoff: TimeSpan.FromMilliseconds(20),
            time: time,
            ct: CancellationToken.None);

        while (!waitTask.IsCompleted)
        {
            time.Advance(TimeSpan.FromMilliseconds(200));
            await Task.Yield();
        }

        var outcome = await waitTask;
        Assert.Equal(MetaCycleHostedService.ResumeVerificationResult.ExhaustedRetries, outcome.Result);
        Assert.Equal(3, outcome.AttemptsMade);
        Assert.NotNull(outcome.LastException);
        Assert.IsType<InvalidOperationException>(outcome.LastException);
    }

    [Fact]
    public async Task Cancellation_PropagatesAsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        var time = new FakeTimeProvider();

        var waitTask = MetaCycleHostedService.VerifyResumeWithRetryAsync(
            resumeAttempt: (_, _) => Task.CompletedTask,
            getCurrentMode: () => "paused",
            expectedMode: "auto-continuous",
            maxAttempts: 100,
            baseBackoff: TimeSpan.FromSeconds(60),
            time: time,
            ct: cts.Token);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
    }

    [Fact]
    public void BuildResumeFailedAdvisory_HasHighSeverity_AndCycleResumeFailedTopic()
    {
        var report = SampleReport("mc-202605051200-abcdef01");
        var outcome = new MetaCycleHostedService.ResumeVerificationOutcome(
            MetaCycleHostedService.ResumeVerificationResult.ExhaustedRetries,
            AttemptsMade: 5,
            LastObservedMode: "paused",
            LastException: null);
        var at = new DateTime(2026, 5, 5, 8, 7, 7, DateTimeKind.Utc);

        var adv = MetaCycleHostedService.BuildResumeFailedAdvisory("agent-taskboard", report, outcome, at);

        Assert.Equal(SupervisorSeverity.High, adv.Severity);
        Assert.Equal("cycle-resume-failed", adv.Topic);
        Assert.Equal(SupervisorSource.AutoIntervention, adv.Source);
        Assert.Equal("agent-taskboard", adv.Project);
        Assert.Equal(at, adv.CreatedAt);
        Assert.Contains(report.CycleId, adv.Message);
        Assert.Contains("5 attempts", adv.Message);
        Assert.Contains("paused", adv.Message);
    }

    [Fact]
    public void BuildResumeFailedAdvisory_PersistsThroughObservationStore_AsHighSeverityRecord()
    {
        using var temp = new TempDir();
        var report = SampleReport("mc-202605051207-deadbeef");
        var outcome = new MetaCycleHostedService.ResumeVerificationOutcome(
            MetaCycleHostedService.ResumeVerificationResult.ExhaustedRetries,
            AttemptsMade: 5,
            LastObservedMode: "paused",
            LastException: null);

        var adv = MetaCycleHostedService.BuildResumeFailedAdvisory("agent-taskboard", report, outcome, DateTime.UtcNow);
        HardHealthCheckHostedService.AppendObservationRecord(temp.Path, adv);

        var path = SupervisorLogPaths.ObservationsFile(temp.Path, "agent-taskboard");
        Assert.True(File.Exists(path));
        var line = File.ReadAllText(path).TrimEnd();
        var decoded = JsonSerializer.Deserialize<SupervisorAdvisory>(line, Json);
        Assert.NotNull(decoded);
        Assert.Equal(SupervisorSeverity.High, decoded!.Severity);
        Assert.Equal("cycle-resume-failed", decoded.Topic);
    }

    [Fact]
    public void BuildResumeFailedChatNoteText_MentionsCycleId_ProjectName_AttemptCount_AndLastMode()
    {
        var report = SampleReport("mc-202605051207-deadbeef");
        var outcome = new MetaCycleHostedService.ResumeVerificationOutcome(
            MetaCycleHostedService.ResumeVerificationResult.ExhaustedRetries,
            AttemptsMade: 7,
            LastObservedMode: "paused",
            LastException: null);

        var text = MetaCycleHostedService.BuildResumeFailedChatNoteText("agent-taskboard", report, outcome);

        Assert.Contains(report.CycleId, text);
        Assert.Contains("agent-taskboard", text);
        Assert.Contains("7 attempts", text);
        Assert.Contains("paused", text);
    }

    private static MetaCycleReport SampleReport(string cycleId)
    {
        var jobs = new List<MetaCycleJobObservation>
        {
            new(JobId: "first-job", Title: "First", NewCommits: 1, HasArtefacts: true),
            new(JobId: "last-job", Title: "Last", NewCommits: 0, HasArtefacts: true),
        };
        var inspection = new MetaCycleInspection(
            CommitLogDiff: new MetaCycleCommitLogDiff(0, null, null),
            LastCrashMarker: new MetaCycleCrashMarker(false, null, null),
            SupervisorAdvisories: new MetaCycleAdvisorySummary(0, Array.Empty<string>()),
            StuckInProgress: new MetaCycleStuckInProgress(0, Array.Empty<string>()),
            ExpectedArtefacts: new MetaCycleExpectedArtefacts(0, Array.Empty<string>()),
            RunnerModeDrift: new MetaCycleRunnerModeDrift(false, null, null),
            Extras: null);
        return new MetaCycleReport(
            CycleId: cycleId,
            Project: "agent-taskboard",
            StartedAt: new DateTime(2026, 5, 5, 8, 0, 0, DateTimeKind.Utc),
            CompletedAt: new DateTime(2026, 5, 5, 8, 7, 7, DateTimeKind.Utc),
            CycleLengthN: 2,
            JobsObserved: jobs,
            Inspection: inspection,
            Findings: Array.Empty<MetaCycleFinding>(),
            Verdict: MetaCycleVerdict.Healthy,
            Action: new MetaCycleAction(MetaCycleActionKind.UpdateStableThenResume, "healthy:update-stable-then-resume"),
            ConfigSnapshot: null);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "meta-cycle-resume-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
