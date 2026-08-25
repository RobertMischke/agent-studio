using System.Globalization;

namespace AgentStudio.Runner;

public sealed record RemoteQueueStarvationItem
{
    public string TaskKey { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime EnteredLaneAt { get; init; }
    public RemoteDispatchRejection? LastRejection { get; init; }
    public bool Limited { get; init; }
    public string? LimitedCliType { get; init; }
    public DateTime? LimitedUntil { get; init; }
    public string? LimitReason { get; init; }
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
    public bool Limited { get; init; }
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
        IEnumerable<AgentStudio.TaskServer.Contracts.RunnerCapabilitySnapshotDto>? capabilitySnapshots = null)
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
        var liveRunnerIds = liveRunners.Select(runner => runner.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var limitedProviders = (capabilitySnapshots ?? [])
            .Where(snapshot => liveRunnerIds.Contains(snapshot.RunnerId))
            .SelectMany(snapshot => snapshot.Capabilities)
            .Where(capability => capability.IsFresh
                                 && capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal)
                                 && string.Equals(capability.AdvertisedStatus, "limited", StringComparison.Ordinal))
            .GroupBy(capability => capability.Key["provider-auth:".Length..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => LimitUntil(item) ?? DateTime.MinValue).First(),
                StringComparer.OrdinalIgnoreCase);

        var eligibleTasks = tasks
            .Where(task => task.State == TaskStates.Ready && !task.Fixture)
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
            .ToList();
        var oldestEligibleAt = eligibleTasks
            .Select(task => task.EnteredLaneAt.ToUniversalTime())
            .OrderBy(enteredAt => enteredAt)
            .Cast<DateTime?>()
            .FirstOrDefault();
        var progressReferenceAt = lastSuccessfulClaimAt ?? oldestEligibleAt;
        var claimProgressStalled = progressReferenceAt is { } referenceAt
                                   && now - referenceAt >= threshold;

        var items = eligibleTasks
            .Select(task =>
            {
                var cliType = CliTypes.Normalize(task.CliType);
                limitedProviders.TryGetValue(cliType, out var limited);
                return (Task: task, CliType: cliType, Limit: limited);
            })
            .Where(candidate => candidate.Limit is not null
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
                Limited = candidate.Limit is not null,
                LimitedCliType = candidate.Limit is null ? null : candidate.CliType,
                LimitedUntil = candidate.Limit is null ? null : LimitUntil(candidate.Limit),
                LimitReason = candidate.Limit?.Detail,
            })
            .ToList();
        var hasRejections = items.Any(item => item.LastRejection is not null);
        var limited = items.Any(item => item.Limited);
        var stalled = claimProgressStalled && items.Any(item => !item.Limited);

        return new RemoteQueueStarvationSnapshot
        {
            Active = items.Count > 0 && availableSlots > 0,
            WaitingTaskCount = items.Count,
            AvailableSlots = availableSlots,
            ThresholdMinutes = Math.Max(1, (int)Math.Ceiling(threshold.TotalMinutes)),
            ClaimProgressStalled = stalled,
            LastSuccessfulClaimAt = lastSuccessfulClaimAt,
            HasRejections = hasRejections,
            Limited = limited,
            OldestEnteredLaneAt = items.FirstOrDefault()?.EnteredLaneAt,
            ObservedAt = now,
            Items = items,
        };
    }

    private static DateTime? LimitUntil(AgentStudio.TaskServer.Contracts.CapabilityHealthDto capability)
    {
        var detail = capability.Detail;
        if (string.IsNullOrWhiteSpace(detail)) return capability.CooldownUntil;
        const string marker = "limited until ";
        var index = detail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return capability.CooldownUntil;
        var value = detail[(index + marker.Length)..].Split(' ', 2)[0].TrimEnd('.');
        return DateTime.TryParse(value, null, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime()
            : capability.CooldownUntil;
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
    private readonly IConfiguration _configuration;
    private readonly ILogger<RemoteQueueStarvationWatchdog> _logger;
    private readonly V1ReviewExecutorRegistry? _capabilityRegistry;
    private readonly object _gate = new();
    private RemoteQueueStarvationSnapshot _current = new() { ObservedAt = DateTime.UtcNow };
    private string? _warningSignature;
    private DateTime? _lastWarningAt;

    public RemoteQueueStarvationWatchdog(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        ClientIdentityStore clients,
        IConfiguration configuration,
        ILogger<RemoteQueueStarvationWatchdog> logger,
        V1ReviewExecutorRegistry? capabilityRegistry = null)
    {
        _scanner = scanner;
        _settings = settings;
        _clients = clients;
        _configuration = configuration;
        _logger = logger;
        _capabilityRegistry = capabilityRegistry;
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
        var next = RemoteQueueStarvationPolicy.Evaluate(
            now,
            TimeSpan.FromMinutes(thresholdMinutes),
            snapshot.Live,
            _settings.Get,
            snapshot.References,
            _clients.ListAll(),
            _capabilityRegistry?.ListCapabilitySnapshots());

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
                HasRejections = visibleItems.Any(item => item.LastRejection is not null),
                Limited = visibleItems.Any(item => item.Limited),
                OldestEnteredLaneAt = visibleItems.FirstOrDefault()?.EnteredLaneAt,
                Items = visibleItems,
            });
        });
    }
}
