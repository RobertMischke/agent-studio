using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace AgentStudio.Runner;

public sealed record AutoReviewPostProcessingRequest(
    string ProjectName,
    string JobId,
    string WatchPath,
    DateTime EnqueuedAtUtc,
    string Source,
    /// <summary>
    /// How many times this card has already been re-driven after a deferral
    /// (see <see cref="PostProcessingCardStatus.Deferred"/>). Zero for every
    /// request produced by a real run boundary, recovery sweep or operator
    /// requeue; only the retry path increments it.
    /// </summary>
    int Attempt = 0);

public interface IAutoReviewPostProcessingQueue
{
    bool Enqueue(AutoReviewPostProcessingRequest request);
}

/// <summary>
/// Event-driven hand-off from the run-boundary post-processing path to the
/// auto-review decision engine. The durable state still lives in
/// <c>4-auto-review</c>; this queue only removes the old "wait for the next
/// poll tick" delay.
/// </summary>
public sealed class AutoReviewPostProcessingQueue : IAutoReviewPostProcessingQueue
{
    private readonly object _pendingLock = new();
    private readonly List<AutoReviewPostProcessingRequest> _pending = [];
    private readonly Channel<AutoReviewPostProcessingRequest> _channel =
        Channel.CreateUnbounded<AutoReviewPostProcessingRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<AutoReviewPostProcessingRequest> Reader => _channel.Reader;

    public bool Enqueue(AutoReviewPostProcessingRequest request)
    {
        lock (_pendingLock) _pending.Add(request);
        if (_channel.Writer.TryWrite(request)) return true;
        MarkStarted(request);
        return false;
    }

    /// <summary>
    /// One-based position in the existing post-processing slot queue. This is a
    /// read projection of queue membership, not a second scheduling state.
    /// </summary>
    public int? PositionOf(string projectName, string jobId)
    {
        lock (_pendingLock)
        {
            var position = _pending.FindIndex(request =>
                string.Equals(request.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.JobId, jobId, StringComparison.OrdinalIgnoreCase));
            return position < 0 ? null : position + 1;
        }
    }

    internal void MarkStarted(AutoReviewPostProcessingRequest request)
    {
        lock (_pendingLock)
        {
            var index = _pending.FindIndex(candidate =>
                candidate.EnqueuedAtUtc == request.EnqueuedAtUtc
                && string.Equals(candidate.ProjectName, request.ProjectName, StringComparison.Ordinal)
                && string.Equals(candidate.JobId, request.JobId, StringComparison.Ordinal)
                && string.Equals(candidate.Source, request.Source, StringComparison.Ordinal));
            if (index >= 0) _pending.RemoveAt(index);
        }
    }
}

/// <summary>
/// Drains the event-driven auto-review queue. Processing is intentionally
/// outside the runner's active-job latch: a coding runner may pick the next
/// task while this worker runs aspect review and the final orchestrator
/// decision for the completed one.
/// </summary>
public sealed class AutoReviewPostProcessingWorker : BackgroundService
{
    /// <summary>
    /// Floor for the concurrent-card cap. The effective cap is derived from
    /// machine capacity (<see cref="DeriveMaxParallelism"/>): remote runners
    /// deliver waves of completed cards, and a fixed small cap let the
    /// 4-auto-review lane back up while cores sat idle. Effective concurrency
    /// still follows queue depth naturally - the cap only bounds it. Override
    /// with <c>PostProcessing:MaxParallelism</c> (read from appsettings like the
    /// gate timeouts). Per card the step order stays sequential - only across
    /// cards is there parallelism; build-heavy steps keep serializing on the
    /// machine-wide build-test-gate lock, so a wider admission mainly lets the
    /// LLM-bound steps of distinct cards overlap instead of queueing behind a
    /// card that is waiting for the gate.
    /// </summary>
    public const int DefaultMaxParallelism = 3;

    /// <summary>
    /// Upper bound for the derived cap: beyond this, more concurrent cards no
    /// longer add throughput (the gate lock and the orchestrator's per-hour
    /// call budget become the binding constraints) but do add workspace churn.
    /// </summary>
    public const int MaxDerivedParallelism = 12;

    /// <summary>Capacity-derived concurrent-card cap; clamped to [<see cref="DefaultMaxParallelism"/>, <see cref="MaxDerivedParallelism"/>].</summary>
    internal static int DeriveMaxParallelism(int processorCount) =>
        Math.Clamp(processorCount / 2, DefaultMaxParallelism, MaxDerivedParallelism);

    private readonly AutoReviewPostProcessingQueue _queue;
    private readonly ReviewDecisionOrchestrator _reviewDecisionOrchestrator;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoReviewPostProcessingWorker> _logger;

    /// <summary>
    /// Test seam: when set, one request is handed to this delegate instead of the
    /// real <see cref="ProcessAsync"/>, so the bounded-parallel drain (max-N +
    /// in-flight dedup + failure isolation) can be exercised deterministically
    /// without a full orchestrator run. Null in production.
    /// </summary>
    internal Func<AutoReviewPostProcessingRequest, CancellationToken, Task>? ProcessOverride { get; set; }

    /// <summary>
    /// Test seam: replaces the deferral backoff, so the re-drive path can be
    /// exercised without waiting out the real 30s-and-doubling schedule. Null
    /// in production.
    /// </summary>
    internal Func<int, TimeSpan>? DeferralDelayOverride { get; set; }

    public AutoReviewPostProcessingWorker(
        AutoReviewPostProcessingQueue queue,
        ReviewDecisionOrchestrator reviewDecisionOrchestrator,
        TaskScannerService scanner,
        TaskMutationService mutations,
        IConfiguration configuration,
        ILogger<AutoReviewPostProcessingWorker> logger)
    {
        _queue = queue;
        _reviewDecisionOrchestrator = reviewDecisionOrchestrator;
        _scanner = scanner;
        _mutations = mutations;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded parallelism across cards: several completed cards are
        // post-processed at once (up to maxParallelism), while each card's own step
        // sequence stays serial. An in-flight set keeps the same card from being
        // processed twice concurrently, and each card's failure is isolated so it
        // never tears down the pool. The workspace-wide backstop sweep remains the
        // safety net for anything a slot missed.
        var maxParallelism = Math.Max(1,
            _configuration.GetValue("PostProcessing:MaxParallelism",
                DeriveMaxParallelism(Environment.ProcessorCount)));
        var slots = new SemaphoreSlim(maxParallelism, maxParallelism);
        var inFlight = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var running = new ConcurrentDictionary<Task, byte>();

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var key = CardKey(request);

                // Same card already in flight: drop the duplicate enqueue. The durable
                // 4-auto-review state plus the backstop sweep / startup recovery
                // re-drive it if it still needs work, so nothing is lost and no card
                // is post-processed twice at the same time.
                if (!inFlight.TryAdd(key, 0))
                {
                    _queue.MarkStarted(request);
                    _logger.LogDebug(
                        "auto-review-postprocessing-dedup project={Project} job={JobId} reason=already-in-flight",
                        request.ProjectName, request.JobId);
                    continue;
                }

                await slots.WaitAsync(stoppingToken);
                _queue.MarkStarted(request);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        await RunOneAsync(request, stoppingToken);
                    }
                    catch (OperationCanceledException __ex) when (stoppingToken.IsCancellationRequested)
                    {
                        SilentCatch.Note(__ex, "AutoReviewPostProcessingWorker: card post-processing cancelled on graceful shutdown.");
                    }
                    catch (Exception ex)
                    {
                        // Failure isolation: one card's fault must not abort the others.
                        _logger.LogWarning(ex,
                            "auto-review-postprocessing-worker-task-failed project={Project} job={JobId}",
                            request.ProjectName, request.JobId);
                    }
                    finally
                    {
                        inFlight.TryRemove(key, out _);
                        slots.Release();
                    }
                }, stoppingToken);

                running[task] = 0;
                _ = task.ContinueWith(
                    t => running.TryRemove(t, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            await Task.WhenAll(running.Keys);
        }
        catch (OperationCanceledException __ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(__ex, "AutoReviewPostProcessingQueue: Graceful shutdown. The ReviewDecisionOrchestrator boot/backstop");
            // Graceful shutdown. The ReviewDecisionOrchestrator boot/backstop
            // sweep remains the recovery path for anything left in 4-auto-review.
            try { await Task.WhenAll(running.Keys); }
            catch (Exception __drainEx) { SilentCatch.Note(__drainEx, "AutoReviewPostProcessingWorker: draining in-flight card post-processings on shutdown."); }
        }
    }

    private Task RunOneAsync(AutoReviewPostProcessingRequest request, CancellationToken ct)
        => ProcessOverride != null ? ProcessOverride(request, ct) : ProcessAsync(request, ct);

    private static string CardKey(AutoReviewPostProcessingRequest request)
        => request.ProjectName + "" + request.JobId;

    /// <summary>
    /// Processes one queued review request. Exposed for deterministic tests;
    /// production reaches it through <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task ProcessAsync(AutoReviewPostProcessingRequest request, CancellationToken ct)
    {
        if (!_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
        {
            TerminalizeActiveLifecycle(
                request,
                "Post Processing is disabled, so the queued attempt cannot continue.");
            _logger.LogInformation(
                "auto-review-postprocessing-skipped project={Project} job={JobId} reason=review-decision-orchestrator-disabled",
                request.ProjectName, request.JobId);
            return;
        }

        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            TerminalizeActiveLifecycle(
                request,
                "Post Processing cannot continue because the task repository is not configured.");
            _logger.LogWarning(
                "auto-review-postprocessing-skipped project={Project} job={JobId} reason=missing-task-repository",
                request.ProjectName, request.JobId);
            return;
        }

        var sw = Stopwatch.StartNew();
        var queueWaitMs = (long)Math.Max(0, (DateTime.UtcNow - request.EnqueuedAtUtc).TotalMilliseconds);
        MarkReviewDecisionRunning(request);
        _logger.LogInformation(
            "auto-review-postprocessing-started project={Project} job={JobId} source={Source} queueWaitMs={QueueWaitMs}",
            request.ProjectName, request.JobId, request.Source, queueWaitMs);

        try
        {
            var outcome = await _reviewDecisionOrchestrator.ProcessCardAsync(
                workspace, request.ProjectName, request.JobId, request.WatchPath, ct);
            sw.Stop();
            // completion latency = run finished (enqueue) -> post-processing done;
            // the queue-wait share separates "stau" from "step cost" in the metric.
            _logger.LogInformation(
                "auto-review-postprocessing-finished project={Project} job={JobId} elapsedMs={ElapsedMs} queueWaitMs={QueueWaitMs} completionLatencyMs={CompletionLatencyMs} status={Status} reason={Reason}",
                request.ProjectName, request.JobId, sw.ElapsedMilliseconds, queueWaitMs,
                queueWaitMs + sw.ElapsedMilliseconds, outcome.Status, outcome.Reason);

            ApplyOutcome(request, outcome, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TerminalizeActiveLifecycle(
                request,
                "Post Processing was interrupted during shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            TerminalizeActiveLifecycle(
                request,
                "Post Processing failed before reaching a terminal decision: " + ex.GetType().Name + ".");
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-failed project={Project} job={JobId} elapsedMs={ElapsedMs}",
                request.ProjectName, request.JobId, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Turns the engine's verdict about the pass into lifecycle state.
    /// <list type="bullet">
    /// <item><b>Decided</b> - a decision path owned the card. If its lifecycle
    /// is nevertheless still active the verdict never landed, so the old
    /// safety net still terminalizes it, now naming the path that ran.</item>
    /// <item><b>Deferred</b> - a legitimate hand-off (the canonical review
    /// executor owns the card) or a transient limit. The card is parked in
    /// <c>awaiting-review</c> without a blocking reason and re-driven with
    /// backoff. This is the case that used to be blocked terminally.</item>
    /// <item><b>Blocked</b> - a real precondition failure, terminalized with
    /// the concrete reason rather than the generic sentence.</item>
    /// </list>
    /// </summary>
    internal void ApplyOutcome(
        AutoReviewPostProcessingRequest request,
        PostProcessingCardResult outcome,
        CancellationToken ct)
    {
        switch (outcome.Status)
        {
            case PostProcessingCardStatus.Deferred:
                ParkActiveLifecycle(request, outcome.Reason);
                ScheduleDeferralRetry(request, outcome.Reason, ct);
                return;

            case PostProcessingCardStatus.Blocked:
                TerminalizeActiveLifecycle(
                    request,
                    "Post Processing stopped before a terminal decision: " + outcome.Reason + ".");
                return;

            default:
                TerminalizeActiveLifecycle(
                    request,
                    "Post Processing returned without a terminal decision (" + outcome.Reason + ").");
                return;
        }
    }

    /// <summary>
    /// Highest number of automatic re-drives for a deferred card. The card is
    /// durable in <c>4-auto-review</c> and the boot/backstop sweep re-drives it
    /// anyway, so this only shortens the wait; exhausting it leaves the card
    /// resting in <c>awaiting-review</c> and never blocks it.
    /// </summary>
    internal const int MaxDeferralRetries = 5;

    /// <summary>First deferral re-drive delay; doubles per attempt.</summary>
    internal static readonly TimeSpan DeferralRetryBaseDelay = TimeSpan.FromSeconds(30);

    /// <summary>Cap for the doubling, so a long wait still re-checks regularly.</summary>
    internal static readonly TimeSpan DeferralRetryMaxDelay = TimeSpan.FromMinutes(10);

    internal static TimeSpan DeferralRetryDelay(int attempt)
    {
        var factor = Math.Pow(2, Math.Max(0, attempt));
        var seconds = DeferralRetryBaseDelay.TotalSeconds * factor;
        return seconds >= DeferralRetryMaxDelay.TotalSeconds
            ? DeferralRetryMaxDelay
            : TimeSpan.FromSeconds(seconds);
    }

    private void ScheduleDeferralRetry(
        AutoReviewPostProcessingRequest request,
        string reason,
        CancellationToken ct)
    {
        if (request.Attempt >= MaxDeferralRetries)
        {
            _logger.LogInformation(
                "auto-review-postprocessing-deferral-exhausted project={Project} job={JobId} reason={Reason} attempts={Attempts}",
                request.ProjectName, request.JobId, reason, request.Attempt);
            return;
        }

        var delay = DeferralDelayOverride?.Invoke(request.Attempt) ?? DeferralRetryDelay(request.Attempt);
        _logger.LogInformation(
            "auto-review-postprocessing-deferred project={Project} job={JobId} reason={Reason} attempt={Attempt} retryInMs={RetryInMs}",
            request.ProjectName, request.JobId, reason, request.Attempt, (long)delay.TotalMilliseconds);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                _queue.Enqueue(request with
                {
                    Attempt = request.Attempt + 1,
                    EnqueuedAtUtc = DateTime.UtcNow,
                    Source = "deferral-retry",
                });
            }
            catch (OperationCanceledException __ex)
            {
                SilentCatch.Note(__ex, "AutoReviewPostProcessingWorker: deferral retry dropped on shutdown.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "auto-review-postprocessing-retry-enqueue-failed project={Project} job={JobId}",
                    request.ProjectName, request.JobId);
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Closes an active post-processing lifecycle without a failure: the pass
    /// legitimately produced no verdict, so the card rests in
    /// <c>awaiting-review</c> with no blocking reason and stays pickable.
    /// </summary>
    private void ParkActiveLifecycle(AutoReviewPostProcessingRequest request, string reason)
    {
        try
        {
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info == null) return;
            var updated = PostProcessingLifecycleStore.Terminalize(
                info.FolderPath,
                DateTime.UtcNow,
                failed: false,
                "Post Processing deferred: " + reason + ".",
                _logger,
                onlyWhenActive: true);
            if (updated && string.Equals(info.State, TaskStates.AutoReview, StringComparison.Ordinal))
                _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.AwaitingReview);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-lifecycle-park-failed project={Project} job={JobId}",
                request.ProjectName,
                request.JobId);
        }
    }

    private void MarkReviewDecisionRunning(AutoReviewPostProcessingRequest request)
    {
        try
        {
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info == null || info.State != TaskStates.AutoReview) return;

            var now = DateTime.UtcNow;
            if (PostProcessingLifecycleStore.BeginPostProcessing(
                    info.FolderPath,
                    now,
                    PipelineCatalogue.OrchestratorDecisionStepId,
                    "Auto-review decision started from the run-boundary post-processing queue.",
                    _logger,
                    replaceChecks: false))
            {
                _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingRunning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-lifecycle-write-failed project={Project} job={JobId}",
                request.ProjectName, request.JobId);
        }
    }

    private void TerminalizeActiveLifecycle(
        AutoReviewPostProcessingRequest request,
        string detail)
    {
        try
        {
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info == null) return;
            var updated = PostProcessingLifecycleStore.Terminalize(
                info.FolderPath,
                DateTime.UtcNow,
                failed: true,
                detail,
                _logger,
                onlyWhenActive: true);
            if (updated && string.Equals(info.State, TaskStates.AutoReview, StringComparison.Ordinal))
                _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingBlocked);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-lifecycle-terminalize-failed project={Project} job={JobId}",
                request.ProjectName,
                request.JobId);
        }
    }
}
