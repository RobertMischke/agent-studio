using Xunit;

namespace AgentStudio.Tests;

public sealed class AcceptedIntegrationBackstopPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void SweepTelemetry_MixedOutcomes_LogsTruthfulCounters()
    {
        var summary = AcceptedIntegrationBackstopPolicy.Summarize(
        [
            MergeIntoIntegrationOutcome.Merged,
            MergeIntoIntegrationOutcome.MergedAfterRebase,
            MergeIntoIntegrationOutcome.AlreadyMerged,
            MergeIntoIntegrationOutcome.Error,
            MergeIntoIntegrationOutcome.Conflict,
            MergeIntoIntegrationOutcome.NoTaskBranch,
        ]);
        var logger = new CapturingLogger();

        AcceptedIntegrationBackstopTelemetry.LogSweep(logger, summary);

        Assert.Equal(6, summary.Attempted);
        Assert.Equal(2, summary.Merged);
        Assert.Equal(1, summary.AlreadyMerged);
        Assert.Equal(3, summary.Failed);
        Assert.Equal(2, summary.Integrated);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("attempted=6", entry.Message, StringComparison.Ordinal);
        Assert.Contains("merged=2", entry.Message, StringComparison.Ordinal);
        Assert.Contains("alreadyMerged=1", entry.Message, StringComparison.Ordinal);
        Assert.Contains("failed=3", entry.Message, StringComparison.Ordinal);
        Assert.Contains("integrated=2", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Alert_OverThirtyMinutesWithoutSuccessfulIntegration_SetsProjectHintAndLogsTaskKeys()
    {
        var snapshot = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            Now,
            TimeSpan.FromMinutes(30),
        [
            Candidate("AGT-2531", 31, MergeIntoIntegrationOutcome.NoTaskBranch.ToString()),
            Candidate("AGT-2541", 45, MergeIntoIntegrationOutcome.Error.ToString()),
            Candidate("AGT-NEW", 29, MergeIntoIntegrationOutcome.Conflict.ToString()),
            Candidate("AGT-DONE", 90, MergeIntoIntegrationOutcome.Merged.ToString(), IntegrationStatuses.Integrated),
        ]);
        var logger = new CapturingLogger();
        var logState = new AcceptedIntegrationAlertLogState();

        logState.Publish(logger, new AcceptedIntegrationAlertSnapshot(), snapshot, Now);

        Assert.True(snapshot.Active);
        Assert.Equal(2, snapshot.StalledTaskCount);
        Assert.Equal(["AGT-2541", "AGT-2531"], snapshot.Items.Select(item => item.TaskKey));
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("AGT-2531", warning.Message, StringComparison.Ordinal);
        Assert.Contains("AGT-2541", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AGT-NEW", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Alert_NoRecordedOutcome_StillSetsHintAfterThreshold()
    {
        var snapshot = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            Now,
            TimeSpan.FromMinutes(30),
            [Candidate("AGT-UNKNOWN", 35, lastOutcome: null)]);

        var item = Assert.Single(snapshot.Items);
        Assert.True(snapshot.Active);
        Assert.Null(item.LastOutcome);
    }

    private static AcceptedIntegrationAlertCandidate Candidate(
        string key,
        int acceptedMinutesAgo,
        string? lastOutcome,
        string integrationStatus = IntegrationStatuses.Pending)
        => new()
        {
            Task = new TaskInfo
            {
                Id = key.ToLowerInvariant(),
                TaskKey = key,
                Key = key,
                Title = $"Delivery {key}",
                ProjectName = "Agent Studio",
                State = TaskStates.HumanReview,
                Mode = TaskModes.Coding,
                EnteredLaneAt = Now.AddMinutes(-acceptedMinutesAgo),
            },
            AcceptedAt = Now.AddMinutes(-acceptedMinutesAgo),
            IntegrationStatus = integrationStatus,
            LastOutcome = lastOutcome,
        };

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
