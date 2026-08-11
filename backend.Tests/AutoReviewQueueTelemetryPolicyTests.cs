using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

public sealed class AutoReviewQueueTelemetryPolicyTests
{
    private static readonly DateTime Now =
        new(2026, 8, 11, 18, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Evaluate_SeparatesClaimableDepthFromLiveReviewsAndComputesRollingRates()
    {
        var snapshot = AutoReviewQueueTelemetryPolicy.Evaluate(
            Now,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(24),
            TimeSpan.FromMinutes(30),
            [
                Fact(Now.AddMinutes(-45), open: true),
                Fact(
                    Now.AddMinutes(-60),
                    open: true,
                    acquiredAt: Now.AddMinutes(-50),
                    expiresAt: Now.AddMinutes(-1)),
                Fact(
                    Now.AddMinutes(-20),
                    open: true,
                    acquiredAt: Now.AddMinutes(-15),
                    expiresAt: Now.AddMinutes(5)),
                Fact(
                    Now.AddMinutes(-30),
                    open: true,
                    acquiredAt: Now.AddMinutes(-25),
                    expiresAt: Now.AddMinutes(5),
                    processUnknown: true),
                Fact(
                    Now.AddMinutes(-40),
                    acquiredAt: Now.AddMinutes(-15),
                    reportedAt: Now.AddMinutes(-10)),
                Fact(
                    Now.AddMinutes(-50),
                    acquiredAt: Now.AddMinutes(-45),
                    reportedAt: Now.AddMinutes(-30)),
                Fact(
                    Now.AddMinutes(-10),
                    reportedAt: Now.AddMinutes(-5),
                    superseded: true),
                Fact(
                    Now.AddMinutes(-5),
                    reportedAt: Now.AddMinutes(-2),
                    countsAsDrain: false),
            ]);

        Assert.Equal(3, snapshot.QueueDepth);
        Assert.Equal(1, snapshot.ActiveReviews);
        Assert.Equal(4, snapshot.OutstandingReviews);
        Assert.Equal(2, snapshot.CompletedReviewsInRateWindow);
        Assert.Equal(2, snapshot.DrainRatePerHour);
        Assert.Equal(600d, snapshot.MedianReviewDurationSeconds);
        Assert.Equal(2, snapshot.ReviewDurationSampleCount);
        Assert.Equal(Now.AddMinutes(-60), snapshot.OldestQueuedAt);
        Assert.Equal(Now.AddMinutes(-10), snapshot.LastDrainAt);
        Assert.False(snapshot.IsStagnant);
    }

    [Fact]
    public void Evaluate_AlarmsOnlyWhenWaitingWorkHasNoDrainProgressForTheThreshold()
    {
        var stagnant = AutoReviewQueueTelemetryPolicy.Evaluate(
            Now,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(24),
            TimeSpan.FromMinutes(30),
            [Fact(Now.AddMinutes(-31), open: true)]);
        var activeOnly = AutoReviewQueueTelemetryPolicy.Evaluate(
            Now,
            TimeSpan.FromHours(1),
            TimeSpan.FromHours(24),
            TimeSpan.FromMinutes(30),
            [Fact(
                Now.AddMinutes(-60),
                open: true,
                acquiredAt: Now.AddMinutes(-59),
                expiresAt: Now.AddMinutes(1))]);

        Assert.True(stagnant.IsStagnant);
        Assert.Equal(Now.AddMinutes(-31), stagnant.StagnantSince);
        Assert.False(activeOnly.IsStagnant);
    }

    [Fact]
    public void Watchdog_LogsOneAcuteWarningAndARecovery()
    {
        var logger = new CapturingLogger<AutoReviewQueueTelemetryWatchdog>();
        var watchdog = new AutoReviewQueueTelemetryWatchdog(
            null!,
            new ConfigurationBuilder().Build(),
            logger);
        var healthy = Snapshot(isStagnant: false);
        var stagnant = Snapshot(isStagnant: true);

        watchdog.Publish(healthy, stagnant);
        watchdog.Publish(stagnant, stagnant with { ObservedAt = Now.AddMinutes(1) });
        watchdog.Publish(stagnant, healthy with { ObservedAt = Now.AddMinutes(2) });

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("recovered", StringComparison.Ordinal));
    }

    private static AutoReviewQueueAttemptFact Fact(
        DateTime createdAt,
        bool open = false,
        DateTime? acquiredAt = null,
        DateTime? expiresAt = null,
        DateTime? reportedAt = null,
        bool processUnknown = false,
        bool? countsAsDrain = null,
        bool superseded = false)
        => new(
            createdAt,
            acquiredAt,
            expiresAt,
            reportedAt,
            open,
            processUnknown,
            countsAsDrain ?? (reportedAt is not null && !superseded),
            superseded);

    private static AutoReviewQueueTelemetrySnapshot Snapshot(bool isStagnant)
        => new(
            QueueDepth: isStagnant ? 4 : 0,
            ActiveReviews: 1,
            OutstandingReviews: isStagnant ? 5 : 1,
            CompletedReviewsInRateWindow: 0,
            DrainRatePerHour: 0,
            MedianReviewDurationSeconds: 600,
            ReviewDurationSampleCount: 1,
            OldestQueuedAt: isStagnant ? Now.AddMinutes(-40) : null,
            LastDrainAt: Now.AddHours(-1),
            ObservedAt: Now,
            RateWindowMinutes: 60,
            DurationWindowMinutes: 1440,
            StagnantThresholdMinutes: 30,
            IsStagnant: isStagnant,
            StagnantSince: isStagnant ? Now.AddMinutes(-40) : null);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
