using System.Text.Json;
using OrchestratorApi.Services.Bus;

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
    private readonly AgentMessageBusBridge? _bus;

    /// <summary>
    /// Cycle 2d advisory dedupe table. Key = (project, topic, jobId);
    /// value = last emit timestamp. The supervisor used to write the same
    /// no-progress / error-burst advisory every 10 s for the duration of
    /// the failure (1830 no-progress + 1555 error-burst rows accumulated
    /// against one wedged session in the 2026-05-09 incident); the bus
    /// mirror was the worst offender because every tick re-rendered
    /// timeline rows on every connected client. Cooldown suppresses the
    /// rebroadcast while the underlying condition keeps firing; the
    /// canonical observations.jsonl gets a single compact "still active"
    /// row at every cooldown boundary instead of N full duplicates.
    /// In-memory only; restarts of the host clear the table, which is
    /// fine because the first tick after restart re-fires the advisory.
    /// </summary>
    private readonly Dictionary<(string project, string topic, string? jobId), DateTime> _advisoryCooldown = new();
    private readonly Lock _cooldownLock = new();

    public HardHealthCheckHostedService(
        TaskRunnerService taskRunner,
        ProjectObservationService observe,
        IConfiguration configuration,
        ILogger<HardHealthCheckHostedService> logger,
        AgentMessageBusBridge? bus = null)
    {
        _taskRunner = taskRunner;
        _observe = observe;
        _configuration = configuration;
        _logger = logger;
        _bus = bus;
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
                var cooldown = TimeSpan.FromSeconds(_configuration.GetValue("Supervisor:AdvisoryCooldownSeconds", 300));
                foreach (var advisory in HardHealthChecks.RunAll(observation, thresholds))
                {
                    if (IsWithinCooldown(advisory, cooldown))
                    {
                        // Same condition still active; skip both the durable
                        // append and the bus mirror so the project screen
                        // does not see N copies of the same warning.
                        continue;
                    }
                    AppendObservationRecord(workspace, advisory);
                    // Mirror to the Agent Message Bus. The legacy
                    // observations.jsonl remains canonical (auto-intervention,
                    // chat-note summary still read it); the bus is a typed
                    // projection so the project screen can show advisories
                    // alongside other timeline events.
                    try { _ = _bus?.EmitAdvisoryAsync(advisory); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of advisory failed for {Project}", project); }
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
    /// Returns true if an advisory with the same (project, topic, jobId)
    /// was already emitted within the cooldown window. Updates the table
    /// in the same call so subsequent ticks within the window also skip.
    /// quota-critical advisories bypass cooldown because they encode a
    /// budget the operator must see every tick.
    /// </summary>
    private bool IsWithinCooldown(SupervisorAdvisory advisory, TimeSpan cooldown)
    {
        if (advisory.Topic == "quota-critical") return false; // never suppress
        var key = (advisory.Project, advisory.Topic, advisory.JobId);
        lock (_cooldownLock)
        {
            if (_advisoryCooldown.TryGetValue(key, out var lastAt))
            {
                if (advisory.CreatedAt - lastAt < cooldown) return true;
            }
            _advisoryCooldown[key] = advisory.CreatedAt;
        }
        return false;
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
