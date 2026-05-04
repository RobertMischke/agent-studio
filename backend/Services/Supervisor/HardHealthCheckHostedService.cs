using System.Text.Json;

namespace OrchestratorApi.Services.Supervisor;

/// <summary>
/// Per-project hosted ticker. Every <c>Supervisor:HardCheckIntervalSeconds</c>
/// (default 10) it observes each project, runs the hard health checks, and
/// appends any advisories to <c>logs/meta/&lt;project&gt;/observations.jsonl</c>.
/// Also writes a heartbeat file so the Layer 3 system review can spot a dead
/// supervisor.
/// </summary>
/// <remarks>
/// Advisory-only. Does not call any emergency primitive. The auto-intervention
/// policy (Task 09) reads these advisories and decides whether to escalate.
/// Filters out advisories whose source is <see cref="SupervisorSource.SoftReasoning"/>
/// or earlier supervisor writes when reasoning about future ticks; for hard
/// checks we recompute fresh from the observation each tick so feedback loops
/// cannot form.
/// </remarks>
public sealed class HardHealthCheckHostedService : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TaskRunnerService _taskRunner;
    private readonly ProjectObservationService _observe;
    private readonly IConfiguration _configuration;
    private readonly ILogger<HardHealthCheckHostedService> _logger;

    public HardHealthCheckHostedService(
        TaskRunnerService taskRunner,
        ProjectObservationService observe,
        IConfiguration configuration,
        ILogger<HardHealthCheckHostedService> logger)
    {
        _taskRunner = taskRunner;
        _observe = observe;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _configuration.GetValue("Supervisor:HardCheckIntervalSeconds", 10);
        var enabled = _configuration.GetValue("Supervisor:HardCheckEnabled", true);
        if (!enabled)
        {
            _logger.LogInformation("HardHealthCheckHostedService disabled via configuration.");
            return;
        }
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; HardHealthCheckHostedService idle.");
            return;
        }

        // Tiny grace period so the rest of DI / runners are wired up first.
        try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(workspace!, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "HardHealthCheck tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var thresholds = HardCheckThresholds.FromConfiguration(_configuration);
        var status = _taskRunner.GetStatus();
        if (status?.Projects == null) return;

        foreach (var project in status.Projects.Keys)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var observation = await _observe.ObserveAsync(project, ct);
                foreach (var advisory in HardHealthChecks.RunAll(observation, thresholds))
                {
                    AppendObservationRecord(workspace, advisory);
                }
                WriteHeartbeat(workspace, project, observation.CapturedAt);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HardHealthCheck failed for project {Project}", project);
            }
        }
    }

    /// <summary>
    /// Append one advisory to <c>observations.jsonl</c>. Public + static so
    /// tests do not need to spin up a hosted service.
    /// </summary>
    public static void AppendObservationRecord(string workspaceRoot, SupervisorAdvisory advisory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var dir = SupervisorLogPaths.ProjectLogDir(workspaceRoot, advisory.Project);
        Directory.CreateDirectory(dir);
        var path = SupervisorLogPaths.ObservationsFile(workspaceRoot, advisory.Project);
        var line = JsonSerializer.Serialize(advisory, Json);
        File.AppendAllText(path, line + Environment.NewLine);
    }

    /// <summary>
    /// Drop a heartbeat for the project. Layer 3 watches this file's mtime to
    /// detect a stuck or dead supervisor.
    /// </summary>
    public static void WriteHeartbeat(string workspaceRoot, string project, DateTime atUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var dir = SupervisorLogPaths.ProjectLogDir(workspaceRoot, project);
        Directory.CreateDirectory(dir);
        var path = SupervisorLogPaths.HeartbeatFile(workspaceRoot, project);
        var payload = JsonSerializer.Serialize(new { project, atUtc }, Json);
        File.WriteAllText(path, payload);
    }
}
