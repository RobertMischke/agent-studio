namespace AgentStudio.Pipeline;

public sealed record AcceptanceRailOptions(
    bool Enabled,
    int IntervalMinutes,
    int MaxRequeues,
    IReadOnlySet<string> HoldList)
{
    public const int DefaultIntervalMinutes = 3;
    public const int DefaultMaxRequeues = 5;

    public static AcceptanceRailOptions Resolve(IConfiguration configuration)
    {
        var holds = configuration.GetSection("AcceptanceRail:HoldList")
            .GetChildren()
            .Select(child => child.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new AcceptanceRailOptions(
            configuration.GetValue<bool?>("AcceptanceRail:Enabled") ?? true,
            Math.Clamp(
                configuration.GetValue<int?>("AcceptanceRail:IntervalMinutes")
                ?? DefaultIntervalMinutes,
                1,
                24 * 60),
            Math.Clamp(
                configuration.GetValue<int?>("AcceptanceRail:MaxRequeues")
                ?? DefaultMaxRequeues,
                1,
                100),
            holds);
    }
}

public enum AcceptanceRailAction
{
    None,
    Hold,
    ConceptHold,
    Accept,
    Requeue,
    Escalate,
}

public sealed record AcceptanceRailPolicyInput(
    string Lane,
    string Mode,
    string? IntegrationStatus,
    bool RebaseRecoveryAvailable,
    bool OperatorHeld,
    int ConflictRequeues,
    int MaxRequeues,
    bool BudgetEscalated);

public sealed record AcceptanceRailDecision(
    AcceptanceRailAction Action,
    string Reason);

/// <summary>
/// Pure lifecycle policy for the deterministic rail. Its closed decisions are
/// matrix-tested independently from filesystem, Git, and hosted-loop effects.
/// </summary>
public static class AcceptanceRailPolicy
{
    public static AcceptanceRailDecision Decide(AcceptanceRailPolicyInput input)
    {
        if (TaskModes.IsConcept(input.Mode))
        {
            return new AcceptanceRailDecision(
                AcceptanceRailAction.ConceptHold,
                "Concept cards require explicit human sight review.");
        }
        if (input.OperatorHeld)
        {
            return new AcceptanceRailDecision(
                AcceptanceRailAction.Hold,
                "The card carries an operator hold.");
        }
        if (input.Lane == TaskStates.HumanReview
            && input.IntegrationStatus == IntegrationStatuses.Integrated)
        {
            return new AcceptanceRailDecision(
                AcceptanceRailAction.Accept,
                "Git proves the current reviewed delivery is integrated.");
        }
        if (input.Lane is TaskStates.HumanReview or TaskStates.Escalated
            && input.IntegrationStatus == IntegrationStatuses.ConflictSkipped
            && input.RebaseRecoveryAvailable)
        {
            if (input.BudgetEscalated)
            {
                return new AcceptanceRailDecision(
                    AcceptanceRailAction.None,
                    "The bounded conflict budget was already escalated.");
            }
            if (input.ConflictRequeues >= Math.Max(1, input.MaxRequeues))
            {
                return new AcceptanceRailDecision(
                    AcceptanceRailAction.Escalate,
                    "The bounded conflict requeue budget is exhausted.");
            }
            return new AcceptanceRailDecision(
                AcceptanceRailAction.Requeue,
                "The typed integration failure is recoverable by a rebase steer round.");
        }
        return new AcceptanceRailDecision(
            AcceptanceRailAction.None,
            "No deterministic acceptance-rail action applies.");
    }
}

public sealed record AcceptanceRailLaneDepthMetric(
    int HumanReview,
    int Escalated,
    int Total);

public sealed record AcceptanceRailStatusSnapshot
{
    public bool Enabled { get; init; }
    public int IntervalMinutes { get; init; }
    public int MaxRequeues { get; init; }
    public DateTime? LastRunStartedAtUtc { get; init; }
    public DateTime? LastRunCompletedAtUtc { get; init; }
    public DateTime? LastActionAtUtc { get; init; }
    public AcceptanceRailLaneDepthMetric LaneDepth { get; init; } = new(0, 0, 0);
    public int Accepted { get; init; }
    public int Requeued { get; init; }
    public int Escalated { get; init; }
    public int Held { get; init; }
    public int ConceptHeld { get; init; }
    public int Unchanged { get; init; }
    public int Failed { get; init; }
}

/// <summary>
/// Default-on, model-free platform rail that drains deterministic terminals
/// from Human Review even when no session-bound orchestrator loop exists.
/// </summary>
public sealed class AcceptanceRailHostedService : BackgroundService
{
    public const string HoldTag = "orchestrator-hold";

    private readonly TaskScannerService _scanner;
    private readonly TaskIntegrationStatusService _integrationStatus;
    private readonly TaskTransitionService _transitions;
    private readonly TaskIntegrationRecoveryService _recovery;
    private readonly HumanReviewEscalation _humanReviewEscalation;
    private readonly TimelineLog _timeline;
    private readonly AcceptanceRailOptions _options;
    private readonly ILogger<AcceptanceRailHostedService> _logger;
    private readonly object _statusGate = new();
    private AcceptanceRailStatusSnapshot _currentStatus;

    public AcceptanceRailHostedService(
        TaskScannerService scanner,
        TaskIntegrationStatusService integrationStatus,
        TaskTransitionService transitions,
        TaskIntegrationRecoveryService recovery,
        HumanReviewEscalation humanReviewEscalation,
        TimelineLog timeline,
        IConfiguration configuration,
        ILogger<AcceptanceRailHostedService> logger)
    {
        _scanner = scanner;
        _integrationStatus = integrationStatus;
        _transitions = transitions;
        _recovery = recovery;
        _humanReviewEscalation = humanReviewEscalation;
        _timeline = timeline;
        _options = AcceptanceRailOptions.Resolve(configuration);
        _logger = logger;
        _currentStatus = new AcceptanceRailStatusSnapshot
        {
            Enabled = _options.Enabled,
            IntervalMinutes = _options.IntervalMinutes,
            MaxRequeues = _options.MaxRequeues,
        };
    }

    public AcceptanceRailStatusSnapshot CurrentStatus
    {
        get { lock (_statusGate) return _currentStatus; }
    }

    public async Task<AcceptanceRailStatusSnapshot> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTime.UtcNow;
        var candidates = _scanner.ScanAllAutomationJobs()
            .Where(task => task.State is TaskStates.HumanReview or TaskStates.Escalated)
            .OrderBy(task => task.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(task => task.EnteredLaneAt)
            .ThenBy(task => task.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var humanReviewDepth = candidates.Count(task => task.State == TaskStates.HumanReview);
        var escalatedDepth = candidates.Count - humanReviewDepth;
        var statusInputs = candidates.Select(task => task.State == TaskStates.Escalated
                ? task with { State = TaskStates.HumanReview }
                : task)
            .ToList();
        var statusByKey = _integrationStatus.BuildLookup(statusInputs);
        var accepted = 0;
        var requeued = 0;
        var escalated = 0;
        var held = 0;
        var conceptHeld = 0;
        var unchanged = 0;
        var failed = 0;
        DateTime? lastActionAt = null;

        foreach (var task in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                statusByKey.TryGetValue(task.TaskKey, out var integration);
                var retryCount = _recovery.GetConflictRequeueCount(task);
                var decision = AcceptanceRailPolicy.Decide(new AcceptanceRailPolicyInput(
                    task.State,
                    task.Mode,
                    integration?.Status,
                    integration?.Failure?.RebaseRecoveryAvailable == true,
                    IsOperatorHeld(task),
                    retryCount,
                    _options.MaxRequeues,
                    _recovery.IsBudgetEscalated(task)));

                switch (decision.Action)
                {
                    case AcceptanceRailAction.Accept:
                        if (await AcceptAsync(task, cancellationToken))
                        {
                            accepted++;
                            lastActionAt = DateTime.UtcNow;
                        }
                        else
                        {
                            failed++;
                        }
                        break;

                    case AcceptanceRailAction.Requeue:
                    {
                        var result = await _recovery.TryQueueAsync(
                            new TaskIntegrationRecoveryRequest(
                                task.Id,
                                task.WatchPath,
                                Automatic: true,
                                MaxRequeues: _options.MaxRequeues,
                                Source: "acceptance-rail"),
                            cancellationToken);
                        if (result.Queued)
                        {
                            requeued++;
                            lastActionAt = DateTime.UtcNow;
                        }
                        else if (result.Status == TaskIntegrationRecoveryStatus.BudgetExhausted
                                 && await EscalateBudgetAsync(task, result.ConflictRequeues, cancellationToken))
                        {
                            escalated++;
                            lastActionAt = DateTime.UtcNow;
                        }
                        else
                        {
                            failed++;
                            _logger.LogWarning(
                                "acceptance-rail-requeue-failed project={Project} job={JobId} status={Status} message={Message}",
                                task.ProjectName,
                                task.Id,
                                result.Status,
                                result.Message);
                        }
                        break;
                    }

                    case AcceptanceRailAction.Escalate:
                        if (await EscalateBudgetAsync(task, retryCount, cancellationToken))
                        {
                            escalated++;
                            lastActionAt = DateTime.UtcNow;
                        }
                        else
                        {
                            failed++;
                        }
                        break;

                    case AcceptanceRailAction.Hold:
                        held++;
                        break;

                    case AcceptanceRailAction.ConceptHold:
                        conceptHeld++;
                        break;

                    default:
                        unchanged++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(
                    ex,
                    "acceptance-rail-item-failed project={Project} job={JobId} lane={Lane}",
                    task.ProjectName,
                    task.Id,
                    task.State);
            }
        }

        var snapshot = new AcceptanceRailStatusSnapshot
        {
            Enabled = _options.Enabled,
            IntervalMinutes = _options.IntervalMinutes,
            MaxRequeues = _options.MaxRequeues,
            LastRunStartedAtUtc = startedAt,
            LastRunCompletedAtUtc = DateTime.UtcNow,
            LastActionAtUtc = lastActionAt ?? CurrentStatus.LastActionAtUtc,
            LaneDepth = new AcceptanceRailLaneDepthMetric(
                humanReviewDepth,
                escalatedDepth,
                candidates.Count),
            Accepted = accepted,
            Requeued = requeued,
            Escalated = escalated,
            Held = held,
            ConceptHeld = conceptHeld,
            Unchanged = unchanged,
            Failed = failed,
        };
        lock (_statusGate) _currentStatus = snapshot;
        _logger.LogInformation(
            "acceptance-rail-sweep humanReviewDepth={HumanReviewDepth} escalatedDepth={EscalatedDepth} accepted={Accepted} requeued={Requeued} escalated={Escalated} held={Held} conceptHeld={ConceptHeld} unchanged={Unchanged} failed={Failed} lastRun={LastRun:o}",
            humanReviewDepth,
            escalatedDepth,
            accepted,
            requeued,
            escalated,
            held,
            conceptHeld,
            unchanged,
            failed,
            snapshot.LastRunCompletedAtUtc);
        return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        if (!_options.Enabled)
        {
            _logger.LogInformation("acceptance-rail-disabled");
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "acceptance-rail-sweep-failed");
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

    private async Task<bool> AcceptAsync(TaskInfo task, CancellationToken cancellationToken)
    {
        var outcome = await _transitions.MoveAsync(
            task.Id,
            TaskStates.Completed,
            task.WatchPath,
            cancellationToken,
            cause: TimelineActors.System,
            reason: "Acceptance rail accepted the Git-proven integrated delivery.",
            expectedSourceState: TaskStates.HumanReview,
            transitionCause: LaneChangeCauses.Accepted,
            transitionDetail: "acceptance-rail");
        if (outcome.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "acceptance-rail-accept-refused project={Project} job={JobId} status={Status} message={Message}",
                task.ProjectName,
                task.Id,
                outcome.Status,
                outcome.Message);
            return false;
        }

        var completed = _scanner.FindJob(task.Id, task.WatchPath)
                        ?? task with
                        {
                            State = TaskStates.Completed,
                            FolderPath = outcome.NewFolderPath ?? task.FolderPath,
                        };
        RecordAction(
            completed,
            "accepted",
            "Acceptance rail moved the Git-proven integrated delivery to Completed.",
            new Dictionary<string, string>
            {
                ["sourceLane"] = TaskStates.HumanReview,
                ["targetLane"] = TaskStates.Completed,
                ["integrationStatus"] = IntegrationStatuses.Integrated,
            });
        return true;
    }

    private async Task<bool> EscalateBudgetAsync(
        TaskInfo task,
        int conflictRequeues,
        CancellationToken cancellationToken)
    {
        var reason = $"Automatic integration recovery exhausted the configured limit of {_options.MaxRequeues} conflict requeues. The task remains for an operator decision instead of looping again.";
        TaskInfo escalatedTask;
        if (task.State == TaskStates.HumanReview)
        {
            var outcome = await _humanReviewEscalation.EscalateAsync(
                task.Id,
                task.WatchPath,
                task.ProjectName,
                HumanReviewEscalationCategories.IntegrationRecoveryExhausted,
                reason,
                cancellationToken);
            if (outcome.Status != MoveJobStatus.Success) return false;
            escalatedTask = _scanner.FindJob(task.Id, task.WatchPath)
                            ?? task with
                            {
                                State = TaskStates.Escalated,
                                FolderPath = outcome.NewFolderPath ?? task.FolderPath,
                            };
        }
        else
        {
            _humanReviewEscalation.RecordVerdictAndStatus(
                task.ProjectName,
                task.Id,
                task.FolderPath,
                HumanReviewEscalationCategories.IntegrationRecoveryExhausted,
                reason);
            escalatedTask = task;
        }

        _recovery.MarkBudgetEscalated(escalatedTask, conflictRequeues);
        RecordAction(
            escalatedTask,
            "escalated",
            reason,
            new Dictionary<string, string>
            {
                ["conflictRequeues"] = conflictRequeues.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["maxRequeues"] = _options.MaxRequeues.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["category"] = HumanReviewEscalationCategories.IntegrationRecoveryExhausted,
            });
        return true;
    }

    private bool IsOperatorHeld(TaskInfo task)
    {
        if (TaskSlugs.IsHumanDecisionNeeded(task.Id)) return true;
        if (task.Tags.Any(tag =>
                string.Equals(tag, HoldTag, StringComparison.OrdinalIgnoreCase)
                || string.Equals(tag, HumanReviewEscalationCategories.HumanDecisionNeeded, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (_options.HoldList.Contains(task.Id)
            || _options.HoldList.Contains(task.TaskKey)
            || (!string.IsNullOrWhiteSpace(task.Key) && _options.HoldList.Contains(task.Key))
            || _options.HoldList.Contains(task.ProjectName + "/" + (task.Key ?? task.Id)))
        {
            return true;
        }
        return task.State == TaskStates.Escalated
               && task.ParkedBlocker?.BlockerType is ParkedBlockerCatalog.OperatorDecision
                   or HumanReviewEscalationCategories.HumanDecisionNeeded;
    }

    private void RecordAction(
        TaskInfo task,
        string action,
        string summary,
        Dictionary<string, string> details)
    {
        details["action"] = action;
        details["source"] = "acceptance-rail";
        _timeline.Append(
            task.FolderPath,
            TimelineEventKinds.AcceptanceRailAction,
            TimelineActors.System,
            summary,
            details: details);
        _logger.LogInformation(
            "acceptance-rail-action project={Project} job={JobId} action={Action} lane={Lane}",
            task.ProjectName,
            task.Id,
            action,
            task.State);
    }
}

public static class AcceptanceRailEndpoints
{
    public static void MapAcceptanceRailEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pipeline/acceptance-rail-status", (
            AcceptanceRailHostedService rail) => Results.Ok(rail.CurrentStatus));
    }
}
