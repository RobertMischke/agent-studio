namespace AgentStudio.Pipeline;

/// <summary>
/// Durable safety net for operator acceptance integration. The normal
/// HumanReview-to-Completed transition enqueues the merge after the lane move is
/// durable. A backend restart before the volatile queue drains must not leave a
/// local <c>task/&lt;id&gt;</c> delivery or a fenced remote delivery permanently
/// unintegrated. Legacy remote <c>no-branch</c> outcomes are replayed against
/// their <c>review-subject.json</c>.
/// </summary>
public sealed class AcceptedIntegrationBackstopHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskMutationService _mutations;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AcceptedIntegrationBackstopHostedService> _logger;

    public AcceptedIntegrationBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        MergeIntoDevelopRunner runner,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<AcceptedIntegrationBackstopHostedService> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _runner = runner;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _configuration = configuration;
        _logger = logger;
    }

    public int RunOnce()
    {
        var acceptedJobs = _scanner.ScanAllJobsWithArchive()
            .Where(job => job.State is TaskStates.Completed or TaskStates.Archive)
            .Where(job => !TaskModes.IsReadOnly(job.Mode))
            .OrderBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (acceptedJobs.Count == 0) return 0;

        var statusByKey = _integrationStatus.BuildLookup(acceptedJobs);
        var integrated = 0;
        foreach (var job in acceptedJobs)
        {
            try
            {
                if (!statusByKey.TryGetValue(job.TaskKey, out var status))
                {
                    _logger.LogWarning(
                        "accepted-integration-backstop has no status for project={Project} job={JobId}",
                        job.ProjectName,
                        job.Id);
                    continue;
                }

                var decision = _integrationStatus.ResolveAcceptedIntegrationRecovery(job, status);
                if (decision.Action == AcceptedIntegrationRecoveryAction.None)
                    continue;
                if (decision.Action == AcceptedIntegrationRecoveryAction.ClearPendingMarker)
                {
                    ClearPendingTag(job);
                    continue;
                }

                var settings = _settings.Get(job.ProjectName);
                var result = _runner.Run(
                    job.ProjectName,
                    job.Id,
                    job.FolderPath,
                    job.WatchPath,
                    TaskIntegrationBranch.Resolve(job, settings.IntegrationBranch),
                    settings.IntegrationStrategy);
                if (result.Outcome is MergeIntoIntegrationOutcome.Merged or MergeIntoIntegrationOutcome.AlreadyMerged)
                {
                    ClearPendingTag(job);
                    integrated++;
                }
                else if (result.Outcome == MergeIntoIntegrationOutcome.Error)
                {
                    _logger.LogError(
                        "accepted-integration-backstop integration failed project={Project} job={JobId} error={Error}",
                        job.ProjectName,
                        job.Id,
                        result.Error ?? "unknown error");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "accepted-integration-backstop item failed project={Project} job={JobId}",
                    job.ProjectName,
                    job.Id);
            }
        }

        if (integrated > 0)
        {
            _logger.LogInformation(
                "accepted-integration-backstop integrated {Count} accepted delivery(s)",
                integrated);
        }
        return integrated;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Clamp(
            _configuration.GetValue<int?>("Integration:BackstopIntervalMinutes") ?? 15,
            1,
            24 * 60));
        using var timer = new PeriodicTimer(interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RunOnce();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Accepted integration backstop sweep failed");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void ClearPendingTag(TaskInfo job)
    {
        var tags = (job.Tags ?? [])
            .Where(tag => !IntegrationStatuses.IsPendingTag(tag))
            .ToList();
        if (tags.Count == (job.Tags?.Count ?? 0)) return;
        _mutations.SetJobTags(job.Id, tags, job.WatchPath);
    }
}
