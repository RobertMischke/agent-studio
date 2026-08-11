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
            TimeSpan.FromMinutes(5),
            null,
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(availableSlots, runnerAgeMinutes, lastClaimAgeMinutes: 31)]);

        Assert.Equal(expectedActive, snapshot.Active);
        Assert.Equal(expectedAvailableSlots, snapshot.AvailableSlots);
        Assert.Equal(expectedActive ? 1 : 0, snapshot.WaitingTaskCount);
    }

    [Fact]
    public void Evaluate_SuppressesAHealthySerialQueueWhileSuccessfulClaimsContinue()
    {
        var tasks = Enumerable.Range(1, 8)
            .Select(index => ReadyTask(30 + index, $"task-{index}"))
            .ToList();

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            null,
            tasks,
            _ => RemoteSettings(),
            TaskReferenceIndex.Build(tasks),
            [Runner(8, lastClaimAgeMinutes: 1)]);

        Assert.False(snapshot.Active);
        Assert.False(snapshot.ClaimProgressStalled);
        Assert.Equal(Now.AddMinutes(-1), snapshot.LastSuccessfulClaimAt);
        Assert.Empty(snapshot.Items);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void Evaluate_DebouncesAQueueWithoutRejectionsUntilClaimProgressStalls(
        int lastClaimAgeMinutes,
        bool expectedActive)
    {
        var task = ReadyTask(40);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            null,
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2, lastClaimAgeMinutes: lastClaimAgeMinutes)]);

        Assert.Equal(expectedActive, snapshot.Active);
        Assert.Equal(expectedActive, snapshot.ClaimProgressStalled);
        Assert.Equal(expectedActive ? 1 : 0, snapshot.WaitingTaskCount);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void Evaluate_DebouncesAQueueWhenNoSuccessfulClaimHasBeenRecorded(
        int observationAgeMinutes,
        bool expectedActive)
    {
        var task = ReadyTask(40);

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            Now.AddMinutes(-observationAgeMinutes),
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(2)]);

        Assert.Equal(expectedActive, snapshot.Active);
        Assert.Equal(expectedActive, snapshot.ClaimProgressStalled);
        Assert.Null(snapshot.LastSuccessfulClaimAt);
    }

    [Fact]
    public void Evaluate_PreservesPerCardRejectionForUnknownStarvationDiagnosis()
    {
        var rejection = new RemoteDispatchRejection
        {
            Code = "future-admission-rule",
            RunnerId = "runner-01",
            RunnerName = "Runner 01",
            Reason = "future admission rule refused the card",
            RejectedAtUtc = Now.AddMinutes(-4),
        };
        var task = ReadyTask(40) with { RemoteDispatchRejection = rejection };

        var snapshot = RemoteQueueStarvationPolicy.Evaluate(
            Now,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(5),
            null,
            [task],
            _ => RemoteSettings(),
            TaskReferenceIndex.Build([task]),
            [Runner(1, lastClaimAgeMinutes: 1)]);

        Assert.True(snapshot.Active);
        Assert.False(snapshot.ClaimProgressStalled);
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

    private static TaskInfo ReadyTask(int waitingMinutes, string id = "task-1") => new()
    {
        Id = id,
        Key = id.ToUpperInvariant(),
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
        int ageMinutes = 0,
        int? lastClaimAgeMinutes = null) => new()
    {
        Id = "runner-01",
        DisplayName = "Runner 01",
        Kind = ClientIdentityKind.Service,
        LastSeenAt = Now.AddMinutes(-ageMinutes),
        RunnerDaemonState = "running",
        RunnerAvailableSlots = availableSlots,
        RunnerLastClaimAt = lastClaimAgeMinutes is { } claimAge
            ? Now.AddMinutes(-claimAge)
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
