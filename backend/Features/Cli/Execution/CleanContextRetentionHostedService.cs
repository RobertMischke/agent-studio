using AgentStudio.CliHosting;

namespace AgentStudio.Cli;

/// <summary>
/// Bounded retention sweep for task-stable clean-context homes. The same store
/// also sweeps opportunistically on Agent Host acquisition; this hosted tick
/// covers a quiet local backend that receives no new runs after homes expire.
/// </summary>
public sealed class CleanContextRetentionHostedService : BackgroundService
{
    public const int DefaultSweepIntervalHours = 6;

    private readonly IConfiguration _configuration;
    private readonly ILogger<CleanContextRetentionHostedService> _logger;

    public CleanContextRetentionHostedService(
        IConfiguration configuration,
        ILogger<CleanContextRetentionHostedService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public CleanContextCleanupResult RunOnce(DateTimeOffset? nowUtc = null)
    {
        var root = TaskCleanContextStore.ResolveRoot(
            GenericCliExecutionService.ResolveUserHome(),
            ResolveRootOverride(_configuration));
        var retentionDays = Math.Clamp(
            _configuration.GetValue<int?>("CleanContext:RetentionDays")
                ?? TaskCleanContextStore.DefaultRetentionDays,
            1,
            90);
        var result = TaskCleanContextStore.Cleanup(
            root,
            nowUtc,
            TimeSpan.FromDays(retentionDays));
        if (result.Deleted > 0 || result.FailedPaths.Count > 0)
        {
            _logger.LogInformation(
                "Clean-context retention sweep scanned {Scanned}, deleted {Deleted}, failed {Failed}",
                result.Scanned,
                result.Deleted,
                result.FailedPaths.Count);
        }
        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SweepSafely();
        using var timer = new PeriodicTimer(ResolveInterval());
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            SweepSafely();
        }
    }

    internal static string? ResolveRootOverride(IConfiguration configuration)
        => configuration["CleanContext:Root"]
           ?? configuration[TaskCleanContextStore.RootOverrideEnvironmentVariable];

    private TimeSpan ResolveInterval()
    {
        var hours = Math.Clamp(
            _configuration.GetValue<int?>("CleanContext:SweepIntervalHours")
                ?? DefaultSweepIntervalHours,
            1,
            24 * 7);
        return TimeSpan.FromHours(hours);
    }

    private void SweepSafely()
    {
        try
        {
            RunOnce();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clean-context retention sweep failed; the next bounded tick will retry");
        }
    }
}
