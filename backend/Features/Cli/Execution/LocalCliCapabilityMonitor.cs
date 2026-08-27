namespace AgentStudio.Cli;

/// <summary>
/// Periodically drives the same local CLI probe used by run startup. This lets
/// a vanished npm shim heal while the board is idle and keeps the repair event
/// available to the status and Execution Hosts surfaces.
/// </summary>
public sealed class LocalCliCapabilityMonitor : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(1);
    private readonly CliRouter _router;
    private readonly LocalCliSelfHealService _selfHeal;
    private readonly ILogger<LocalCliCapabilityMonitor> _logger;

    public LocalCliCapabilityMonitor(
        CliRouter router,
        LocalCliSelfHealService selfHeal,
        ILogger<LocalCliCapabilityMonitor> logger)
    {
        _router = router;
        _selfHeal = selfHeal;
        _logger = logger;
    }

    public async Task ProbeNowAsync(CancellationToken ct)
    {
        foreach (var cli in _router.All.Where(cli => cli.CliType is CliTypes.Claude or CliTypes.Codex))
        {
            try
            {
                await _selfHeal.ProbeAndRepairAsync(
                    cli.CliType,
                    cli.GetCliPath(),
                    () => cli.TestCliPath(),
                    ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "local-cli-capability-probe-failed cli={Cli}", cli.CliType);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ProbeInterval);
        do
        {
            await ProbeNowAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
