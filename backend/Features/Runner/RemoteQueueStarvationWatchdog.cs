namespace AgentStudio.Runner;

public sealed record RemoteQueueStarvationItem
{
    public string TaskKey { get; init; } = "";
    public string TaskId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime EnteredLaneAt { get; init; }
    public RemoteDispatchRejection? LastRejection { get; init; }

    /// <summary>
    /// True when this card is unclaimable purely because its project's build
    /// profile is not validated (AGT-2677). Such a card is starved from the first
    /// second, so it never has to wait out the stall threshold to be reported.
    /// </summary>
    public bool BuildProfileGateBlocked { get; init; }
}

/// <summary>
/// One project whose build-profile gate is refusing auto-pickup, with the number
/// of ready cards it is holding (AGT-2677). This is what the project and hosts
/// banners render: "N ready cards not claimable: build profile not validated".
/// </summary>
public sealed record BuildProfileGateBlockage
{
    public string ProjectName { get; init; } = "";
    public int ReadyTaskCount { get; init; }

    /// <summary>Stable <see cref="BuildProfileGateCodes"/> value.</summary>
    public string GateCode { get; init; } = "";

    /// <summary>Human-readable gate reason, safe to render verbatim.</summary>
    public string GateReason { get; init; } = "";

    /// <summary>Onboarding status of the profile, for the settings deep link.</summary>
    public string BuildProfileStatus { get; init; } = "";
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
    public DateTime? OldestEnteredLaneAt { get; init; }
    public DateTime ObservedAt { get; init; }
    public IReadOnlyList<RemoteQueueStarvationItem> Items { get; init; } = [];

    /// <summary>Ready cards held by a closed build-profile gate (AGT-2677).</summary>
    public int GateBlockedTaskCount { get; init; }

    /// <summary>Per-project gate blockages behind <see cref="GateBlockedTaskCount"/>.</summary>
    public IReadOnlyList<BuildProfileGateBlockage> GateBlockedProjects { get; init; } = [];
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
        IEnumerable<ClientIdentity> runners)
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

        // AGT-2677: the build-profile gate used to be part of this filter, so a
        // gate-blocked card was dropped here and could never raise the alarm it was
        // the clearest case for. Gate-blocked cards now stay in the population and
        // are separated afterwards.
        var remoteReadyTasks = tasks
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
        var gateBlockedTasks = remoteReadyTasks
            .Where(task => !BuildProfileGate.AllowsAutoPickup(projectSettings(task.ProjectName).BuildProfile))
            .ToList();
        var gateBlockedKeys = gateBlockedTasks
            .Select(task => task.Key ?? task.TaskKey ?? task.Id)
            .ToHashSet(StringComparer.Ordinal);
        var eligibleTasks = remoteReadyTasks
            .Where(task => !gateBlockedKeys.Contains(task.Key ?? task.TaskKey ?? task.Id))
            .ToList();
        var gateBlockedProjects = gateBlockedTasks
            .GroupBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var profile = projectSettings(group.Key).BuildProfile;
                var gate = BuildProfileGate.Evaluate(profile);
                return new BuildProfileGateBlockage
                {
                    ProjectName = group.Key,
                    ReadyTaskCount = group.Count(),
                    GateCode = gate.Code,
                    GateReason = gate.Reason,
                    BuildProfileStatus = BuildProfileStatuses.Normalize(profile?.Status),
                };
            })
            .OrderByDescending(blockage => blockage.ReadyTaskCount)
            .ThenBy(blockage => blockage.ProjectName, StringComparer.OrdinalIgnoreCase)
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
            .Where(task => task.RemoteDispatchRejection is not null
                           || (claimProgressStalled
                               && now - task.EnteredLaneAt.ToUniversalTime() >= threshold))
            .Select(task => ToItem(task, gateBlocked: false))
            // A gate-blocked card is starved by construction: no runner will ever
            // offer it, so waiting out the stall threshold would only delay the
            // alarm by the exact interval that made the outage invisible.
            .Concat(gateBlockedTasks.Select(task => ToItem(task, gateBlocked: true)))
            .OrderBy(item => item.EnteredLaneAt)
            .ToList();
        var hasRejections = items.Any(item => item.LastRejection is not null);

        return new RemoteQueueStarvationSnapshot
        {
            // Free capacity is the evidence that a normal queue is stuck. A closed
            // build-profile gate needs no such evidence - the cards are unclaimable
            // whether or not a slot happens to be free right now.
            Active = items.Count > 0 && (availableSlots > 0 || gateBlockedTasks.Count > 0),
            WaitingTaskCount = items.Count,
            AvailableSlots = availableSlots,
            ThresholdMinutes = Math.Max(1, (int)Math.Ceiling(threshold.TotalMinutes)),
            ClaimProgressStalled = claimProgressStalled,
            LastSuccessfulClaimAt = lastSuccessfulClaimAt,
            HasRejections = hasRejections,
            OldestEnteredLaneAt = items.FirstOrDefault()?.EnteredLaneAt,
            ObservedAt = now,
            Items = items,
            GateBlockedTaskCount = gateBlockedTasks.Count,
            GateBlockedProjects = gateBlockedProjects,
        };
    }

    private static RemoteQueueStarvationItem ToItem(TaskInfo task, bool gateBlocked) =>
        new()
        {
            TaskKey = task.Key ?? task.TaskKey ?? task.Id,
            TaskId = task.Id,
            ProjectName = task.ProjectName,
            Title = task.Title,
            EnteredLaneAt = task.EnteredLaneAt,
            LastRejection = task.RemoteDispatchRejection,
            BuildProfileGateBlocked = gateBlocked,
        };
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
    private readonly object _gate = new();
    private RemoteQueueStarvationSnapshot _current = new() { ObservedAt = DateTime.UtcNow };
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
        var snapshot = _scanner.GetLiveSnapshotWithReferenceIndex();
        var next = RemoteQueueStarvationPolicy.Evaluate(
            now,
            TimeSpan.FromMinutes(thresholdMinutes),
            snapshot.Live,
            _settings.Get,
            snapshot.References,
            _clients.ListAll());

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

        // The gate-blocked set is part of the signature so a project whose gate
        // closes mid-window re-announces instead of hiding behind the debounce.
        var signature = string.Join(',', next.Items.Select(item => item.TaskKey))
                        + "|" + string.Join(',', next.GateBlockedProjects.Select(
                            blockage => $"{blockage.ProjectName}:{blockage.GateCode}:{blockage.ReadyTaskCount}"));
        var repeatDue = _lastWarningAt is null || now - _lastWarningAt >= TimeSpan.FromMinutes(30);
        if (string.Equals(signature, _warningSignature, StringComparison.Ordinal) && !repeatDue)
            return;

        _logger.LogWarning(
            "remote-ready-starvation waitingTasks={WaitingTaskCount} availableSlots={AvailableSlots} oldestEnteredLaneAt={OldestEnteredLaneAt} lastSuccessfulClaimAt={LastSuccessfulClaimAt} rejectedTasks={RejectedTaskCount} buildProfileGateBlocked={GateBlockedTaskCount} gatedProjects={GatedProjects}",
            next.WaitingTaskCount,
            next.AvailableSlots,
            next.OldestEnteredLaneAt,
            next.LastSuccessfulClaimAt,
            next.Items.Count(item => item.LastRejection is not null),
            next.GateBlockedTaskCount,
            string.Join(',', next.GateBlockedProjects.Select(blockage => blockage.ProjectName)));
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
            var visibleBlockages = snapshot.GateBlockedProjects
                .Where(blockage => ProjectAccessAuthorization.Allows(human.User, blockage.ProjectName, projects))
                .ToList();
            var visibleGateBlocked = visibleItems.Count(item => item.BuildProfileGateBlocked);
            return Results.Ok(snapshot with
            {
                Active = visibleItems.Count > 0
                         && (snapshot.AvailableSlots > 0 || visibleGateBlocked > 0),
                WaitingTaskCount = visibleItems.Count,
                HasRejections = visibleItems.Any(item => item.LastRejection is not null),
                OldestEnteredLaneAt = visibleItems.FirstOrDefault()?.EnteredLaneAt,
                Items = visibleItems,
                GateBlockedTaskCount = visibleGateBlocked,
                GateBlockedProjects = visibleBlockages,
            });
        });
    }
}
