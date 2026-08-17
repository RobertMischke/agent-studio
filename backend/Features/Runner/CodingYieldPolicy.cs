namespace AgentStudio.Runner;

public enum CodingYieldAction { Hold, Yield, Restore }

/// <summary>
/// One evaluation of the plane-priority policy: whether the coding plane
/// should give up (or give back) a parallelism slot so the review plane
/// drains first when it is backed up. Task priority inside each plane stays
/// FIFO; this only ever moves the coding-plane ceiling, never reorders
/// tasks. Like <see cref="AdaptiveReviewParallelismDecision"/>, this is a
/// recommendation - applying it means running the AGT-2628 sanctioned
/// <c>sudo agent-runner-deploy config coding RUNNER_MAX_PARALLELISM &lt;value&gt;</c>,
/// which this card does not invoke automatically.
/// </summary>
public sealed record CodingYieldDecision(
    CodingYieldAction Action,
    int RecommendedCodingParallelism,
    string Reason);

public sealed record CodingYieldOptions
{
    /// <summary>Normal coding ceiling when the review plane is not backed up. Restore never overshoots this.</summary>
    public int CodingBaselineParallelism { get; init; } = 4;

    /// <summary>Coding never yields below this floor - some coding throughput always stays available.</summary>
    public int CodingFloorParallelism { get; init; } = 1;

    /// <summary>Review queue depth that starts a yield. Deliberately above <see cref="AdaptiveReviewParallelismOptions.RaiseQueueDepthThreshold"/>: plane priority is a more disruptive lever than scaling review up, so it only engages once scaling review alone has not been enough.</summary>
    public int YieldQueueDepthThreshold { get; init; } = 10;

    /// <summary>Review queue depth that must be reached before a restore is considered - lower than <see cref="YieldQueueDepthThreshold"/> on purpose (Schmitt-trigger hysteresis: entering and leaving the yielded state use different thresholds, so depth hovering near one value cannot flap the plane back and forth).</summary>
    public int RestoreQueueDepthThreshold { get; init; } = 2;

    public TimeSpan YieldCooldown { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Longer than <see cref="YieldCooldown"/>: giving coding capacity back is the flap-prone direction if review backlog is bursty.</summary>
    public TimeSpan RestoreCooldown { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long review depth must stay at or below <see cref="RestoreQueueDepthThreshold"/>, and non-stagnant, before restoring - mirrors <see cref="AdaptiveReviewParallelismOptions.DrainedIdleBeforeLower"/>.</summary>
    public TimeSpan RestoreEligibleFor { get; init; } = TimeSpan.FromMinutes(10);

    public static readonly CodingYieldOptions Default = new();
}

/// <summary>
/// Pure yield/hold/restore decision for whether the coding plane should
/// give up a parallelism slot to let the review plane drain first. FIFO
/// ordering inside each plane is untouched - this only ever proposes moving
/// the coding-plane ceiling by one step, with two-sided hysteresis (distinct
/// enter/exit depth thresholds plus asymmetric cooldowns) so review depth
/// oscillating near one value cannot flap coding capacity back and forth.
/// </summary>
public static class CodingYieldPolicy
{
    public static CodingYieldDecision Evaluate(
        int currentCodingParallelism,
        int reviewQueueDepth,
        bool reviewIsStagnant,
        DateTime nowUtc,
        DateTime? lastChangeAtUtc,
        DateTime? restoreEligibleSinceUtc,
        CodingYieldOptions? options = null)
    {
        var opts = options ?? CodingYieldOptions.Default;
        var current = Math.Clamp(
            currentCodingParallelism, opts.CodingFloorParallelism, opts.CodingBaselineParallelism);
        var sinceLastChange = lastChangeAtUtc is { } last ? nowUtc - last : TimeSpan.MaxValue;

        if ((reviewQueueDepth >= opts.YieldQueueDepthThreshold || reviewIsStagnant)
            && current > opts.CodingFloorParallelism
            && sinceLastChange >= opts.YieldCooldown)
        {
            var target = Math.Max(opts.CodingFloorParallelism, current - 1);
            var reason = reviewIsStagnant
                ? "review queue is stagnant; yielding a coding slot so review drains"
                : $"review queue depth {reviewQueueDepth} at or above the yield threshold ({opts.YieldQueueDepthThreshold})";
            return new CodingYieldDecision(CodingYieldAction.Yield, target, reason);
        }

        var restoreEligible = reviewQueueDepth <= opts.RestoreQueueDepthThreshold && !reviewIsStagnant;
        if (restoreEligible
            && current < opts.CodingBaselineParallelism
            && sinceLastChange >= opts.RestoreCooldown
            && restoreEligibleSinceUtc is { } eligibleSince
            && nowUtc - eligibleSince >= opts.RestoreEligibleFor)
        {
            var target = Math.Min(opts.CodingBaselineParallelism, current + 1);
            var eligibleMinutes = (nowUtc - eligibleSince).TotalMinutes;
            return new CodingYieldDecision(
                CodingYieldAction.Restore,
                target,
                $"review queue depth has stayed at or below {opts.RestoreQueueDepthThreshold} for {eligibleMinutes:F0}m; restoring coding toward baseline ({opts.CodingBaselineParallelism})");
        }

        return new CodingYieldDecision(CodingYieldAction.Hold, current, "within the current band");
    }
}

/// <summary>
/// Tracks how long the review queue has stayed inside the restore band and
/// applies <see cref="CodingYieldPolicy"/> on the same cadence as the review
/// watchdogs. Like <see cref="AdaptiveReviewParallelismAdvisor"/>, it owns
/// its recommendation as internal state seeded at the coding baseline
/// (no live per-host RUNNER_MAX_PARALLELISM signal exists to read from).
/// Registered but exposed as a recommendation-only endpoint; wiring an
/// executor is deliberately a follow-up (see the AGT-2645 results dossier).
/// </summary>
public sealed class CodingYieldAdvisor : BackgroundService
{
    public const int DefaultIntervalSeconds = 30;

    private readonly AutoReviewQueueStagnationWatchdog _reviewQueue;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CodingYieldAdvisor> _logger;
    private readonly object _gate = new();

    private DateTime? _restoreEligibleSince;
    private DateTime? _lastChangeAtUtc;
    private CodingYieldDecision _current;

    public CodingYieldAdvisor(
        AutoReviewQueueStagnationWatchdog reviewQueue,
        IConfiguration configuration,
        ILogger<CodingYieldAdvisor> logger)
    {
        _reviewQueue = reviewQueue;
        _configuration = configuration;
        _logger = logger;
        var baseline = ReadOptions(configuration).CodingBaselineParallelism;
        _current = new CodingYieldDecision(CodingYieldAction.Hold, baseline, "startup baseline");
    }

    public CodingYieldDecision Current
    {
        get { lock (_gate) return _current; }
    }

    public CodingYieldDecision Refresh(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var options = ReadOptions(_configuration);
        var review = _reviewQueue.Current;

        lock (_gate)
        {
            var restoreEligible = review.QueueDepth <= options.RestoreQueueDepthThreshold && !review.IsStagnant;
            _restoreEligibleSince = restoreEligible ? (_restoreEligibleSince ?? now) : null;

            var decision = CodingYieldPolicy.Evaluate(
                _current.RecommendedCodingParallelism,
                review.QueueDepth,
                review.IsStagnant,
                now,
                _lastChangeAtUtc,
                _restoreEligibleSince,
                options);

            if (decision.Action != CodingYieldAction.Hold
                && decision.RecommendedCodingParallelism != _current.RecommendedCodingParallelism)
            {
                _lastChangeAtUtc = now;
                _logger.LogInformation(
                    "coding-yield-recommendation-changed action={Action} from={From} to={To} reason={Reason}",
                    decision.Action, _current.RecommendedCodingParallelism, decision.RecommendedCodingParallelism,
                    decision.Reason);
            }

            _current = decision;
            return _current;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshSafely();
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("CodingYield:IntervalSeconds") ?? DefaultIntervalSeconds,
            5, 15 * 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RefreshSafely();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("coding-yield-advisor-stopped");
        }
    }

    private void RefreshSafely()
    {
        try
        {
            Refresh();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "coding-yield-advisor-failed");
        }
    }

    private static CodingYieldOptions ReadOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("CodingYield");
        var defaults = CodingYieldOptions.Default;
        return new CodingYieldOptions
        {
            CodingBaselineParallelism = Math.Max(1,
                section.GetValue<int?>("CodingBaselineParallelism") ?? defaults.CodingBaselineParallelism),
            CodingFloorParallelism = Math.Max(1,
                section.GetValue<int?>("CodingFloorParallelism") ?? defaults.CodingFloorParallelism),
            YieldQueueDepthThreshold = Math.Max(1,
                section.GetValue<int?>("YieldQueueDepthThreshold") ?? defaults.YieldQueueDepthThreshold),
            RestoreQueueDepthThreshold = Math.Max(0,
                section.GetValue<int?>("RestoreQueueDepthThreshold") ?? defaults.RestoreQueueDepthThreshold),
            YieldCooldown = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("YieldCooldownMinutes") ?? defaults.YieldCooldown.TotalMinutes)),
            RestoreCooldown = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("RestoreCooldownMinutes") ?? defaults.RestoreCooldown.TotalMinutes)),
            RestoreEligibleFor = TimeSpan.FromMinutes(Math.Max(1,
                section.GetValue<double?>("RestoreEligibleForMinutes") ?? defaults.RestoreEligibleFor.TotalMinutes)),
        };
    }
}

public static class CodingYieldEndpoints
{
    public static void MapCodingYieldEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runner/coding-yield-recommendation",
            (CodingYieldAdvisor advisor) => Results.Ok(advisor.Refresh()));
    }
}
