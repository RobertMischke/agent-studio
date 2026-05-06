using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Background loop that runs the orchestrator intake check on every
/// 2-ready card sitting in <c>phase = human-ready</c> (or with no phase
/// at all) inside a project that has <c>IntakeEnabled = true</c>.
///
/// <para>
/// Intake is opt-in per project (<see cref="ProjectSettings.IntakeEnabled"/>)
/// so the broader migration risk stays bounded. When the flag is off, this
/// loop is a no-op for that project; the runner pickup gate stays open and
/// behavior matches the pre-intake board.
/// </para>
///
/// <para>
/// Intake runs decoupled from the coding runner: per the open question in
/// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c> section 13,
/// intake is not a coding run, so it can run while the project's coding
/// runner is busy on a different job. The single-active-run boundary
/// applies to <c>3-progress</c>, not to intake. The hosted-service tick
/// processes one card per project per tick to bound work even on a busy
/// board.
/// </para>
/// </summary>
public sealed class IntakeHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    private readonly IServiceProvider _services;
    private readonly ILogger<IntakeHostedService> _logger;

    public IntakeHostedService(IServiceProvider services, ILogger<IntakeHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so a backend restart does not race the watcher's
        // initial scan and try to intake jobs before the project list resolves.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var settings = scope.ServiceProvider.GetRequiredService<ProjectSettingsService>();
                var scanner = scope.ServiceProvider.GetRequiredService<JobScannerService>();
                var intake = scope.ServiceProvider.GetRequiredService<IntakeRunner>();

                var allSettings = settings.GetAll();
                var enabledProjects = scanner.GetWatchPaths()
                    .Where(p => allSettings.TryGetValue(p.Name, out var s)
                                && s.IntakeEnabled is true)
                    .ToList();

                if (enabledProjects.Count == 0)
                {
                    await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                foreach (var entry in enabledProjects)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    ProcessOneProject(scanner, intake, entry);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intake tick failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ProcessOneProject(JobScannerService scanner, IntakeRunner intake, WatchPathEntry entry)
    {
        // Pick the oldest 2-ready job that has not yet been intaked. A null
        // phase counts as human-ready under the compatibility contract; an
        // explicit human-ready also qualifies. intake-running, intake-passed,
        // and intake-blocked are skipped: they are the runner's signal that
        // the verdict already exists.
        var candidate = scanner.ScanAllJobs()
            .Where(j => string.Equals(j.WatchPath, entry.Path, StringComparison.OrdinalIgnoreCase)
                        && j.State == JobStates.Ready
                        && IsAwaitingIntake(j.Phase))
            .OrderBy(j => j.Order)
            .ThenBy(j => j.CreatedAt)
            .FirstOrDefault();
        if (candidate == null) return;

        try
        {
            intake.RunForJob(candidate.Id, entry.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intake run for {JobId} in {Project} failed", candidate.Id, entry.Name);
        }
    }

    private static bool IsAwaitingIntake(string? phase)
    {
        return string.IsNullOrWhiteSpace(phase)
            || string.Equals(phase, LifecyclePhases.HumanReady, StringComparison.Ordinal);
    }
}
