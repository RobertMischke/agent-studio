using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Periodic safety net for completed-job auto-push. The synchronous trigger
/// lives on the move to <c>6-completed</c>; this sweep covers missed process
/// windows and pre-existing completed jobs after a backend restart.
/// </summary>
public sealed class CompletedPushBackstopHostedService : BackgroundService
{
    private readonly JobScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly JobTransitionService _transitions;
    private readonly IConfiguration _config;
    private readonly ILogger<CompletedPushBackstopHostedService> _logger;

    public CompletedPushBackstopHostedService(
        JobScannerService scanner,
        ProjectSettingsService settings,
        JobTransitionService transitions,
        IConfiguration config,
        ILogger<CompletedPushBackstopHostedService> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _transitions = transitions;
        _config = config;
        _logger = logger;
    }

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var pushed = 0;
        var completed = _scanner.ScanAllJobs()
            .Where(j => j.State == JobStates.Completed)
            .OrderBy(j => j.LastActivity)
            .ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var job in completed)
        {
            ct.ThrowIfCancellationRequested();
            var strategy = AutoPushStrategies.Normalize(_settings.Get(job.ProjectName).AutoPushStrategy);
            if (strategy == AutoPushStrategies.Never) continue;
            pushed += await _transitions.PushCompletedJobCommitsAsync(job, strategy, ct);
        }

        if (pushed > 0)
            _logger.LogInformation("Completed auto-push backstop pushed {Count} commit(s)", pushed);
        return pushed;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = ResolveInterval();
        using var timer = new PeriodicTimer(interval);

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
                _logger.LogWarning(ex, "Completed auto-push backstop sweep failed");
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

    private TimeSpan ResolveInterval()
    {
        var minutes = _config.GetValue<int?>("AutoPush:BackstopIntervalMinutes") ?? 15;
        return TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60));
    }
}
