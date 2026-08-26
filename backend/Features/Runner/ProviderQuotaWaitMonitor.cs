namespace AgentStudio.Runner;

public sealed record ProviderCapabilityAvailability(
    string CliType,
    string Status,
    bool Fresh,
    string HealthState);

/// <summary>Pure resume gate for durable provider-limit waits.</summary>
public static class ProviderQuotaWaitPolicy
{
    public const int DefaultIntervalSeconds = 15;
    public const int MaxCardsPerTick = 100;

    public static bool CanResume(
        QuotaWaitStatus wait,
        DateTime now,
        bool automaticPickupEnabled,
        IEnumerable<ProviderCapabilityAvailability> capabilities)
        => automaticPickupEnabled
           && wait.ResetAt <= now
           && capabilities.Any(capability =>
               string.Equals(capability.CliType, wait.CliType, StringComparison.OrdinalIgnoreCase)
               && string.Equals(capability.Status, "ready", StringComparison.OrdinalIgnoreCase)
               && capability.Fresh
               && !string.Equals(capability.HealthState, "draining", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Requeues remote cards after their durable provider wait expires and a fresh
/// capability advertisement says the matching CLI is eligible again. Manual
/// project pauses remain manual because automatic pickup must still be enabled.
/// </summary>
public sealed class ProviderQuotaWaitMonitor : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly AgentStudio.Projects.ProjectSettingsService _settings;
    private readonly V1ReviewExecutorRegistry _runners;
    private readonly TaskTransitionService _transitions;
    private readonly ILogger<ProviderQuotaWaitMonitor> _logger;

    public ProviderQuotaWaitMonitor(
        TaskScannerService scanner,
        AgentStudio.Projects.ProjectSettingsService settings,
        V1ReviewExecutorRegistry runners,
        TaskTransitionService transitions,
        ILogger<ProviderQuotaWaitMonitor> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _runners = runners;
        _transitions = transitions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
            ProviderQuotaWaitPolicy.DefaultIntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TickAsync(DateTime.UtcNow, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "provider-quota-wait-monitor-tick-failed; retrying on the next scheduled tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("provider-quota-wait-monitor-stopped");
        }
    }

    internal async Task TickAsync(DateTime now, CancellationToken ct)
    {
        var capabilities = _runners.ListCapabilitySnapshots()
            .SelectMany(snapshot => snapshot.Capabilities)
            .Where(capability => capability.Key.StartsWith("provider-auth:", StringComparison.Ordinal))
            .Select(capability => new ProviderCapabilityAvailability(
                capability.Key["provider-auth:".Length..],
                capability.AdvertisedStatus,
                capability.IsFresh,
                capability.HealthState))
            .ToArray();

        foreach (var task in _scanner.ScanAllAutomationJobs().Where(task =>
                     string.Equals(task.State, TaskStates.Progress, StringComparison.Ordinal)
                     && string.Equals(task.Phase, LifecyclePhases.QuotaWaiting, StringComparison.Ordinal)
                     && string.Equals(task.QuotaWait?.Kind, "provider-limit", StringComparison.Ordinal)
                     && task.QuotaWait is not null)
                 .Take(ProviderQuotaWaitPolicy.MaxCardsPerTick))
        {
            var settings = _settings.Get(task.ProjectName);
            var automatic = ProjectExecutionPolicy.AllowsAutomaticPickup(settings)
                            && !ProjectExecutionPolicy.IsLocalExecution(settings);
            if (!ProviderQuotaWaitPolicy.CanResume(task.QuotaWait!, now, automatic, capabilities))
                continue;

            var move = await _transitions.MoveAsync(
                task.Id,
                TaskStates.Ready,
                task.WatchPath,
                ct,
                cause: $"provider-limit-recovered:{task.QuotaWait!.CliType}",
                suppressProductExecution: true,
                transitionCause: LaneChangeCauses.RunnerRequeue,
                transitionDetail: "provider-limit-recovered");
            if (move.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "provider_limit_resume_refused project={Project} task={TaskKey} cli={Cli} status={Status} reason={Reason}",
                    task.ProjectName,
                    task.Key ?? task.TaskKey ?? task.Id,
                    task.QuotaWait!.CliType,
                    move.Status,
                    move.Message);
                continue;
            }

            QuotaWaitMarker.Clear(move.NewFolderPath ?? task.FolderPath, _logger);
            _logger.LogInformation(
                "provider_limit_resumed project={Project} task={TaskKey} cli={Cli}; card returned to Ready for automatic claim",
                task.ProjectName,
                task.Key ?? task.TaskKey ?? task.Id,
                task.QuotaWait!.CliType);
        }
    }
}
