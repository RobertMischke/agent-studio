namespace AgentStudio.Cli;

/// <summary>
/// Keeps the control-plane host capability truthful even when no operator has
/// the CLI settings surface open. The coordinator owns concurrency and the
/// durable one-attempt-per-hour repair budget.
/// </summary>
public sealed class LocalCliSelfHealHostedService(
    LocalCliSelfHeal selfHeal,
    ILogger<LocalCliSelfHealHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await selfHeal.ProbeAndRepairAllAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "cli-self-heal-probe-failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
