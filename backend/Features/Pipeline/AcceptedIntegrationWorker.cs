using System.Diagnostics;

namespace AgentStudio.Pipeline;

/// <summary>
/// Integrates accepted deliveries outside the accept HTTP request. The runner
/// owns merge/gate/rollback serialization and hands a released SHA to the
/// existing <see cref="IntegrationPushWorker"/>. Successful integration clears
/// the card's durable pending marker; decided failures retain it for operator
/// visibility and recovery.
/// </summary>
public sealed class AcceptedIntegrationWorker : BackgroundService
{
    private readonly AcceptedIntegrationQueue _queue;
    private readonly MergeIntoDevelopRunner _runner;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly TaskProvenanceService _provenance;
    private readonly ILogger<AcceptedIntegrationWorker> _logger;

    public AcceptedIntegrationWorker(
        AcceptedIntegrationQueue queue,
        MergeIntoDevelopRunner runner,
        TaskScannerService scanner,
        TaskMutationService mutations,
        TaskProvenanceService provenance,
        ILogger<AcceptedIntegrationWorker> logger)
    {
        _queue = queue;
        _runner = runner;
        _scanner = scanner;
        _mutations = mutations;
        _provenance = provenance;
        _logger = logger;
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
                CompletePendingMarker(request, result);
            }

            sw.Stop();
            if (result.Outcome == MergeIntoIntegrationOutcome.Error)
            {
                _logger.LogError(
                    "Accepted-integration worker completed {JobId} with an integration error in {ElapsedMs}ms: {Error}",
                    request.JobId,
                    sw.ElapsedMilliseconds,
                    result.Error ?? "unknown error");
            }
            else
            {
                _logger.LogInformation(
                    "Accepted-integration worker completed {JobId} with {Outcome} in {ElapsedMs}ms",
                    request.JobId,
                    result.Outcome,
                    sw.ElapsedMilliseconds);
            }
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(
                ex,
                "Accepted-integration worker failed for {JobId} after {ElapsedMs}ms; the backstop will retry",
                request.JobId, sw.ElapsedMilliseconds);
            return MergeIntoIntegrationResult.Of(
                MergeIntoIntegrationOutcome.Error,
                error: ex.Message);
        }
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
