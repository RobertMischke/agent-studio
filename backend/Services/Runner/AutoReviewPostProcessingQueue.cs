using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Services.Runner;

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
    private readonly Channel<AutoReviewPostProcessingRequest> _channel =
        Channel.CreateUnbounded<AutoReviewPostProcessingRequest>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ChannelReader<AutoReviewPostProcessingRequest> Reader => _channel.Reader;

    public bool Enqueue(AutoReviewPostProcessingRequest request) =>
        _channel.Writer.TryWrite(request);
}

/// <summary>
/// Drains the event-driven auto-review queue. Processing is intentionally
/// outside the runner's active-job latch: a coding runner may pick the next
/// task while this worker runs aspect review and the final orchestrator
/// decision for the completed one.
/// </summary>
public sealed class AutoReviewPostProcessingWorker : BackgroundService
{
    private readonly AutoReviewPostProcessingQueue _queue;
    private readonly ReviewDecisionOrchestrator _reviewDecisionOrchestrator;
    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AutoReviewPostProcessingWorker> _logger;

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
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await ProcessAsync(request, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown. The ReviewDecisionOrchestrator boot/backstop
            // sweep remains the recovery path for anything left in 4-auto-review.
        }
    }

    /// <summary>
    /// Processes one queued review request. Exposed for deterministic tests;
    /// production reaches it through <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task ProcessAsync(AutoReviewPostProcessingRequest request, CancellationToken ct)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning(
                "auto-review-postprocessing-skipped project={Project} job={JobId} reason=missing-task-repository",
                request.ProjectName, request.JobId);
            return;
        }

        var sw = Stopwatch.StartNew();
        MarkReviewDecisionRunning(request);
        _logger.LogInformation(
            "auto-review-postprocessing-started project={Project} job={JobId} source={Source}",
            request.ProjectName, request.JobId, request.Source);

        try
        {
            await _reviewDecisionOrchestrator.TickOnceAsync(workspace, ct);
            sw.Stop();
            _logger.LogInformation(
                "auto-review-postprocessing-finished project={Project} job={JobId} elapsedMs={ElapsedMs}",
                request.ProjectName, request.JobId, sw.ElapsedMilliseconds);
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
