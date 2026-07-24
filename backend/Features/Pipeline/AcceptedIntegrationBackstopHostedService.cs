namespace AgentStudio.Pipeline;

/// <summary>
/// Durable safety net for operator acceptance integration. The normal
/// HumanReview-to-Completed transition runs the merge synchronously, but the
/// lane move is already durable when that process window begins. A backend
/// restart in that window, or a legacy <c>no-branch</c> result from looking for
/// <c>task/&lt;slug&gt;</c> instead of the fenced remote delivery ref, must not
/// leave an accepted card permanently unintegrated.
/// </summary>
public sealed class AcceptedIntegrationBackstopHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly PipelineExecutionLog _pipeline;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskMutationService _mutations;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AcceptedIntegrationBackstopHostedService> _logger;

    public AcceptedIntegrationBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        MergeIntoDevelopRunner runner,
        PipelineExecutionLog pipeline,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<AcceptedIntegrationBackstopHostedService> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _runner = runner;
        _pipeline = pipeline;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _configuration = configuration;
        _logger = logger;
    }

    public int RunOnce()
    {
        var candidates = _scanner.ScanAllJobsWithArchive()
            .Where(job => job.State is TaskStates.Completed or TaskStates.Archive)
            .Where(job => !TaskModes.IsReadOnly(job.Mode))
            .Where(job => ReviewSubjectStore.Read(job.FolderPath) is not null)
            .Where(RequiresSweep)
            .OrderBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return 0;

        var statusByKey = _integrationStatus.BuildLookup(candidates);
        var integrated = 0;
        foreach (var job in candidates)
        {
            statusByKey.TryGetValue(job.TaskKey, out var status);
            var lastMerge = LastMergeStep(job.FolderPath);
            if (lastMerge?.Status == PipelineStepStatus.Passed)
            {
                ClearPendingTag(job);
                continue;
            }

            // A curated manual rebase can be truthfully integrated without
            // retaining the original delivery SHA. Do not replay that old ref.
            // Conversely, when the exact fenced SHA is already contained but the
            // durable merge step is absent, the backend died after the local
            // merge and before Record()/queue enqueue. Re-run the idempotent
            // runner so it records AlreadyMerged and restores the push fact.
            if (status?.Status == IntegrationStatuses.Integrated
                && !_integrationStatus.IsFencedDeliveryIntegrated(job))
            {
                ClearPendingTag(job);
                continue;
            }

            // A conflict, a deliberate PR hand-off, and a red build gate are all
            // decided states that need a human / a steer round. Replaying them
            // every sweep would only re-merge, re-build and roll back again.
            if (string.Equals(lastMerge?.Verdict, "conflict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lastMerge?.Verdict, "pushed-for-review", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lastMerge?.Verdict, "gate-failed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var settings = _settings.Get(job.ProjectName);
            var result = _runner.Run(
                job.ProjectName,
                job.Id,
                job.FolderPath,
                job.WatchPath,
                settings.IntegrationBranch,
                settings.IntegrationStrategy,
                PipelineTypes.Resolve(job));
            if (result.Outcome is MergeIntoIntegrationOutcome.Merged or MergeIntoIntegrationOutcome.AlreadyMerged)
            {
                ClearPendingTag(job);
                integrated++;
            }
        }

        if (integrated > 0)
        {
            _logger.LogInformation(
                "accepted-integration-backstop integrated {Count} accepted remote delivery(s)",
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

    private PipelineStepExecution? LastMergeStep(string jobFolderPath)
    {
        try
        {
            return _pipeline.Read(jobFolderPath)?.Steps.LastOrDefault(
                step => step.StepId == PipelineCatalogue.MergeIntoDevelopStepId);
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "AcceptedIntegrationBackstop: pipeline read is best-effort");
            return null;
        }
    }

    private bool RequiresSweep(TaskInfo job)
    {
        if ((job.Tags ?? []).Any(IntegrationStatuses.IsPendingTag))
        {
            return true;
        }

        return LastMergeStep(job.FolderPath)?.Status != PipelineStepStatus.Passed;
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
