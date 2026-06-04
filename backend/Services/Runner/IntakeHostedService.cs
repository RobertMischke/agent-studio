using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;

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
/// applies to <c>3-progress</c>, not to intake. Because intake holds no
/// code seat, it is parallel-safe: a tick drains every awaiting card in the
/// project (up to <see cref="MaxIntakePerProjectPerTick"/>) rather than one
/// at a time, so a batch of newly-ready cards is prepared together instead
/// of one-per-20s. The cap bounds work on a very large board.
/// </para>
/// </summary>
public sealed class IntakeHostedService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Upper bound on intake runs per project per tick. Intake is cheap and
    /// deterministic, but a single tick should not fan out unboundedly on a
    /// board with hundreds of freshly-ready cards; the remainder is picked up
    /// on the next tick.
    /// </summary>
    internal const int MaxIntakePerProjectPerTick = 16;

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
                var scanner = scope.ServiceProvider.GetRequiredService<TaskScannerService>();
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
                    ProcessProject(scanner, intake, entry, stoppingToken);
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

    private void ProcessProject(TaskScannerService scanner, IntakeRunner intake, WatchPathEntry entry, CancellationToken stoppingToken)
    {
        // Drain every 2-ready job that has not yet been intaked, oldest first,
        // up to the per-tick cap. A null phase counts as human-ready under the
        // compatibility contract; an explicit human-ready also qualifies.
        // intake-running, intake-passed, and intake-blocked are skipped: they
        // are the runner's signal that the verdict already exists. Intake holds
        // no code seat, so processing several cards in one tick is safe — it is
        // the parallel-prep contract, not a coding run.
        var candidates = SelectCandidates(scanner.ScanAllJobs(), entry.Path, MaxIntakePerProjectPerTick);

        foreach (var candidate in candidates)
        {
            if (stoppingToken.IsCancellationRequested) break;
            try
            {
                intake.RunForJob(candidate.Id, entry.Path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Intake run for {JobId} in {Project} failed", candidate.Id, entry.Name);
            }
        }
    }

    /// <summary>
    /// Pure selection surface: the awaiting 2-ready cards for one project,
    /// oldest first, capped at <paramref name="cap"/>. Cards already stamped
    /// intake-running / intake-passed / intake-blocked are excluded so a tick
    /// never re-runs a settled verdict. Extracted so the parallel-prep contract
    /// (drain several awaiting cards per tick, bounded) is unit-testable without
    /// the BackgroundService loop.
    /// </summary>
    internal static IReadOnlyList<TaskInfo> SelectCandidates(IEnumerable<TaskInfo> jobs, string watchPath, int cap)
    {
        return jobs
            .Where(j => string.Equals(j.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase)
                        && j.State == TaskStates.Ready
                        && IsAwaitingIntake(j.Phase))
            .OrderBy(j => j.Order)
            .ThenBy(j => j.CreatedAt)
            .Take(cap)
            .ToList();
    }

    private static bool IsAwaitingIntake(string? phase)
    {
        return string.IsNullOrWhiteSpace(phase)
            || string.Equals(phase, LifecyclePhases.HumanReady, StringComparison.Ordinal);
    }
}
