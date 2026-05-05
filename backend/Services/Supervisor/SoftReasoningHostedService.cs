using System.Diagnostics;
using System.Text;
using OrchestratorApi.Services.Bus;

namespace OrchestratorApi.Services.Supervisor;

/// <summary>
/// CLI-driven soft reasoning. Per project, every
/// <c>Supervisor:SoftReasoningIntervalSeconds</c> seconds (default 600), a
/// short Claude (or other CLI) session is spawned with the project state and
/// recent activity, asked to emit one or more
/// <c>[[SUPERVISOR_OBSERVATION: ...]]</c> sentinels, and any returned advisories
/// are appended to <c>observations.jsonl</c> with source
/// <see cref="SupervisorSource.SoftReasoning"/>.
/// </summary>
/// <remarks>
/// Off by default to avoid surprise token spend. Enable via
/// <c>Supervisor:SoftReasoningEnabled = true</c>. Hard rate-limit via
/// <c>Supervisor:SoftReasoningCallsPerHour</c> (default 60).
/// </remarks>
public sealed class SoftReasoningHostedService : BackgroundService
{
    private readonly TaskRunnerService _taskRunner;
    private readonly ProjectObservationService _observe;
    private readonly RuntimePromptService _prompts;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SoftReasoningHostedService> _logger;
    private readonly AgentMessageBusBridge? _bus;

    private readonly Queue<DateTime> _callTimestamps = new();

    public SoftReasoningHostedService(
        TaskRunnerService taskRunner,
        ProjectObservationService observe,
        RuntimePromptService prompts,
        IConfiguration configuration,
        ILogger<SoftReasoningHostedService> logger,
        AgentMessageBusBridge? bus = null)
    {
        _taskRunner = taskRunner;
        _observe = observe;
        _prompts = prompts;
        _configuration = configuration;
        _logger = logger;
        _bus = bus;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue("Supervisor:SoftReasoningEnabled", false);
        if (!enabled)
        {
            _logger.LogInformation("SoftReasoningHostedService disabled via configuration.");
            return;
        }

        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; SoftReasoningHostedService idle.");
            return;
        }

        var intervalSeconds = _configuration.GetValue("Supervisor:SoftReasoningIntervalSeconds", 600);

        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(workspace!, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "SoftReasoning tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var status = _taskRunner.GetStatus();
        if (status?.Projects == null) return;

        var maxPerHour = _configuration.GetValue("Supervisor:SoftReasoningCallsPerHour", 60);
        var cliBinary = _configuration.GetValue("Supervisor:SoftReasoningCli", "claude");
        var model = _configuration.GetValue("Supervisor:SoftReasoningModel", "claude-haiku-4-5");

        foreach (var (project, projectStatus) in status.Projects)
        {
            if (!RateLimitOk(maxPerHour))
            {
                _logger.LogInformation("SoftReasoning rate limit reached ({MaxPerHour}/h); skipping {Project}", maxPerHour, project);
                continue;
            }

            try
            {
                var observation = await _observe.ObserveAsync(project, ct);
                if (string.IsNullOrEmpty(observation.CurrentJobId)) continue; // nothing to reason about while idle
                var prompt = BuildPrompt(project, observation);
                var output = await RunCliAsync(cliBinary, model, prompt, TimeSpan.FromSeconds(120), ct);
                _callTimestamps.Enqueue(DateTime.UtcNow);
                var advisories = SoftReasoningParsing.Parse(output, project, DateTime.UtcNow, observation.CurrentJobId);
                foreach (var advisory in advisories)
                {
                    HardHealthCheckHostedService.AppendObservationRecord(workspace, advisory);
                    try { _ = _bus?.EmitAdvisoryAsync(advisory); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of soft-reasoning advisory failed for {Project}", project); }
                }
                _logger.LogInformation("SoftReasoning {Project}: {Count} observation(s)", project, advisories.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SoftReasoning failed for {Project}", project);
            }
        }
    }

    private bool RateLimitOk(int maxPerHour)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff) _callTimestamps.Dequeue();
        return _callTimestamps.Count < maxPerHour;
    }

    private string BuildPrompt(string project, SupervisorObservation o)
    {
        var samples = string.Join("\n", o.RecentAgentSamples.TakeLast(20));
        var decisions = string.Join("\n", o.RecentDecisions.Select(d => $"{d.At:HH:mm:ss} [{d.Kind}] {d.Summary}"));
        var values = new Dictionary<string, string?>
        {
            ["project"] = project,
            ["runner_status"] = o.RunnerStatus,
            ["current_job_id"] = o.CurrentJobId ?? string.Empty,
            ["current_run_state"] = o.CurrentRunState ?? string.Empty,
            ["last_progress_at"] = o.LastProgressAt?.ToString("u") ?? "-",
            ["error_cli"] = o.ErrorCounts.CliErrorsLastHour.ToString(),
            ["error_orch"] = o.ErrorCounts.OrchestratorErrorsLastHour.ToString(),
            ["error_failures"] = o.ErrorCounts.RunFailuresLastHour.ToString(),
            ["recent_samples"] = samples,
            ["recent_decisions"] = decisions,
        };
        try
        {
            return _prompts.Render("supervisor-soft-reasoning.md", values);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to inline supervisor-soft-reasoning prompt template");
            return InlineFallback(values);
        }
    }

    private static string InlineFallback(Dictionary<string, string?> v) =>
        $"Project: {v["project"]}\nState: {v["runner_status"]} active={v["current_job_id"]}\nLast progress: {v["last_progress_at"]}\n\nRecent samples:\n{v["recent_samples"]}\n\nEmit observations as [[SUPERVISOR_OBSERVATION: severity=<info|warn|high>; topic=<tag>; message=<one-line>]] then [[TASK_DONE]].";

    private static async Task<string> RunCliAsync(string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cli,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--dangerously-skip-permissions");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);

        using var p = Process.Start(psi);
        if (p == null) return string.Empty;
        var sb = new StringBuilder();
        var readTask = Task.Run(async () =>
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync(ct)) != null)
            {
                sb.AppendLine(line);
            }
        }, ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            await readTask;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
        }
        return sb.ToString();
    }
}
