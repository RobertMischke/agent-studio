using AgentStudio.TaskServer.Contracts;
using AuthorityReviewAttemptDto = AgentStudio.Shared.ReviewAttemptDto;

namespace AgentStudio.Runner;

/// <summary>
/// Compatibility-owner queue metrics. The standalone Task Server owns the
/// same route in networked mode, so callers see one contract in both profiles.
/// </summary>
public sealed class ReviewQueueTelemetryWatchdog : BackgroundService
{
    public const int DefaultIntervalSeconds = 30;
    public const int DefaultDrainWindowMinutes = 60;
    public const int DefaultDurationWindowHours = 24;
    public const int DefaultStagnationThresholdMinutes = 30;

    private readonly TaskScannerService _scanner;
    private readonly AttemptAuthorityService _authority;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewQueueTelemetryWatchdog> _logger;
    private readonly object _gate = new();
    private ReviewQueueTelemetryDto _current = Empty();
    private bool _wasStagnant;
    private DateTime? _lastWarningAt;

    public ReviewQueueTelemetryWatchdog(
        TaskScannerService scanner,
        AttemptAuthorityService authority,
        IConfiguration configuration,
        ILogger<ReviewQueueTelemetryWatchdog> logger)
    {
        _scanner = scanner;
        _authority = authority;
        _configuration = configuration;
        _logger = logger;
    }

    public ReviewQueueTelemetryDto Current
    {
        get { lock (_gate) return _current; }
    }

    public ReviewQueueTelemetryDto Refresh(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var tasks = _scanner.ScanAllAutomationJobs()
            .Where(task => string.Equals(
                task.State,
                TaskStates.AutoReview,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        var taskKeys = tasks
            .Select(TaskKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var attempts = _authority.ListReviewAttempts();
        var currentByTask = attempts
            .Where(attempt => taskKeys.Contains(attempt.TaskKey))
            .GroupBy(attempt => attempt.TaskKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(attempt => attempt.CreatedAt).First(),
                StringComparer.OrdinalIgnoreCase);

        var waiting = new List<(TaskInfo Task, AuthorityReviewAttemptDto? Attempt)>();
        var activeReviews = 0;
        foreach (var task in tasks)
        {
            currentByTask.TryGetValue(TaskKey(task), out var attempt);
            if (attempt is { State: AttemptLifecycleState.Leased, Lease: { } lease }
                && lease.ExpiresAt.ToUniversalTime() > now)
            {
                activeReviews++;
                continue;
            }
            if (attempt is null || !Terminal(attempt.State))
                waiting.Add((task, attempt));
        }

        var completions = attempts
            .Where(attempt => attempt.TerminalAt is not null
                              && attempt.Lease is not null
                              && attempt.Outcome is ReviewTerminalOutcome.Pass
                                  or ReviewTerminalOutcome.ProductFailure
                                  or ReviewTerminalOutcome.Inconclusive)
            .Select(attempt => new ReviewCompletionSampleDto(
                attempt.TerminalAt!.Value.ToUniversalTime(),
                Math.Max(
                    0,
                    (attempt.TerminalAt.Value.ToUniversalTime()
                     - attempt.Lease!.AcquiredAt.ToUniversalTime()).TotalSeconds)))
            .ToList();
        DateTime? lastDrainAt = completions.Count == 0
            ? null
            : completions.Max(sample => sample.CompletedAt);
        DateTime? oldestWaitingAt = waiting.Count == 0
            ? null
            : waiting.Min(item => item.Attempt?.CreatedAt.ToUniversalTime()
                                  ?? item.Task.EnteredLaneAt.ToUniversalTime());
        var next = ReviewQueueTelemetryPolicy.Evaluate(
            now,
            tasks.Count,
            waiting.Count,
            activeReviews,
            oldestWaitingAt,
            lastDrainAt,
            completions,
            TimeSpan.FromMinutes(DrainWindowMinutes()),
            TimeSpan.FromHours(DurationWindowHours()),
            TimeSpan.FromMinutes(StagnationThresholdMinutes()));

        lock (_gate)
        {
            PublishTransition(next);
            _current = next;
            return _current;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshSafely();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds()));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) RefreshSafely();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("auto-review-queue-watchdog-stopped");
        }
    }

    private void RefreshSafely()
    {
        try
        {
            Refresh();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "auto-review-queue-watchdog-failed");
        }
    }

    private void PublishTransition(ReviewQueueTelemetryDto next)
    {
        if (!next.Stagnant)
        {
            if (_wasStagnant)
                _logger.LogInformation(
                    "auto-review-queue-stagnation-recovered queueDepth={QueueDepth} drainRatePerHour={DrainRatePerHour}",
                    next.QueueDepth,
                    next.DrainRatePerHour);
            _wasStagnant = false;
            _lastWarningAt = null;
            return;
        }

        var repeatDue = _lastWarningAt is null
                        || next.ObservedAt - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (_wasStagnant && !repeatDue) return;
        _logger.LogWarning(
            "auto-review-queue-stagnant queueDepth={QueueDepth} waitingDepth={WaitingDepth} activeReviews={ActiveReviews} stagnantForMinutes={StagnantForMinutes} lastDrainAt={LastDrainAt} oldestWaitingAt={OldestWaitingAt}",
            next.QueueDepth,
            next.WaitingDepth,
            next.ActiveReviews,
            next.StagnantForMinutes,
            next.LastDrainAt,
            next.OldestWaitingAt);
        _wasStagnant = true;
        _lastWarningAt = next.ObservedAt;
    }

    private int IntervalSeconds() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:IntervalSeconds")
        ?? DefaultIntervalSeconds,
        5,
        15 * 60);

    private int DrainWindowMinutes() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:DrainWindowMinutes")
        ?? DefaultDrainWindowMinutes,
        5,
        24 * 60);

    private int DurationWindowHours() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:DurationWindowHours")
        ?? DefaultDurationWindowHours,
        1,
        14 * 24);

    private int StagnationThresholdMinutes() => Math.Clamp(
        _configuration.GetValue<int?>("ReviewQueueTelemetry:StagnationThresholdMinutes")
        ?? DefaultStagnationThresholdMinutes,
        5,
        24 * 60);

    private static bool Terminal(AttemptLifecycleState state)
        => state is AttemptLifecycleState.Completed
            or AttemptLifecycleState.Failed
            or AttemptLifecycleState.Cancelled
            or AttemptLifecycleState.Superseded;

    private static string TaskKey(TaskInfo task)
        => !string.IsNullOrWhiteSpace(task.Key)
            ? task.Key
            : !string.IsNullOrWhiteSpace(task.TaskKey)
                ? task.TaskKey
                : task.Id;

    private static ReviewQueueTelemetryDto Empty() => new(
        DateTime.UtcNow,
        0,
        0,
        0,
        0,
        DefaultDrainWindowMinutes,
        null,
        DefaultDurationWindowHours,
        0,
        null,
        null,
        false,
        DefaultStagnationThresholdMinutes,
        0);
}
