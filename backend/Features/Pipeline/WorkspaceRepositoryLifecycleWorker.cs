using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// Keeps the platform-owned workspace repository bounded. Runtime-state
/// classification is enforced on boot, then tracked drift and Git object
/// maintenance run on the configured cadence after the host load gate opens.
/// </summary>
public sealed class WorkspaceRepositoryLifecycleWorker : BackgroundService
{
    private readonly WorkspaceArtifactCommitService _commits;
    private readonly ILoadThrottleGate _loadGate;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceRepositoryLifecycleWorker> _logger;
    private readonly TimeProvider _time;

    public WorkspaceRepositoryLifecycleWorker(
        WorkspaceArtifactCommitService commits,
        ILoadThrottleGate loadGate,
        IConfiguration configuration,
        ILogger<WorkspaceRepositoryLifecycleWorker> logger,
        TimeProvider? time = null)
    {
        _commits = commits;
        _loadGate = loadGate;
        _configuration = configuration;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;

        await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return;

        try
        {
            await _loadGate.WaitUntilReadyAsync("workspace-repository-maintenance", ct).ConfigureAwait(false);

            Report("runtime-policy", _commits.TryApplyRuntimeStatePolicy(workspace));
            Report("tracked-sweep", _commits.TryCommitTrackedSweep(workspace));
            Report("git-maintenance", _commits.TryRunMaintenance(workspace));
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "WorkspaceRepositoryLifecycleWorker: graceful shutdown");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "workspace-repository-lifecycle failed repo={Repo}", workspace);
        }
    }

    private bool Enabled =>
        _configuration.GetValue<bool?>("WorkspaceRepositoryMaintenance:Enabled") ?? true;

    private TimeSpan Interval => TimeSpan.FromMinutes(Math.Clamp(
        _configuration.GetValue<int?>("WorkspaceRepositoryMaintenance:IntervalMinutes") ?? 60,
        5, 24 * 60));

    private void Report(string operation, WorkspaceArtifactCommitResult result)
    {
        if (!result.Success)
            _logger.LogWarning(
                "workspace-repository-lifecycle-operation-failed operation={Operation} error={Error}",
                operation, result.Error);
        else
            _logger.LogInformation(
                "workspace-repository-lifecycle-operation-complete operation={Operation} changed={Changed} detail={Detail}",
                operation, result.DidCommit, result.Error ?? result.Steps ?? "complete");
    }
}
