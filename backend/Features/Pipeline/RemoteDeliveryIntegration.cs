using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

public enum RemoteBuildTestGateClass
{
    Passed,
    NotApplicable,
    Failed,
}

public sealed record RemoteDeliveryIntegrationDecision(
    bool ShouldIntegrate,
    RemoteBuildTestGateClass BuildTestGate,
    string Reason);

/// <summary>
/// Pure admission policy for immediate Remote delivery integration. A fenced
/// result is eligible only after its source run persisted a settled immutable
/// envelope, the Remote Review reached Pass, and its build/test verdict is
/// either green or explicitly not applicable.
/// </summary>
public static class RemoteDeliveryIntegrationPolicy
{
    public static RemoteDeliveryIntegrationDecision Decide(
        bool hasSettledResultEnvelope,
        string? reviewOutcome,
        Contract.ReviewPlanDto? reviewPlan,
        IReadOnlyList<Contract.ReviewVerdictDto> verdicts)
    {
        if (!hasSettledResultEnvelope)
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                "The source run has no settled immutable Result-Envelope.");
        }

        if (!string.Equals(reviewOutcome, "Pass", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                $"Remote Review ended with '{reviewOutcome ?? "unknown"}', not Pass.");
        }

        var buildTests = verdicts
            .Where(verdict => string.Equals(
                verdict.Aspect,
                "build-tests",
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (buildTests.Count == 0)
        {
            var buildTestWasPlanned = reviewPlan?.Commands.Any(command => string.Equals(
                                          command.Aspect,
                                          "build-tests",
                                          StringComparison.OrdinalIgnoreCase)) == true
                                      || reviewPlan?.RequiredAspects.Any(aspect => string.Equals(
                                          aspect,
                                          "build-tests",
                                          StringComparison.OrdinalIgnoreCase)) == true;
            if (reviewPlan is null || buildTestWasPlanned)
            {
                return new RemoteDeliveryIntegrationDecision(
                    false,
                    RemoteBuildTestGateClass.Failed,
                    reviewPlan is null
                        ? "The settled Remote Review has no frozen plan proving build/test is not applicable."
                        : "The Remote Review report omitted its planned build/test verdict.");
            }

            return new RemoteDeliveryIntegrationDecision(
                true,
                RemoteBuildTestGateClass.NotApplicable,
                "The Remote Review plan has no applicable build/test gate.");
        }

        if (buildTests.Any(verdict => !IsGreenOrNotApplicable(verdict)))
        {
            return new RemoteDeliveryIntegrationDecision(
                false,
                RemoteBuildTestGateClass.Failed,
                "At least one applicable Remote Review build/test gate did not pass.");
        }

        var gateClass = buildTests.All(IsNotApplicable)
            ? RemoteBuildTestGateClass.NotApplicable
            : RemoteBuildTestGateClass.Passed;
        return new RemoteDeliveryIntegrationDecision(
            true,
            gateClass,
            gateClass == RemoteBuildTestGateClass.Passed
                ? "All applicable Remote Review build/test gates passed."
                : "Every Remote Review build/test gate is not applicable.");
    }

    private static bool IsGreenOrNotApplicable(Contract.ReviewVerdictDto verdict)
        => string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase)
           || IsNotApplicable(verdict);

    private static bool IsNotApplicable(Contract.ReviewVerdictDto verdict)
        => string.Equals(verdict.Status, "not-applicable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verdict.Status, "not_applicable", StringComparison.OrdinalIgnoreCase)
           || string.Equals(verdict.Classification, "NotApplicable", StringComparison.OrdinalIgnoreCase)
           || (string.Equals(verdict.Status, "skipped", StringComparison.OrdinalIgnoreCase)
               && string.Equals(verdict.Classification, "NoCommands", StringComparison.OrdinalIgnoreCase));
}

public sealed record RemoteDeliveryIntegrationRequest(
    string Project,
    string JobId,
    string JobFolderPath,
    string? WatchPath,
    string IntegrationBranch,
    string IntegrationStrategy,
    string PipelineType,
    DateTimeOffset DeliveredAtUtc);

/// <summary>
/// Serializes immediately eligible fenced deliveries per project in delivery
/// order, then delegates every mutation to <see cref="MergeIntoDevelopRunner"/>.
/// The caller awaits the result before moving the card to Human Review.
/// </summary>
public sealed class RemoteDeliveryIntegrationCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectQueue> _projectQueues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>> _integrate;
    private readonly Action<RemoteDeliveryIntegrationRequest, string, string, string> _recordFailure;
    private readonly ILogger<RemoteDeliveryIntegrationCoordinator> _logger;
    private long _sequence;

    public RemoteDeliveryIntegrationCoordinator(
        MergeIntoDevelopRunner runner,
        TaskScannerService scanner,
        TaskProvenanceService provenance,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline,
        ILogger<RemoteDeliveryIntegrationCoordinator> logger)
        : this(
            request => IntegrateAndRecordAsync(request, runner, scanner, provenance, timeline),
            logger,
            (request, failureCode, summary, detail) => RecordPreReviewFailure(
                request,
                failureCode,
                summary,
                detail,
                pipelineLog,
                timeline))
    {
    }

    internal RemoteDeliveryIntegrationCoordinator(
        Func<RemoteDeliveryIntegrationRequest, Task<MergeIntoIntegrationResult>> integrate,
        ILogger<RemoteDeliveryIntegrationCoordinator> logger,
        Action<RemoteDeliveryIntegrationRequest, string, string, string>? recordFailure = null)
    {
        _integrate = integrate;
        _recordFailure = recordFailure ?? ((_, _, _, _) => { });
        _logger = logger;
    }

    /// <summary>
    /// Persists a Remote delivery gate rejection before the card enters Human
    /// Review. The card therefore projects a typed failure instead of a silent
    /// integration-pending state, and acceptance has no reason to retry it.
    /// </summary>
    public void RecordGateFailure(
        RemoteDeliveryIntegrationRequest request,
        string detail)
        => _recordFailure(
            request,
            AcceptedIntegrationFailureCodes.DeliveryGateFailed,
            "Remote delivery gate failed before integration.",
            detail);

    public Task<MergeIntoIntegrationResult> EnqueueAsync(
        RemoteDeliveryIntegrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<MergeIntoIntegrationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = Interlocked.Increment(ref _sequence);
        ProjectQueue projectQueue;
        var startDrain = false;
        lock (_gate)
        {
            if (!_projectQueues.TryGetValue(request.Project, out projectQueue!))
            {
                projectQueue = new ProjectQueue();
                _projectQueues[request.Project] = projectQueue;
            }
            projectQueue.Pending.Add(new QueuedDelivery(request, sequence, completion));
            if (!projectQueue.Running)
            {
                projectQueue.Running = true;
                startDrain = true;
            }
        }

        if (startDrain) _ = DrainProjectAsync(request.Project, projectQueue);
        return completion.Task;
    }

    private async Task DrainProjectAsync(string project, ProjectQueue projectQueue)
    {
        while (true)
        {
            QueuedDelivery delivery;
            lock (_gate)
            {
                if (projectQueue.Pending.Count == 0)
                {
                    projectQueue.Running = false;
                    if (_projectQueues.TryGetValue(project, out var current)
                        && ReferenceEquals(current, projectQueue))
                    {
                        _projectQueues.Remove(project);
                    }
                    return;
                }

                var nextIndex = 0;
                for (var index = 1; index < projectQueue.Pending.Count; index++)
                {
                    if (QueuedDeliveryComparer.Instance.Compare(
                            projectQueue.Pending[index],
                            projectQueue.Pending[nextIndex]) < 0)
                    {
                        nextIndex = index;
                    }
                }
                delivery = projectQueue.Pending[nextIndex];
                projectQueue.Pending.RemoveAt(nextIndex);
            }

            try
            {
                _logger.LogInformation(
                    "remote-delivery-integration started project={Project} job={JobId} sequence={Sequence} deliveredAt={DeliveredAt}",
                    delivery.Request.Project,
                    delivery.Request.JobId,
                    delivery.Sequence,
                    delivery.Request.DeliveredAtUtc);
                var result = await _integrate(delivery.Request).ConfigureAwait(false);
                delivery.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "remote-delivery-integration failed project={Project} job={JobId} sequence={Sequence}",
                    delivery.Request.Project,
                    delivery.Request.JobId,
                    delivery.Sequence);
                _recordFailure(
                    delivery.Request,
                    AcceptedIntegrationFailureCodes.IntegrationError,
                    "Immediate Remote delivery integration failed.",
                    ex.Message);
                delivery.Completion.TrySetResult(MergeIntoIntegrationResult.Of(
                    MergeIntoIntegrationOutcome.Error,
                    error: ex.Message));
            }
        }
    }

    private sealed class ProjectQueue
    {
        public List<QueuedDelivery> Pending { get; } = [];
        public bool Running { get; set; }
    }

    private sealed record QueuedDelivery(
        RemoteDeliveryIntegrationRequest Request,
        long Sequence,
        TaskCompletionSource<MergeIntoIntegrationResult> Completion);

    private sealed class QueuedDeliveryComparer : IComparer<QueuedDelivery>
    {
        public static QueuedDeliveryComparer Instance { get; } = new();

        public int Compare(QueuedDelivery? left, QueuedDelivery? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var delivered = left.Request.DeliveredAtUtc.CompareTo(right.Request.DeliveredAtUtc);
            return delivered != 0 ? delivered : left.Sequence.CompareTo(right.Sequence);
        }
    }

    private static async Task<MergeIntoIntegrationResult> IntegrateAndRecordAsync(
        RemoteDeliveryIntegrationRequest request,
        MergeIntoDevelopRunner runner,
        TaskScannerService scanner,
        TaskProvenanceService provenance,
        TimelineLog timeline)
    {
        var result = await runner.RunAsync(
            request.Project,
            request.JobId,
            request.JobFolderPath,
            request.WatchPath,
            request.IntegrationBranch,
            CancellationToken.None,
            request.IntegrationStrategy,
            request.PipelineType).ConfigureAwait(false);

        var job = scanner.FindJob(request.JobId, request.WatchPath);
        if (job is null) return result;
        if (result.Outcome.IsFreshMerge()
            && !string.IsNullOrWhiteSpace(result.MergedSha))
        {
            provenance.RecordMerge(job, result.MergedSha);
            job = scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        var success = result.Outcome.IsSuccessfulIntegration();
        timeline.Append(
            job.FolderPath,
            success ? TimelineEventKinds.IntegrationSucceeded : TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            success
                ? $"Remote delivery integrated into {request.IntegrationBranch} before Human Review."
                : $"Immediate Remote delivery integration failed ({result.Outcome}); the task remains reviewable with a visible integration failure.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["integrationBranch"] = request.IntegrationBranch,
                ["detail"] = result.Error ?? string.Empty,
                ["stage"] = "pre-human-review",
            });
        return result;
    }

    private static void RecordPreReviewFailure(
        RemoteDeliveryIntegrationRequest request,
        string failureCode,
        string summary,
        string detail,
        PipelineExecutionLog pipelineLog,
        TimelineLog timeline)
    {
        var now = DateTime.UtcNow;
        pipelineLog.RecordStep(request.JobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            StartedAt = now,
            CompletedAt = now,
            Verdict = failureCode,
            VerdictSummary = summary,
            Reason = detail,
            FailureCode = failureCode,
        });
        timeline.Append(
            request.JobFolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"{summary} The task remains available for review and delivery recovery.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = failureCode,
                ["integrationBranch"] = request.IntegrationBranch,
                ["detail"] = detail,
                ["stage"] = "pre-human-review",
            });
    }
}
