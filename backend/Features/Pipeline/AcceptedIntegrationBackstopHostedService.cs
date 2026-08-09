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
    private readonly object _alertGate = new();
    private readonly AcceptedIntegrationAlertLogState _alertLog = new();
    private AcceptedIntegrationAlertSnapshot _currentAlert = new()
    {
        ObservedAt = DateTime.UtcNow,
        ThresholdMinutes = 30,
    };

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

    public AcceptedIntegrationAlertSnapshot CurrentAlert
    {
        get { lock (_alertGate) return _currentAlert; }
    }

    public int RunOnce()
    {
        // AGT-2480: the automation scanner excludes fixture cards. Keep the
        // acceptedJobs name from AGT-2428 because it describes the recovery set.
        var acceptedJobs = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(job =>
                job.State is TaskStates.Completed or TaskStates.Archive
                || (job.State == TaskStates.HumanReview
                    && string.Equals(
                        job.Phase,
                        LifecyclePhases.Integrating,
                        StringComparison.Ordinal)))
            .Where(job => !TaskModes.IsReadOnly(job.Mode))
            .OrderBy(job => job.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(DeliveryOrder)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var statusByKey = _integrationStatus.BuildLookup(acceptedJobs);
        var attemptOutcomes = new List<MergeIntoIntegrationOutcome>();
        foreach (var job in acceptedJobs)
        {
            var attemptRecorded = false;
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
                attemptOutcomes.Add(result.Outcome);
                attemptRecorded = true;
                if (result.Outcome is MergeIntoIntegrationOutcome.Merged or MergeIntoIntegrationOutcome.AlreadyMerged)
                {
                    FinalizeTransactionalAccept(job);
                    ClearPendingTag(job);
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
                if (!attemptRecorded)
                    attemptOutcomes.Add(MergeIntoIntegrationOutcome.Error);
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

        var summary = AcceptedIntegrationBackstopPolicy.Summarize(attemptOutcomes);
        AcceptedIntegrationBackstopTelemetry.LogSweep(_logger, summary);
        try
        {
            RefreshAlert(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "accepted-integration-alert-refresh-failed");
        }
        return summary.Integrated;
    }

    internal AcceptedIntegrationAlertSnapshot RefreshAlert(DateTime nowUtc)
    {
        var now = nowUtc.ToUniversalTime();
        var thresholdMinutes = Math.Clamp(
            _configuration.GetValue<int?>("Integration:AcceptedAlertThresholdMinutes") ?? 30,
            1,
            24 * 60);
        var candidates = _scanner.ScanAllAutomationJobsWithArchive()
            .Where(IsAcceptedAlertCandidate)
            .OrderBy(job => job.EnteredLaneAt)
            .ThenBy(job => job.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var statusByKey = _integrationStatus.BuildLookup(candidates);
        var policyCandidates = new List<AcceptedIntegrationAlertCandidate>(candidates.Count);

        foreach (var job in candidates)
        {
            statusByKey.TryGetValue(job.TaskKey, out var status);
            var recovery = _integrationStatus.ResolveAcceptedIntegrationRecovery(job, status);
            policyCandidates.Add(new AcceptedIntegrationAlertCandidate
            {
                Task = job,
                AcceptedAt = ResolveAcceptedAt(job),
                IntegrationStatus = status?.Status,
                LastOutcome = NormalizeOutcome(recovery.LastMergeAttempt?.Verdict),
                Detail = recovery.LastMergeAttempt?.Reason
                         ?? recovery.LastMergeAttempt?.VerdictSummary
                         ?? status?.Detail,
            });
        }

        var next = AcceptedIntegrationBackstopPolicy.EvaluateAlert(
            now,
            TimeSpan.FromMinutes(thresholdMinutes),
            policyCandidates);
        lock (_alertGate)
        {
            _alertLog.Publish(_logger, _currentAlert, next, now);
            _currentAlert = next;
            return _currentAlert;
        }
    }

    private static bool IsAcceptedAlertCandidate(TaskInfo job)
    {
        if (TaskModes.IsReadOnly(job.Mode)) return false;
        if (job.State is TaskStates.Completed or TaskStates.Archive) return true;
        return job.State == TaskStates.HumanReview
               && (string.Equals(job.Phase, LifecyclePhases.Integrating, StringComparison.Ordinal)
                   || (job.Tags ?? []).Any(IntegrationStatuses.IsPendingTag));
    }

    private DateTime ResolveAcceptedAt(TaskInfo job)
    {
        var integrationStarted = _timeline?.ReadAll(job.FolderPath)
            .Where(item => string.Equals(
                item.Kind,
                TimelineEventKinds.IntegrationStarted,
                StringComparison.Ordinal))
            .OrderByDescending(item => item.Ts)
            .FirstOrDefault();
        return integrationStarted?.Ts ?? job.EnteredLaneAt;
    }

    private static string? NormalizeOutcome(string? verdict)
        => verdict?.Trim().ToLowerInvariant() switch
        {
            "merged" => nameof(MergeIntoIntegrationOutcome.Merged),
            "already-merged" or "already-integrated" => nameof(MergeIntoIntegrationOutcome.AlreadyMerged),
            "no-branch" => nameof(MergeIntoIntegrationOutcome.NoTaskBranch),
            "conflict" => nameof(MergeIntoIntegrationOutcome.Conflict),
            "gate-failed" => nameof(MergeIntoIntegrationOutcome.GateFailed),
            "pushed-for-review" => nameof(MergeIntoIntegrationOutcome.PushedForReview),
            "error" => nameof(MergeIntoIntegrationOutcome.Error),
            null or "" => null,
            _ => verdict,
        };

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

    private static DateTimeOffset DeliveryOrder(TaskInfo job)
        => ReviewSubjectStore.Read(job.FolderPath)?.CompletedAtUtc
           ?? (job.EnteredLaneAt == default
               ? DateTimeOffset.MaxValue
               : new DateTimeOffset(DateTime.SpecifyKind(job.EnteredLaneAt, DateTimeKind.Utc)));

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

public static class AcceptedIntegrationBackstopEndpoints
{
    public static void MapAcceptedIntegrationBackstopEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pipeline/accepted-integration-alert", (
            HttpContext context,
            AcceptedIntegrationBackstopHostedService backstop,
            ProjectRegistry projects) =>
        {
            var snapshot = backstop.CurrentAlert;
            if (context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human)
                return Results.Ok(snapshot);

            var visibleItems = snapshot.Items
                .Where(item => ProjectAccessAuthorization.Allows(human.User, item.ProjectName, projects))
                .ToList();
            return Results.Ok(snapshot with
            {
                Active = visibleItems.Count > 0,
                StalledTaskCount = visibleItems.Count,
                OldestAcceptedAt = visibleItems.FirstOrDefault()?.AcceptedAt,
                Items = visibleItems,
            });
        });
    }
}
