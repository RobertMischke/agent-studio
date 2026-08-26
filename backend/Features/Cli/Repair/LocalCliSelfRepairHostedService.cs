namespace AgentStudio.Cli;

/// <summary>
/// Keeps the control-plane host capability fresh even when no operator has the
/// CLI settings page open. The coordinator is itself hourly-bounded; this loop
/// may therefore probe frequently without repeating npm mutations.
/// </summary>
public sealed class LocalCliSelfRepairHostedService : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);
    private readonly LocalCliSelfRepairService _repair;
    private readonly ILocalNpmRepairBoundary _boundary;
    private readonly ILogger<LocalCliSelfRepairHostedService> _logger;

    public LocalCliSelfRepairHostedService(
        LocalCliSelfRepairService repair,
        ILocalNpmRepairBoundary boundary,
        ILogger<LocalCliSelfRepairHostedService> logger)
    {
        _repair = repair;
        _boundary = boundary;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_boundary.SupportsRepair) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _repair.ProbeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Local CLI capability probe failed");
            }
            try
            {
                await Task.Delay(ProbeInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
