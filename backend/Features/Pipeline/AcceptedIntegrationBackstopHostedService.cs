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
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskMutationService _mutations;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AcceptedIntegrationBackstopHostedService> _logger;
    private readonly TaskTransitionService? _transitions;
    private readonly TimelineLog? _timeline;
    private readonly PipelineExecutionLog? _pipelineLog;

    public AcceptedIntegrationBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        MergeIntoDevelopRunner runner,
        TaskIntegrationStatusService integrationStatus,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<AcceptedIntegrationBackstopHostedService> logger,
        TaskTransitionService? transitions = null,
        TimelineLog? timeline = null,
        PipelineExecutionLog? pipelineLog = null)
    {
        _scanner = scanner;
        _settings = settings;
        _runner = runner;
        _integrationStatus = integrationStatus;
        _mutations = mutations;
        _configuration = configuration;
        _logger = logger;
        _transitions = transitions;
        _timeline = timeline;
        _pipelineLog = pipelineLog;
    }

    public int RunOnce()
    {
        // AGT-2480: Automation-Scanner schliesst Fixture-Karten aus;
        // Variablenname acceptedJobs (AGT-2428) bleibt, damit der Rest der Methode traegt.
        var acceptedJobs = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(job =>
                job.State is TaskStates.Completed or TaskStates.Archive
                || (job.State == TaskStates.HumanReview
                    && string.Equals(
                        job.Phase,
                        LifecyclePhases.Integrating,
                        StringComparison.Ordinal)))
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
                statusByKey.TryGetValue(job.TaskKey, out var status);
                var decision = _integrationStatus.ResolveAcceptedIntegrationRecovery(job, status);
                if (decision.Action == AcceptedIntegrationRecoveryAction.Finalize)
                {
                    FinalizeTransactionalAccept(job);
                    ClearPendingTag(job);
                    continue;
                }
                if (decision.Action == AcceptedIntegrationRecoveryAction.ReturnToReview)
                {
                    ReturnTransactionalAcceptToReview(
                        job,
                        decision.LastMergeAttempt?.Verdict ?? "integration-failed",
                        decision.LastMergeAttempt?.Reason ?? decision.LastMergeAttempt?.VerdictSummary);
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
            catch (Exception ex)
            {
                RecordUnexpectedFailure(job, ex.Message);
                ReturnTransactionalAcceptToReview(
                    job,
                    MergeIntoIntegrationOutcome.Error.ToString(),
                    ex.Message);
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

    private void RecordUnexpectedFailure(TaskInfo job, string detail)
    {
        var now = DateTime.UtcNow;
        _pipelineLog?.RecordStep(job.FolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = "error",
            VerdictSummary = "Accepted integration recovery failed.",
            Reason = detail,
        });
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
