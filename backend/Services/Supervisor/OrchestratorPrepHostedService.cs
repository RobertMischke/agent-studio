using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services.Supervisor;

/// <summary>
/// ADR-0026 orchestrator-prep loop. Per project, pulls eligible jobs out of
/// <c>1-preparation</c> into <c>1a-orchestrator-prep</c>, runs the pure-rule
/// engine in <see cref="OrchestratorPrepRules"/> against the prompt, and
/// then either ships the task to <c>2-ready</c>, bounces it to
/// <c>1b-needs-human-review</c>, or leaves it in <c>1a-orchestrator-prep</c>
/// with an iteration counter incremented.
///
/// <para>Off by default. Enable with <c>Orchestrator:PrepEnabled = true</c>.
/// Rate-limited via <c>Orchestrator:PrepCallsPerHour</c> (default 30).</para>
///
/// <para>The loop is sequential-per-project: it never runs while a project's
/// runner is mid-task. ADR-0001 still holds.</para>
///
/// <para>This first slice is heuristic-only (no LLM calls). The clarity
/// score in <see cref="OrchestratorPrepRules.ScoreClarity"/> is auditable
/// and cheap; a fast-model upgrade is a follow-up slice that does not
/// change the lane shape or the autonomy gating.</para>
/// </summary>
public sealed class OrchestratorPrepHostedService : BackgroundService
{
    private readonly JobScannerService _scanner;
    private readonly JobStateMachine _states;
    private readonly ProjectSettingsService _settings;
    private readonly OrchestratorChatLog _chatLog;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrchestratorPrepHostedService> _logger;

    private readonly Queue<DateTime> _callTimestamps = new();

    public OrchestratorPrepHostedService(
        JobScannerService scanner,
        JobStateMachine states,
        ProjectSettingsService settings,
        OrchestratorChatLog chatLog,
        IConfiguration configuration,
        ILogger<OrchestratorPrepHostedService> logger)
    {
        _scanner = scanner;
        _states = states;
        _settings = settings;
        _chatLog = chatLog;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = _configuration.GetValue("Orchestrator:PrepTickSeconds", 60);

        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_configuration.GetValue("Orchestrator:PrepEnabled", false))
                    await TickOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "OrchestratorPrep tick failed"); }

            intervalSeconds = _configuration.GetValue("Orchestrator:PrepTickSeconds", 60);
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Runs one pass over every watched project. Public for tests.
    /// </summary>
    public Task TickOnceAsync(CancellationToken ct)
    {
        var maxPerHour = _configuration.GetValue("Orchestrator:PrepCallsPerHour", 30);
        var maxIterations = _configuration.GetValue("Orchestrator:MaxPrepIterations", 3);
        var queueFloor = _configuration.GetValue("Orchestrator:QueueFloor", 2);

        var entries = _scanner.GetWatchPaths();
        foreach (var entry in entries)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                ProcessProject(entry.Name, entry.Path, maxIterations, queueFloor, maxPerHour);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OrchestratorPrep failed for project {Project}", entry.Name);
            }
        }
        return Task.CompletedTask;
    }

    private void ProcessProject(string projectName, string watchPath, int maxIterations, int queueFloor, int maxPerHour)
    {
        var level = _settings.Get(projectName).AutonomyLevel ?? 2;
        if (level == 0) return; // manual: never moves a task forward

        var allJobs = _scanner.ScanAllJobs().Where(j => j.WatchPath == watchPath).ToList();
        var prep = allJobs.Where(j => j.State == JobStates.Preparation).OrderBy(j => j.Order).ToList();
        var orchPrep = allJobs.Where(j => j.State == JobStates.OrchestratorPrep).OrderBy(j => j.Order).ToList();
        var ready = allJobs.Where(j => j.State == JobStates.Ready).OrderBy(j => j.Order).ToList();

        // 1) Refill: when 2-ready is below the floor, pull the next eligible
        //    1-preparation job into 1a-orchestrator-prep so the prep loop has
        //    something to iterate on. Skipped at level 0 (already returned).
        if (ready.Count < queueFloor && orchPrep.Count == 0 && prep.Count > 0)
        {
            var head = prep.First();
            var moved = _states.MoveJob(head.Id, JobStates.OrchestratorPrep, head.WatchPath);
            if (moved.Status == MoveJobStatus.Success)
            {
                _logger.LogInformation(
                    "OrchestratorPrep pulled {JobId} into {Lane} for project {Project} (queue floor {Floor})",
                    head.Id, JobStates.OrchestratorPrep, projectName, queueFloor);
                _chatLog.Append(head, OrchestratorMessageKind.Decision,
                    $"orchestrator-prep: refill (queue at {ready.Count}, floor {queueFloor}); pulled into {JobStates.OrchestratorPrep}");
            }
        }

        // 2) Decide on each task currently in 1a-orchestrator-prep.
        foreach (var job in orchPrep)
        {
            if (!RateLimitOk(maxPerHour))
            {
                _logger.LogInformation(
                    "OrchestratorPrep rate limit reached ({MaxPerHour}/h); skipping further work this tick", maxPerHour);
                return;
            }

            var promptText = ReadPromptText(job);
            var prevText = "";
            var nextText = "";
            // Heuristic neighbour-context: previous = last completed, next = next ready.
            // Both are best-effort; missing files don't break the score.
            try
            {
                var prevJob = ready.LastOrDefault();
                if (prevJob != null) prevText = ReadPromptText(prevJob);
                var nextJob = ready.FirstOrDefault();
                if (nextJob != null) nextText = ReadPromptText(nextJob);
            }
            catch { /* best-effort */ }

            var iteration = ReadIteration(job);
            var input = new OrchestratorPrepRules.PrepInput
            {
                PromptText = promptText,
                Iteration = iteration,
                MaxIterations = level == 1 ? 1 : maxIterations,
                AutonomyLevel = level,
                PrevPromptText = prevText,
                NextPromptText = nextText,
            };

            var decision = OrchestratorPrepRules.Decide(input);
            _callTimestamps.Enqueue(DateTime.UtcNow);

            ApplyDecision(job, decision, iteration);
        }
    }

    private void ApplyDecision(JobInfo job, OrchestratorPrepRules.PrepDecision decision, int iteration)
    {
        switch (decision.Verdict)
        {
            case OrchestratorPrepRules.Verdict.Accept:
                _states.MoveJob(job.Id, JobStates.Ready, job.WatchPath);
                _chatLog.Append(job, OrchestratorMessageKind.Decision,
                    decision.Note ?? $"orchestrator-prep: accept (clarity {decision.Clarity:F2}); -> {JobStates.Ready}");
                break;

            case OrchestratorPrepRules.Verdict.Bounce:
                WriteBounceMetadata(job, decision);
                _states.MoveJob(job.Id, JobStates.NeedsHumanReview, job.WatchPath);
                _chatLog.Append(job, OrchestratorMessageKind.GiveUp,
                    $"orchestrator-prep: bounce ({decision.BounceReason}); clarity {decision.Clarity:F2}; -> {JobStates.NeedsHumanReview}");
                break;

            case OrchestratorPrepRules.Verdict.Iterate:
                WriteIterationMetadata(job, iteration + 1, decision);
                _chatLog.Append(job, OrchestratorMessageKind.Decision,
                    $"orchestrator-prep: iterate {iteration + 1} (clarity {decision.Clarity:F2})");
                break;

            case OrchestratorPrepRules.Verdict.Hold:
                // No move. Level 0 stays in 1-preparation; nothing logged so the
                // orchestrator does not spam the chat at every tick.
                break;
        }
    }

    private static string ReadPromptText(JobInfo job)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "prompt.md");
            return File.Exists(p) ? File.ReadAllText(p) : "";
        }
        catch { return ""; }
    }

    private int ReadIteration(JobInfo job)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "orchestrator-prep.json");
            if (!File.Exists(p)) return 0;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("iteration", out var it) ? it.GetInt32() : 0;
        }
        catch { return 0; }
    }

    private void WriteIterationMetadata(JobInfo job, int newIteration, OrchestratorPrepRules.PrepDecision decision)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "orchestrator-prep.json");
            var payload = new
            {
                iteration = newIteration,
                lastVerdict = decision.Verdict.ToString().ToLowerInvariant(),
                lastClarity = decision.Clarity,
                updatedAt = DateTime.UtcNow,
            };
            File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write orchestrator-prep.json for {JobId}", job.Id);
        }
    }

    private void WriteBounceMetadata(JobInfo job, OrchestratorPrepRules.PrepDecision decision)
    {
        try
        {
            var p = Path.Combine(job.FolderPath, "orchestrator-prep.json");
            var payload = new
            {
                iteration = 0,
                lastVerdict = "bounce",
                lastClarity = decision.Clarity,
                bounceReason = decision.BounceReason.ToString(),
                bouncedAt = DateTime.UtcNow,
            };
            File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write orchestrator-prep.json bounce metadata for {JobId}", job.Id);
        }
    }

    private bool RateLimitOk(int maxPerHour)
    {
        var cutoff = DateTime.UtcNow.AddHours(-1);
        while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff) _callTimestamps.Dequeue();
        return _callTimestamps.Count < maxPerHour;
    }
}
