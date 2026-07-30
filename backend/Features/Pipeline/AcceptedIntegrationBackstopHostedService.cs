namespace AgentStudio.Pipeline;

/// <summary>
/// Durable safety net for operator acceptance integration. The normal
/// transaction keeps the card in Human Review with phase integrating while the
/// merge runs. A backend restart before the volatile queue drains must resume
/// that transaction and move the task to Completed only after successful
/// integration. Legacy Completed cards and remote <c>no-branch</c> outcomes
/// remain recoverable.
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
    private readonly TaskTransitionService? _transitions;
    private readonly TimelineLog? _timeline;

    public AcceptedIntegrationBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        MergeIntoDevelopRunner runner,
        PipelineExecutionLog pipeline,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<AcceptedIntegrationBackstopHostedService> logger,
        TaskTransitionService? transitions = null,
        TimelineLog? timeline = null)
    {
        _scanner = scanner;
        _settings = settings;
        _runner = runner;
        _pipeline = pipeline;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _configuration = configuration;
        _logger = logger;
        _transitions = transitions;
        _timeline = timeline;
    }

    public int RunOnce()
    {
        var candidates = _scanner.ScanAllJobsWithArchive()
            .Where(job =>
                job.State is TaskStates.Completed or TaskStates.Archive
                || (job.State == TaskStates.HumanReview
                    && string.Equals(
                        job.Phase,
                        LifecyclePhases.Integrating,
                        StringComparison.Ordinal)))
            .Where(job => !TaskModes.IsReadOnly(job.Mode))
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
                FinalizeTransactionalAccept(job);
                ClearPendingTag(job);
                continue;
            }

            // The canonical attributed set can be integrated while a later
            // fenced lifecycle snapshot is not itself a delivery expectation.
            // Conversely, when the exact fenced SHA is already contained but the
            // durable merge step is absent, re-run the idempotent runner so it
            // records AlreadyMerged and restores the push fact.
            if (status?.Status == IntegrationStatuses.Integrated
                && !_integrationStatus.IsFencedDeliveryIntegrated(job))
            {
                FinalizeTransactionalAccept(job);
                ClearPendingTag(job);
                continue;
            }

            // A conflict, a deliberate PR hand-off, and a red build gate are all
            // decided states that need a human / a steer round. Replaying them
            // every sweep would only re-merge, re-build and roll back again.
            if (string.Equals(lastMerge?.Verdict, "conflict", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lastMerge?.Verdict, "pushed-for-review", StringComparison.OrdinalIgnoreCase)
                || string.Equals(lastMerge?.Verdict, "gate-failed", StringComparison.OrdinalIgnoreCase)
                // A local delivery with no task branch is a decided no-op. The
                // legacy replay exception applies only when a fenced remote
                // subject proves that the old lookup used the wrong ref.
                || (string.Equals(lastMerge?.Verdict, "no-branch", StringComparison.OrdinalIgnoreCase)
                    && ReviewSubjectStore.Read(job.FolderPath) is null))
            {
                ReturnTransactionalAcceptToReview(
                    job,
                    lastMerge?.Verdict ?? "integration-failed",
                    lastMerge?.Reason ?? lastMerge?.VerdictSummary);
                continue;
            }

            var settings = _settings.Get(job.ProjectName);
            var result = _runner.Run(
                job.ProjectName,
                job.Id,
                job.FolderPath,
                job.WatchPath,
                TaskIntegrationBranch.Resolve(job, settings.IntegrationBranch),
                settings.IntegrationStrategy,
                PipelineTypes.Resolve(job));
            if (result.Outcome is MergeIntoIntegrationOutcome.Merged or MergeIntoIntegrationOutcome.AlreadyMerged)
            {
                FinalizeTransactionalAccept(job);
                ClearPendingTag(job);
                integrated++;
            }
            else
            {
                ReturnTransactionalAcceptToReview(
                    job,
                    result.Outcome.ToString(),
                    result.Error);
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

    private void FinalizeTransactionalAccept(TaskInfo job)
    {
        if (job.State != TaskStates.HumanReview || _transitions == null) return;
        var moved = _transitions.MoveAsync(
                job.Id,
                TaskStates.Completed,
                job.WatchPath,
                CancellationToken.None,
                cause: TimelineActors.System,
                reason: "Recovered transactional acceptance after integration succeeded.",
                expectedSourceState: TaskStates.HumanReview,
                suppressIntegrationTrigger: true)
            .GetAwaiter()
            .GetResult();
        if (moved.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "Backstop integrated {JobId}, but finalizing Completed failed with {Status}: {Message}",
                job.Id,
                moved.Status,
                moved.Message);
            return;
        }

        var completed = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        _mutations.SetJobPhase(completed.FolderPath, null);
        completed = _scanner.FindJob(job.Id, job.WatchPath) ?? completed;
        _timeline?.Append(
            completed.FolderPath,
            TimelineEventKinds.IntegrationSucceeded,
            TimelineActors.System,
            "Recovered acceptance integration succeeded; task moved to Completed.");
    }

    private void ReturnTransactionalAcceptToReview(
        TaskInfo job,
        string outcome,
        string? detail)
    {
        if (job.State != TaskStates.HumanReview
            || !string.Equals(job.Phase, LifecyclePhases.Integrating, StringComparison.Ordinal))
            return;

        _mutations.SetJobPhase(job.FolderPath, null);
        var reviewed = _scanner.FindJob(job.Id, job.WatchPath) ?? job;
        _timeline?.Append(
            reviewed.FolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"Recovered acceptance integration failed ({outcome}); the task remains in Human Review.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = outcome,
                ["detail"] = detail ?? string.Empty,
            });
    }
}
