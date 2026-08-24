using Xunit;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Tests;

public sealed class RemoteQueueStarvationPolicyTests
{
    private static readonly DateTime Now = new(2026, 8, 8, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(31, 2, 0, 2, true)]
    [InlineData(30, 2, 0, 2, true)]
    [InlineData(29, 2, 0, 2, false)]
    [InlineData(31, 0, 0, 0, false)]
    [InlineData(31, 2, 3, 0, false)]
    public void Evaluate_RequiresOldRemoteReadyWorkAndFreshFreeCapacity(
        int waitingMinutes,
        int availableSlots,
        int runnerAgeMinutes,
        int expectedAvailableSlots,
        bool expectedActive)
    {
        var task = ReadyTask(waitingMinutes);
        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(availableSlots, runnerAgeMinutes)]);

        Assert.Equal(expectedActive, snapshot.Active);
        Assert.Equal(expectedAvailableSlots, snapshot.AvailableSlots);
        Assert.Equal(waitingMinutes >= 30 ? 1 : 0, snapshot.WaitingTaskCount);
    }

    [Fact]
    public void Evaluate_SuppressesAlarmWhileSerialQueueClaimsKeepProgressing()
    {
        var task = ReadyTask(90);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2, 0, lastClaimMinutesAgo: 1)]);

        Assert.False(snapshot.Active);
        Assert.False(snapshot.ClaimProgressStalled);
        Assert.Equal(Now.AddMinutes(-1), snapshot.LastSuccessfulClaimAt);
        Assert.Empty(snapshot.Items);
    }

    [Theory]
    [InlineData(29, false)]
    [InlineData(30, true)]
    public void Evaluate_DebouncesMissingClaimProgressUntilThreshold(
        int lastClaimMinutesAgo,
        bool expectedActive)
    {
        var task = ReadyTask(90);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2, 0, lastClaimMinutesAgo)]);

        Assert.Equal(expectedActive, snapshot.Active);
        Assert.Equal(expectedActive, snapshot.ClaimProgressStalled);
        Assert.Equal(expectedActive ? 1 : 0, snapshot.WaitingTaskCount);
    }

    [Fact]
    public void Evaluate_ActivatesForRecordedRejectionEvenWhileOtherClaimsProgress()
    {
        var rejection = new RemoteDispatchRejection
        {
            Code = "future-admission-rule",
            RunnerId = "runner-01",
            RunnerName = "Runner 01",
            Reason = "future admission rule refused the card",
            RejectedAtUtc = Now.AddMinutes(-4),
        };
        var task = ReadyTask(4) with { RemoteDispatchRejection = rejection };

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(1, 0, lastClaimMinutesAgo: 1)]);

        Assert.True(snapshot.Active);
        Assert.False(snapshot.ClaimProgressStalled);
        Assert.True(snapshot.HasRejections);
        Assert.Equal(rejection, Assert.Single(snapshot.Items).LastRejection);
    }

    [Fact]
    public void Watchdog_LogsWarningOnceForAnAcuteQueueAndRecoveryWhenItClears()
    {
        var logger = new CapturingLogger();
        var watchdog = new RemoteQueueStarvationWatchdog(
            null!,
            null!,
            null!,
            new ConfigurationBuilder().Build(),
            logger);
        var acute = new RemoteQueueStarvationSnapshot
        {
            Active = true,
            WaitingTaskCount = 1,
            AvailableSlots = 2,
            OldestEnteredLaneAt = Now.AddMinutes(-40),
            ObservedAt = Now,
            Items = [new RemoteQueueStarvationItem { TaskKey = "AGT-1" }],
        };

        watchdog.PublishLogTransition(new RemoteQueueStarvationSnapshot(), acute, Now);
        watchdog.PublishLogTransition(acute, acute, Now.AddMinutes(1));
        watchdog.PublishLogTransition(acute, new RemoteQueueStarvationSnapshot(), Now.AddMinutes(2));

        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Information
            && entry.Message.Contains("recovered", StringComparison.Ordinal));
    }

    /// <summary>
    /// AGT-2680. A card parked by an account-level provider limit is LIMITED,
    /// not STALLED: the alarm must stay quiet, because nothing is broken and no
    /// operator action shortens the wait. Firing it would have meant an alarm
    /// every 30 minutes for the whole 2026-08-23 night.
    /// </summary>
    [Fact]
    public void Evaluate_QuotaWaitingCards_AreReportedAsLimitedNotStarved()
    {
        var task = QuotaWaitingTask(waitingMinutes: 90, resetMinutesFromNow: 45);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2, 0)]);

        Assert.False(snapshot.Active);
        Assert.Equal(0, snapshot.WaitingTaskCount);
        Assert.True(snapshot.ProviderLimited);
        var limit = Assert.Single(snapshot.ProviderLimits);
        Assert.Equal("claude", limit.CliType);
        Assert.Equal(1, limit.WaitingTaskCount);
        Assert.Equal(Now.AddMinutes(45), snapshot.ProviderLimitResetAt);
    }

    /// <summary>
    /// The converse guard: an expired marker must stop excusing the queue, or a
    /// genuinely stuck fleet would hide behind a stale quota reason forever.
    /// </summary>
    [Fact]
    public void Evaluate_ExpiredQuotaWait_NoLongerSuppressesTheAlarm()
    {
        var task = QuotaWaitingTask(waitingMinutes: 90, resetMinutesFromNow: -5);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2, 0)]);

        Assert.True(snapshot.Active);
        Assert.Equal(1, snapshot.WaitingTaskCount);
        Assert.False(snapshot.ProviderLimited);
    }

    /// <summary>
    /// A mixed queue reports both facts at once: the codex card is genuinely
    /// starved and must still raise the alarm, while the claude card is only
    /// waiting for its window.
    /// </summary>
    [Fact]
    public void Evaluate_MixedQueue_SeparatesLimitedFromStarved()
    {
        var limited = QuotaWaitingTask(waitingMinutes: 90, resetMinutesFromNow: 45);
        var starved = ReadyTask(90) with { Id = "task-2", Key = "AGT-2" };
        TaskInfo[] tasks = [limited, starved];

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            tasks,
            _ => RemoteSettings(),
            TaskReferenceIndex.Build(tasks),
            [Runner(2, 0)]);

        Assert.True(snapshot.Active);
        Assert.Equal("task-2", Assert.Single(snapshot.Items).TaskId);
        Assert.True(snapshot.ProviderLimited);
        Assert.Equal(1, Assert.Single(snapshot.ProviderLimits).WaitingTaskCount);
    }

    private static TaskInfo QuotaWaitingTask(int waitingMinutes, int resetMinutesFromNow)
        => ReadyTask(waitingMinutes) with
        {
            QuotaWait = new QuotaWaitStatus(
                "claude",
                Now.AddMinutes(-waitingMinutes),
                Now.AddMinutes(resetMinutesFromNow),
                0,
                "You've hit your session limit"),
        };

    private static TaskInfo ReadyTask(int waitingMinutes) => new()
    {
        Id = "task-1",
        Key = "AGT-1",
        Title = "Waiting task",
        ProjectName = "demo",
        State = TaskStates.Ready,
        Agent = AgentTypes.Codex,
        EnteredLaneAt = Now.AddMinutes(-waitingMinutes),
        CreatedAt = Now.AddHours(-1),
    };

    private static ProjectSettings RemoteSettings() => new()
    {
        PickupMode = PickupModes.Auto,
        ExecutionLocation = "runner-01",
    };

    private static ClientIdentity Runner(
        int availableSlots,
        int ageMinutes,
        int? lastClaimMinutesAgo = null) => new()
    {
        Id = "runner-01",
        DisplayName = "Runner 01",
        Kind = ClientIdentityKind.Service,
        LastSeenAt = Now.AddMinutes(-ageMinutes),
        RunnerDaemonState = "running",
        RunnerAvailableSlots = availableSlots,
        RunnerLastClaimAt = lastClaimMinutesAgo is { } minutes
            ? Now.AddMinutes(-minutes)
            : null,
    };

    private sealed class CapturingLogger : ILogger<RemoteQueueStarvationWatchdog>
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
