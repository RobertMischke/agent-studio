namespace AgentStudio.Runner;

public sealed record RemoteQueueStarvationItem
{
    public string TaskKey { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime EnteredLaneAt { get; init; }
    public RemoteDispatchRejection? LastRejection { get; init; }
}

public sealed record RemoteQueueStarvationSnapshot
{
    public bool Active { get; init; }
    public int WaitingTaskCount { get; init; }
    public int AvailableSlots { get; init; }
    public int ThresholdMinutes { get; init; }
    public int ClaimProgressWindowMinutes { get; init; }
    public bool ClaimProgressStalled { get; init; }
    public DateTime? LastSuccessfulClaimAt { get; init; }
    public DateTime? OldestEnteredLaneAt { get; init; }
    public DateTime ObservedAt { get; init; }
    public IReadOnlyList<RemoteQueueStarvationItem> Items { get; init; } = [];
    internal bool TrackMissingClaimProgress { get; init; }
}

/// <summary>Pure policy for the ready-queue starvation watchdog.</summary>
public static class RemoteQueueStarvationPolicy
{
    public static RemoteQueueStarvationSnapshot Evaluate(
        DateTime now,
        TimeSpan threshold,
        TimeSpan claimProgressWindow,
        DateTime? missingClaimProgressObservedAt,
        IEnumerable<TaskInfo> tasks,
        Func<string, ProjectSettings> projectSettings,
        TaskReferenceIndex references,
        IEnumerable<ClientIdentity> runners)
    {
        var liveRunners = runners
            .Where(runner => string.Equals(
                runner.RunnerDaemonState,
                "running",
                StringComparison.OrdinalIgnoreCase))
            .Where(runner => runner.Kind != ClientIdentityKind.Retired)
            .Where(runner => runner.DrainRequestedAt is null)
            .Where(runner => runner.LastSeenAt is { } lastSeen
                             && now - lastSeen.ToUniversalTime() <= TimeSpan.FromMinutes(2))
            .ToList();
        var availableSlots = liveRunners
            .Sum(runner => Math.Max(0, runner.RunnerAvailableSlots ?? 0));
        var lastSuccessfulClaimAt = liveRunners
            .Where(runner => runner.RunnerLastClaimAt is not null)
            .Select(runner => runner.RunnerLastClaimAt!.Value.ToUniversalTime())
            .Cast<DateTime?>()
            .Max();

        var waitingItems = tasks
            .Where(task => task.State == TaskStates.Ready && !task.Fixture)
            .Where(task => now - task.EnteredLaneAt.ToUniversalTime() >= threshold)
            .Where(task =>
            {
                var settings = projectSettings(task.ProjectName);
                return ProjectExecutionPolicy.ResolveExecutionLocation(settings) != ExecutionLocations.Local
                       && ProjectExecutionPolicy.AllowsAutomaticPickup(settings)
                       && AgentTypes.IsAutoPickupEligible(task.Agent)
                       && !TaskSlugs.IsHumanDecisionNeeded(task.Id)
                       && BuildProfileGate.AllowsAutoPickup(settings.BuildProfile)
                       && (!settings.IntakeEnabled.GetValueOrDefault()
                           || task.Phase == LifecyclePhases.IntakePassed)
                       && !references.EvaluateWaitsOn(task).Blocked;
            })
            .OrderBy(task => task.EnteredLaneAt)
            .Select(task => new RemoteQueueStarvationItem
            {
                TaskKey = task.Key ?? task.TaskKey ?? task.Id,
                TaskId = task.Id,
                ProjectName = task.ProjectName,
                Title = task.Title,
                EnteredLaneAt = task.EnteredLaneAt,
                LastRejection = task.RemoteDispatchRejection is { } rejection
                                && rejection.RejectedAtUtc >= task.EnteredLaneAt.ToUniversalTime()
                    ? rejection
                    : null,
            })
            .ToList();
        var trackMissingClaimProgress = lastSuccessfulClaimAt is null
                                        && waitingItems.Count > 0
                                        && availableSlots > 0;
        var claimProgressStalled = waitingItems.Count > 0
                                   && availableSlots > 0
                                   && (lastSuccessfulClaimAt is { } lastClaim
                                       ? now - lastClaim >= claimProgressWindow
                                       : missingClaimProgressObservedAt is { } firstObserved
                                         && now - firstObserved >= claimProgressWindow);
        var items = claimProgressStalled
            ? waitingItems
            : waitingItems.Where(item => item.LastRejection is not null).ToList();

        return new RemoteQueueStarvationSnapshot
        {
            Active = items.Count > 0 && availableSlots > 0,
            WaitingTaskCount = items.Count,
            AvailableSlots = availableSlots,
            ThresholdMinutes = Math.Max(1, (int)Math.Ceiling(threshold.TotalMinutes)),
            ClaimProgressWindowMinutes = Math.Max(
                1,
                (int)Math.Ceiling(claimProgressWindow.TotalMinutes)),
            ClaimProgressStalled = claimProgressStalled,
            LastSuccessfulClaimAt = lastSuccessfulClaimAt,
            OldestEnteredLaneAt = items.FirstOrDefault()?.EnteredLaneAt,
            ObservedAt = now,
            Items = items,
            TrackMissingClaimProgress = trackMissingClaimProgress,
        };
    }
}

/// <summary>
/// Detects remote Ready cards that stop making progress while a live Runner
/// continues to report free capacity. Acute transitions are visible both to
/// the board endpoint and as warning-level structured events.
/// </summary>
public sealed class RemoteQueueStarvationWatchdog : BackgroundService
{
    public const int DefaultThresholdMinutes = 30;
    public const int DefaultClaimProgressWindowMinutes = 5;
    public const int DefaultIntervalSeconds = 30;

    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly ClientIdentityStore _clients;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteQueueStarvationWatchdog> _logger;
    private readonly object _gate = new();
    private RemoteQueueStarvationSnapshot _current = new() { ObservedAt = DateTime.UtcNow };
    private DateTime? _missingClaimProgressObservedAt;
    private string? _warningSignature;
    private DateTime? _lastWarningAt;

    public RemoteQueueStarvationWatchdog(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        ClientIdentityStore clients,
        IConfiguration configuration,
        ILogger<RemoteQueueStarvationWatchdog> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
    }

    public RemoteQueueStarvationSnapshot Current
    {
        get { lock (_gate) return _current; }
    }

    public RemoteQueueStarvationSnapshot Refresh(DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToUniversalTime();
        var thresholdMinutes = Math.Clamp(
            _configuration.GetValue<int?>("RemoteQueueStarvation:ThresholdMinutes")
            ?? DefaultThresholdMinutes,
            1,
            24 * 60);
        var claimProgressWindowMinutes = Math.Clamp(
            _configuration.GetValue<int?>("RemoteQueueStarvation:ClaimProgressWindowMinutes")
            ?? DefaultClaimProgressWindowMinutes,
            1,
            24 * 60);
        var snapshot = _scanner.GetLiveSnapshotWithReferenceIndex();
        var runners = _clients.ListAll();

        lock (_gate)
        {
            var next = RemoteQueueStarvationPolicy.Evaluate(
                now,
                TimeSpan.FromMinutes(thresholdMinutes),
                TimeSpan.FromMinutes(claimProgressWindowMinutes),
                _missingClaimProgressObservedAt,
                snapshot.Live,
                _settings.Get,
                snapshot.References,
                runners);
            _missingClaimProgressObservedAt = next.TrackMissingClaimProgress
                ? _missingClaimProgressObservedAt ?? now
                : null;
            PublishLogTransition(_current, next, now);
            _current = next;
            return _current;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        RefreshSafely();
        var intervalSeconds = Math.Clamp(
            _configuration.GetValue<int?>("RemoteQueueStarvation:IntervalSeconds")
            ?? DefaultIntervalSeconds,
            5,
            15 * 60);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                RefreshSafely();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("remote-ready-starvation-watchdog-stopped");
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
            _logger.LogWarning(ex, "remote-ready-starvation-watchdog-failed");
        }
    }

    internal void PublishLogTransition(
        RemoteQueueStarvationSnapshot previous,
        RemoteQueueStarvationSnapshot next,
        DateTime now)
    {
        if (!next.Active)
        {
            if (previous.Active)
                _logger.LogInformation("remote-ready-starvation-recovered");
            _warningSignature = null;
            _lastWarningAt = null;
            return;
        }

        var signature = string.Join(',', next.Items.Select(item =>
            $"{item.TaskKey}:{item.LastRejection?.Code ?? "stalled"}"));
        var repeatDue = _lastWarningAt is null || now - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (string.Equals(signature, _warningSignature, StringComparison.Ordinal) && !repeatDue)
            return;

        _logger.LogWarning(
            "remote-ready-starvation waitingTasks={WaitingTaskCount} availableSlots={AvailableSlots} oldestEnteredLaneAt={OldestEnteredLaneAt} rejectedTasks={RejectedTaskCount} claimProgressStalled={ClaimProgressStalled} lastSuccessfulClaimAt={LastSuccessfulClaimAt}",
            next.WaitingTaskCount,
            next.AvailableSlots,
            next.OldestEnteredLaneAt,
            next.Items.Count(item => item.LastRejection is not null),
            next.ClaimProgressStalled,
            next.LastSuccessfulClaimAt);
        _warningSignature = signature;
        _lastWarningAt = now;
    }
}

public static class RemoteQueueStarvationEndpoints
{
    public static void MapRemoteQueueStarvationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/runner/queue-starvation", (
            HttpContext context,
            RemoteQueueStarvationWatchdog watchdog,
            ProjectRegistry projects) =>
        {
            var snapshot = watchdog.Refresh();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
                return Results.Ok(snapshot);

            var visibleItems = snapshot.Items
                .Where(item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects))
                .ToList();
            return Results.Ok(snapshot with
            {
                Active = visibleItems.Count > 0 && snapshot.AvailableSlots > 0,
                WaitingTaskCount = visibleItems.Count,
                OldestEnteredLaneAt = visibleItems.FirstOrDefault()?.EnteredLaneAt,
                Items = visibleItems,
            });
        });
    }
}
