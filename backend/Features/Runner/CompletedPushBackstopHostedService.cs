

namespace AgentStudio.Runner;

/// <summary>
/// Periodic safety net for completed-job auto-push. The synchronous trigger
/// lives on the move to <c>6-completed</c>; this sweep covers missed process
/// windows and pre-existing completed jobs after a backend restart.
/// </summary>
public sealed class CompletedPushBackstopHostedService : BackgroundService
{
    private readonly TaskScannerService _scanner;
    private readonly ProjectSettingsService _settings;
    private readonly TaskTransitionService _transitions;
    private readonly IConfiguration _config;
    private readonly ILogger<CompletedPushBackstopHostedService> _logger;

    public CompletedPushBackstopHostedService(
        TaskScannerService scanner,
        ProjectSettingsService settings,
        TaskTransitionService transitions,
        IConfiguration config,
        ILogger<CompletedPushBackstopHostedService> logger)
    {
        _scanner = scanner;
        _settings = settings;
        _transitions = transitions;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Attempts already refused on lineage or policy grounds, keyed by
    /// job + SHA + branch. Every input in that key is immutable, so a repeat
    /// sweep would receive the identical refusal; skipping it is what keeps a
    /// blocked card from re-emitting the same alarm every 15 minutes forever
    /// (AGT-2688). A restart deliberately clears the memory so the refusal is
    /// re-checked and re-surfaced exactly once per process.
    /// </summary>
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);

    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        var pushed = 0;
        var completed = _scanner.ScanAllAutomationJobs()
            .Where(j => j.State == TaskStates.Completed)
            .OrderBy(j => j.LastActivity)
            .ThenBy(j => j.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var skipped = 0;
        foreach (var job in completed)
        {
            ct.ThrowIfCancellationRequested();
            var strategy = AutoPushStrategies.Normalize(_settings.Get(job.ProjectName).AutoPushStrategy);
            if (strategy == AutoPushStrategies.Never) continue;
            if (IsFullyBlocked(job))
            {
                skipped++;
                continue;
            }

            var outcome = await _transitions.PushCompletedJobCommitsAsync(job, strategy, ct);
            pushed += outcome.Pushed;
            foreach (var refusal in outcome.Refusals)
                _blocked.Add(BlockedKey(job.Id, refusal.Sha, refusal.TargetBranch));
        }

        if (pushed > 0)
            _logger.LogInformation("Completed auto-push backstop pushed {Count} commit(s)", pushed);
        if (skipped > 0)
            _logger.LogInformation(
                "Completed auto-push backstop skipped {Count} job(s) whose push is integration-push-blocked; "
                + "they need re-integration, not another push attempt",
                skipped);
        return pushed;
    }

    /// <summary>
    /// True when every commit this job would publish has already been refused
    /// for good. A job with even one still-pushable commit is retried in full.
    /// </summary>
    private bool IsFullyBlocked(TaskInfo job)
    {
        if (_blocked.Count == 0) return false;

        var commits = job.Commits.Count > 0
            ? job.Commits
            : job.Commit is null ? [] : [job.Commit];
        var shas = commits
            .Select(c => c.Sha)
            .Where(sha => !string.IsNullOrWhiteSpace(sha))
            .ToList();
        if (shas.Count == 0) return false;

        return shas.All(sha => _blocked.Any(key =>
            key.StartsWith(BlockedShaPrefix(job.Id, sha), StringComparison.Ordinal)));
    }

    private static string BlockedShaPrefix(string jobId, string sha) => $"{jobId}\0{sha}\0";

    private static string BlockedKey(string jobId, string sha, string branch)
        => BlockedShaPrefix(jobId, sha) + branch;

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
