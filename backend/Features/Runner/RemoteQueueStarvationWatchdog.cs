namespace AgentStudio.Runner;

public sealed record RemoteQueueStarvationItem
{
    public string TaskKey { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime EnteredLaneAt { get; init; }
    public RemoteDispatchRejection? LastRejection { get; init; }
    public string? BlockReasonCode { get; init; }
    public string? BlockReason { get; init; }
}

public sealed record ProviderLimitVisibility
{
    public string CliType { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? ProjectName { get; init; }
}

public sealed record PickupPauseVisibility
{
    public string ProjectName { get; init; } = "";
    public string Reason { get; init; } = "";
    public DateTime? ChangedAt { get; init; }
}

public sealed record RemoteQueueStarvationSnapshot
{
    public bool Active { get; init; }
    public int WaitingTaskCount { get; init; }
    public int AvailableSlots { get; init; }
    public int ThresholdMinutes { get; init; }
    public bool ClaimProgressStalled { get; init; }
    public DateTime? LastSuccessfulClaimAt { get; init; }
    public bool HasRejections { get; init; }
    public int BuildProfileGateBlockedTaskCount { get; init; }
    public int ProviderLimitedTaskCount { get; init; }
    public string ClaimProgressState { get; init; } = "healthy";
    public IReadOnlyList<ProviderLimitVisibility> LimitedProviders { get; init; } = [];
    public IReadOnlyList<PickupPauseVisibility> PickupPauses { get; init; } = [];
    public DateTime? OldestEnteredLaneAt { get; init; }
    public DateTime ObservedAt { get; init; }
    public IReadOnlyList<RemoteQueueStarvationItem> Items { get; init; } = [];
}

/// <summary>Pure policy for the ready-queue starvation watchdog.</summary>
public static class RemoteQueueStarvationPolicy
{
    public static RemoteQueueStarvationSnapshot Evaluate(
        DateTime now,
        TimeSpan threshold,
        IEnumerable<TaskInfo> tasks,
        Func<string, ProjectSettings> projectSettings,
        TaskReferenceIndex references,
        IEnumerable<ClientIdentity> runners,
        IEnumerable<ProviderLimitVisibility>? providerLimits = null)
    {
        var liveRunners = runners
            .Where(runner => string.Equals(
                runner.RunnerDaemonState,
                "running",
                StringComparison.OrdinalIgnoreCase))
            .Where(runner => runner.Kind != ClientIdentityKind.Retired)
            .Where(runner => runner.LastSeenAt is { } lastSeen
                             && now - lastSeen.ToUniversalTime() <= TimeSpan.FromMinutes(2))
            .ToList();
        var availableSlots = liveRunners
            .Where(runner => runner.DrainRequestedAt is null)
            .Sum(runner => Math.Max(0, runner.RunnerAvailableSlots ?? 0));
        var lastSuccessfulClaimAt = liveRunners
            .Where(runner => runner.RunnerLastClaimAt is not null)
            .Select(runner => runner.RunnerLastClaimAt!.Value.ToUniversalTime())
            .OrderByDescending(claimedAt => claimedAt)
            .Cast<DateTime?>()
            .FirstOrDefault();

        var routedReadyTasks = tasks
            .Where(task => task.State == TaskStates.Ready && !task.Fixture)
            .Where(task =>
            {
                var settings = projectSettings(task.ProjectName);
                return ProjectExecutionPolicy.ResolveExecutionLocation(settings) != ExecutionLocations.Local
                       && ProjectExecutionPolicy.AllowsAutomaticPickup(settings)
                       && AgentTypes.IsAutoPickupEligible(task.Agent)
                       && !TaskSlugs.IsHumanDecisionNeeded(task.Id)
                       && (!settings.IntakeEnabled.GetValueOrDefault()
                           || task.Phase == LifecyclePhases.IntakePassed)
                       && !references.EvaluateWaitsOn(task).Blocked;
            })
            .ToList();
        var oldestEligibleAt = routedReadyTasks
            .Select(task => task.EnteredLaneAt.ToUniversalTime())
            .OrderBy(enteredAt => enteredAt)
            .Cast<DateTime?>()
            .FirstOrDefault();
        var progressReferenceAt = lastSuccessfulClaimAt ?? oldestEligibleAt;
        var claimProgressStalled = progressReferenceAt is { } referenceAt
                                   && now - referenceAt >= threshold;

        var limitedProviders = (providerLimits ?? [])
            .Where(limit => !string.IsNullOrWhiteSpace(limit.CliType))
            .GroupBy(limit => limit.CliType, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var items = routedReadyTasks
            .Select(task =>
            {
                var gate = BuildProfileGate.Evaluate(projectSettings(task.ProjectName).BuildProfile);
                var providerLimit = limitedProviders.FirstOrDefault(limit =>
                    string.Equals(limit.CliType, CliTypes.Normalize(task.CliType), StringComparison.OrdinalIgnoreCase));
                return (Task: task, Gate: gate, ProviderLimit: providerLimit);
            })
            .Where(candidate => candidate.ProviderLimit is not null
                           || !candidate.Gate.AllowsPickup
                           || candidate.Task.RemoteDispatchRejection is not null
                           || (claimProgressStalled
                               && now - candidate.Task.EnteredLaneAt.ToUniversalTime() >= threshold))
            .OrderBy(candidate => candidate.Task.EnteredLaneAt)
            .Select(candidate => new RemoteQueueStarvationItem
            {
                TaskKey = candidate.Task.Key ?? candidate.Task.TaskKey ?? candidate.Task.Id,
                TaskId = candidate.Task.Id,
                ProjectName = candidate.Task.ProjectName,
                Title = candidate.Task.Title,
                EnteredLaneAt = candidate.Task.EnteredLaneAt,
                LastRejection = candidate.Task.RemoteDispatchRejection,
                BlockReasonCode = candidate.ProviderLimit is not null
                    ? "provider-limited"
                    : candidate.Gate.AllowsPickup ? null : "build-profile-gate",
                BlockReason = candidate.ProviderLimit?.Detail
                              ?? (candidate.Gate.AllowsPickup ? null : candidate.Gate.Reason),
            })
            .ToList();
        var hasRejections = items.Any(item => item.LastRejection is not null);
        var providerLimitedTaskCount = items.Count(item =>
            string.Equals(item.BlockReasonCode, "provider-limited", StringComparison.Ordinal));

        return new RemoteQueueStarvationSnapshot
        {
            Active = items.Count > 0
                     && (availableSlots > 0 || items.Any(item =>
                         item.BlockReasonCode is "build-profile-gate" or "provider-limited")),
            WaitingTaskCount = items.Count,
            AvailableSlots = availableSlots,
            ThresholdMinutes = Math.Max(1, (int)Math.Ceiling(threshold.TotalMinutes)),
            ClaimProgressStalled = claimProgressStalled,
            LastSuccessfulClaimAt = lastSuccessfulClaimAt,
            HasRejections = hasRejections,
            BuildProfileGateBlockedTaskCount = items.Count(item =>
                string.Equals(item.BlockReasonCode, "build-profile-gate", StringComparison.Ordinal)),
            ProviderLimitedTaskCount = providerLimitedTaskCount,
            ClaimProgressState = providerLimitedTaskCount > 0
                ? "limited"
                : claimProgressStalled ? "stalled" : "healthy",
            LimitedProviders = limitedProviders,
            OldestEnteredLaneAt = items.FirstOrDefault()?.EnteredLaneAt,
            ObservedAt = now,
            Items = items,
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
    public const int DefaultIntervalSeconds = 30;

    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly ClientIdentityStore _clients;
    private readonly V1ReviewExecutorRegistry _runnerCapabilities;
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteQueueStarvationWatchdog> _logger;
    private readonly object _gate = new();
    private RemoteQueueStarvationSnapshot _current = new() { ObservedAt = DateTime.UtcNow };
    private string? _warningSignature;
    private DateTime? _lastWarningAt;

    public RemoteQueueStarvationWatchdog(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        ClientIdentityStore clients,
        V1ReviewExecutorRegistry runnerCapabilities,
        IConfiguration configuration,
        ILogger<RemoteQueueStarvationWatchdog> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _clients = clients;
        _runnerCapabilities = runnerCapabilities;
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
        var snapshot = _scanner.GetLiveSnapshotWithReferenceIndex();
        var providerLimits = _runnerCapabilities.ListCapabilitySnapshots()
            .SelectMany(snapshot => snapshot.Capabilities)
            .Where(capability => capability.IsFresh
                                 && capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal)
                                 && string.Equals(capability.AdvertisedStatus, "limited", StringComparison.OrdinalIgnoreCase))
            .Select(capability => new ProviderLimitVisibility
            {
                CliType = capability.Key["provider-auth:".Length..],
                Detail = capability.Detail ?? $"{capability.Key}: limited",
            })
            .ToArray();
        var next = RemoteQueueStarvationPolicy.Evaluate(
            now,
            TimeSpan.FromMinutes(thresholdMinutes),
            snapshot.Live,
            _settings.Get,
            snapshot.References,
            _clients.ListAll(),
            providerLimits);

        lock (_gate)
        {
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

        var signature = string.Join(',', next.Items.Select(item => item.TaskKey));
        var repeatDue = _lastWarningAt is null || now - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (string.Equals(signature, _warningSignature, StringComparison.Ordinal) && !repeatDue)
            return;

        _logger.LogWarning(
            "remote-ready-starvation waitingTasks={WaitingTaskCount} availableSlots={AvailableSlots} oldestEnteredLaneAt={OldestEnteredLaneAt} lastSuccessfulClaimAt={LastSuccessfulClaimAt} rejectedTasks={RejectedTaskCount}",
            next.WaitingTaskCount,
            next.AvailableSlots,
            next.OldestEnteredLaneAt,
            next.LastSuccessfulClaimAt,
            next.Items.Count(item => item.LastRejection is not null));
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
            ProjectRegistry projects,
            TaskRunnerService taskRunners) =>
        {
            var snapshot = watchdog.Refresh();
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
                return Results.Ok(snapshot);

            var visibleItems = snapshot.Items
                .Where(item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects))
                .ToList();
            var runnerStatuses = taskRunners.GetStatus().Projects.Values;
            var pickupPauses = runnerStatuses
                .Where(status => status.Mode is "manual" or "paused"
                                 && string.Equals(status.ModeSource, "circuit-breaker", StringComparison.OrdinalIgnoreCase))
                .Where(status => ProjectAccessAuthorization.Allows(human.User, status.ProjectName, projects))
                .Select(status => new PickupPauseVisibility
                {
                    ProjectName = status.ProjectName,
                    Reason = status.ModeReason ?? status.BreakerReason ?? "infra circuit-breaker",
                    ChangedAt = status.ModeChangedAt,
                })
                .ToArray();
            var localProviderLimits = runnerStatuses
                .Where(status => ProjectAccessAuthorization.Allows(human.User, status.ProjectName, projects))
                .SelectMany(status => status.ProviderLimits.Select(limit => new ProviderLimitVisibility
                {
                    CliType = limit.CliType,
                    Detail = limit.Reason,
                    ProjectName = status.ProjectName,
                }))
                .ToArray();
            var visibleProviderLimits = snapshot.LimitedProviders
                .Concat(localProviderLimits)
                .DistinctBy(limit => (limit.ProjectName?.ToLowerInvariant(), limit.CliType.ToLowerInvariant()))
                .ToArray();
            var providerLimitedTaskCount = visibleItems.Count(item =>
                string.Equals(item.BlockReasonCode, "provider-limited", StringComparison.Ordinal));
            return Results.Ok(snapshot with
            {
                Active = pickupPauses.Length > 0 || localProviderLimits.Length > 0 || (visibleItems.Count > 0
                         && (snapshot.AvailableSlots > 0 || visibleItems.Any(item =>
                             item.BlockReasonCode is "build-profile-gate" or "provider-limited"))),
                WaitingTaskCount = visibleItems.Count,
                HasRejections = visibleItems.Any(item => item.LastRejection is not null),
                BuildProfileGateBlockedTaskCount = visibleItems.Count(item =>
                    string.Equals(item.BlockReasonCode, "build-profile-gate", StringComparison.Ordinal)),
                ProviderLimitedTaskCount = providerLimitedTaskCount,
                ClaimProgressState = visibleProviderLimits.Length > 0
                    ? "limited"
                    : snapshot.ClaimProgressStalled ? "stalled" : "healthy",
                LimitedProviders = visibleProviderLimits,
                PickupPauses = pickupPauses,
                OldestEnteredLaneAt = visibleItems.FirstOrDefault()?.EnteredLaneAt,
                Items = visibleItems,
            });
        });
    }
}
