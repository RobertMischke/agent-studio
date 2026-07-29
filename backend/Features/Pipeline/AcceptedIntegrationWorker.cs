using System.Diagnostics;

namespace AgentStudio.Pipeline;

/// <summary>
/// Integrates accepted deliveries outside the accept HTTP request. The runner
/// owns merge/gate/rollback serialization and hands a released SHA to the
/// existing <see cref="IntegrationPushWorker"/>. Successful integration moves
/// the card from Human Review to Completed and clears the durable pending
/// marker. Decided failures clear the integrating phase, keep the card in Human
/// Review, and retain the marker for operator visibility and recovery.
/// </summary>
public sealed class AcceptedIntegrationWorker : BackgroundService
{
    private readonly AcceptedIntegrationQueue _queue;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskProvenanceService _provenance;
    private readonly ILogger<AcceptedIntegrationWorker> _logger;
    private readonly TaskTransitionService? _transitions;
    private readonly TimelineLog? _timeline;

    public AcceptedIntegrationWorker(
        AcceptedIntegrationQueue queue,
        MergeIntoDevelopRunner runner,
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskProvenanceService provenance,
        ILogger<AcceptedIntegrationWorker> logger,
        TaskTransitionService? transitions = null,
        TimelineLog? timeline = null)
    {
        _queue = queue;
        _runner = runner;
        _scanner = scanner;
        _mutations = mutations;
        _provenance = provenance;
        _logger = logger;
        _transitions = transitions;
        _timeline = timeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request);
            }
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(
                ex,
                "AcceptedIntegrationWorker: graceful shutdown; queued items are recovered by the durable backstop.");
        }
    }

    /// <summary>
    /// Processes one accepted delivery. Exposed internally for deterministic
    /// worker tests without starting a hosted-service loop.
    /// </summary>
    internal async Task<MergeIntoIntegrationResult> ProcessAsync(AcceptedIntegrationRequest request)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Deliberately not the host stopping token. Once merge/gate/rollback
            // starts it must reach a consistent terminal state. A process exit
            // can still interrupt it; the accepted-integration backstop then
            // reconstructs the work from the durable lane and pipeline facts.
            var result = await _runner.RunAsync(
                request.Project,
                request.JobId,
                request.JobFolderPath,
                request.WatchPath,
                request.IntegrationBranch,
                CancellationToken.None,
                request.IntegrationStrategy).ConfigureAwait(false);

            if (result.Outcome is MergeIntoIntegrationOutcome.Merged
                or MergeIntoIntegrationOutcome.AlreadyMerged)
            {
                await FinalizeAcceptedTaskAsync(request, result).ConfigureAwait(false);
            }
            else
            {
                ReturnToReviewWithFailure(request, result);
            }

            sw.Stop();
            _logger.LogInformation(
                "Accepted-integration worker completed {JobId} with {Outcome} in {ElapsedMs}ms",
                request.JobId, result.Outcome, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var errored = MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: ex.Message);
            ReturnToReviewWithFailure(request, errored);
            _logger.LogWarning(
                ex,
                "Accepted-integration worker failed for {JobId} after {ElapsedMs}ms; the task returned to Human Review",
                request.JobId, sw.ElapsedMilliseconds);
            return errored;
        }
    }

    private async Task FinalizeAcceptedTaskAsync(
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job?.State == TaskStates.HumanReview && _transitions != null)
        {
            var moved = await _transitions.MoveAsync(
                request.JobId,
                TaskStates.Completed,
                request.WatchPath,
                CancellationToken.None,
                request.CompletedLaneIndex,
                request.Cause ?? TimelineActors.System,
                request.Reason,
                expectedSourceState: TaskStates.HumanReview,
                suppressIntegrationTrigger: true).ConfigureAwait(false);
            if (moved.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "Integrated {JobId}, but finalizing Completed failed with {Status}: {Message}",
                    request.JobId,
                    moved.Status,
                    moved.Message);
                return;
            }
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
            _mutations.SetJobPhase(job.FolderPath, null);
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        CompletePendingMarker(request, result);
        if (job != null)
        {
            _timeline?.Append(
                job.FolderPath,
                TimelineEventKinds.IntegrationSucceeded,
                TimelineActors.System,
                $"Integration into {request.IntegrationBranch} succeeded; acceptance completed.",
                details: new Dictionary<string, string>
                {
                    ["outcome"] = result.Outcome.ToString(),
                    ["integrationBranch"] = request.IntegrationBranch,
                });
        }
    }

    private void ReturnToReviewWithFailure(
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job == null || job.State != TaskStates.HumanReview) return;

        _mutations.SetJobPhase(job.FolderPath, null);
        job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        _timeline?.Append(
            job.FolderPath,
            TimelineEventKinds.IntegrationFailed,
            TimelineActors.System,
            $"Integration failed ({result.Outcome}); the task remains in Human Review.",
            details: new Dictionary<string, string>
            {
                ["outcome"] = result.Outcome.ToString(),
                ["integrationBranch"] = request.IntegrationBranch,
                ["detail"] = result.Error ?? string.Empty,
            });
    }

    private void CompletePendingMarker(
        AcceptedIntegrationRequest request,
        MergeIntoIntegrationResult result)
    {
        var job = _scanner.FindJob(request.JobId, request.WatchPath);
        if (job == null) return;

        if (result.Outcome == MergeIntoIntegrationOutcome.Merged
            && !string.IsNullOrWhiteSpace(result.MergedSha))
        {
            _provenance.RecordMerge(job, result.MergedSha);
            job = _scanner.FindJob(request.JobId, request.WatchPath) ?? job;
        }

        var tags = (job.Tags ?? [])
            .Where(tag => !IntegrationStatuses.IsPendingTag(tag))
            .ToList();
        if (tags.Count != (job.Tags?.Count ?? 0))
        {
            _mutations.SetJobTags(job.Id, tags, job.WatchPath);
        }
    }
}
