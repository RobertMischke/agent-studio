using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace AgentStudio.Runner;

public sealed record AutoReviewPostProcessingRequest(
    string ProjectName,
    string JobId,
    string WatchPath,
    DateTime EnqueuedAtUtc,
    string Source);

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

    private static readonly JsonSerializerOptions LifecycleJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

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
            _logger.LogInformation(
                "auto-review-postprocessing-skipped project={Project} job={JobId} reason=review-decision-orchestrator-disabled",
                request.ProjectName, request.JobId);
            return;
        }

        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
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
            await _reviewDecisionOrchestrator.ProcessCardAsync(
                workspace, request.ProjectName, request.JobId, request.WatchPath, ct);
            sw.Stop();
            // completion latency = run finished (enqueue) -> post-processing done;
            // the queue-wait share separates "stau" from "step cost" in the metric.
            _logger.LogInformation(
                "auto-review-postprocessing-finished project={Project} job={JobId} elapsedMs={ElapsedMs} queueWaitMs={QueueWaitMs} completionLatencyMs={CompletionLatencyMs}",
                request.ProjectName, request.JobId, sw.ElapsedMilliseconds, queueWaitMs, queueWaitMs + sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-failed project={Project} job={JobId} elapsedMs={ElapsedMs}",
                request.ProjectName, request.JobId, sw.ElapsedMilliseconds);
        }
    }

    private void MarkReviewDecisionRunning(AutoReviewPostProcessingRequest request)
    {
        try
        {
            var info = _scanner.FindJob(request.JobId, request.WatchPath);
            if (info == null || info.State != TaskStates.AutoReview) return;

            _mutations.SetJobPhase(info.FolderPath, LifecyclePhases.PostProcessingRunning);
            var now = DateTime.UtcNow;
            var snapshot = ReadLifecycleSnapshot(info.FolderPath) ?? new LifecycleSnapshot
            {
                Phase = LifecyclePhases.PostProcessingRunning,
                PhaseEnteredAt = now,
            };

            var checks = snapshot.PostProcessingChecks
                .Where(c => !string.Equals(c.Name, PipelineCatalogue.OrchestratorDecisionStepId, StringComparison.Ordinal))
                .ToList();
            checks.Add(new LifecycleCheck
            {
                Name = PipelineCatalogue.OrchestratorDecisionStepId,
                Status = "running",
                StartedAt = now,
                Detail = "Auto-review decision started from the run-boundary post-processing queue."
            });

            var updated = snapshot with
            {
                Phase = LifecyclePhases.PostProcessingRunning,
                PhaseEnteredAt = snapshot.PhaseEnteredAt ?? now,
                PostProcessingChecks = checks,
            };
            File.WriteAllText(
                Path.Combine(info.FolderPath, "lifecycle.json"),
                JsonSerializer.Serialize(updated, LifecycleJsonOptions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "auto-review-postprocessing-lifecycle-write-failed project={Project} job={JobId}",
                request.ProjectName, request.JobId);
        }
    }

    private static LifecycleSnapshot? ReadLifecycleSnapshot(string folderPath)
    {
        var path = Path.Combine(folderPath, "lifecycle.json");
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<LifecycleSnapshot>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }
}
