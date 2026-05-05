using System.Diagnostics;
using System.Text;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Background loop that reads the 4-review lane and acts on tasks that
/// ended in <c>[[TASK_NEEDS_INPUT]]</c>.
///
/// <para>
/// The user framing: when a job lands in 4-review with an unanswered
/// decision request, the orchestrator should read the task, the recent
/// activity, the roadmap, prior decisions, and either answer the
/// question (reissuing the task back to 3-progress with the orchestrator's
/// reply) or escalate to the user with a clear reason. A fast-model
/// Claude (Haiku) session is spawned per pending review with a structured
/// prompt and is expected to respond with a single
/// <c>[[ORCHESTRATOR_DECISION]]</c> sentinel.
/// </para>
///
/// <para>
/// Decisions are append-only: the source of truth for the chain is the
/// per-project journal at
/// <c>{workspace}/logs/decisions/{project}.jsonl</c>, plus the
/// orchestrator chat log written into the job's <c>cli-output.log</c>.
/// The single-state-machine rule still applies: lane transitions go
/// through <see cref="JobStateMachine"/>.
/// </para>
///
/// <para>
/// Off by default. Enable via <c>ReviewDecisionOrchestrator:Enabled = true</c>.
/// Rate-limit reuses the same shape as
/// <see cref="OrchestratorApi.Services.Supervisor.SoftReasoningHostedService"/>
/// (calls per hour, default 30).
/// </para>
/// </summary>
public sealed class ReviewDecisionOrchestrator : BackgroundService
{
    private readonly JobScannerService _scanner;
    private readonly JobStateMachine _stateMachine;
    private readonly OrchestratorChatLog _chatLog;
    private readonly RuntimePromptService _prompts;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewDecisionOrchestrator> _logger;

    private readonly Queue<DateTime> _callTimestamps = new();

    /// <summary>
    /// CLI runner injection point. Tests substitute a deterministic stub.
    /// Args: cliBinary, model, prompt, timeout, ct → captured stdout/stderr.
    /// </summary>
    public Func<string, string, string, TimeSpan, CancellationToken, Task<string>> CliRunner { get; set; }
        = DefaultRunCliAsync;

    public ReviewDecisionOrchestrator(
        JobScannerService scanner,
        JobStateMachine stateMachine,
        OrchestratorChatLog chatLog,
        RuntimePromptService prompts,
        IConfiguration configuration,
        ILogger<ReviewDecisionOrchestrator> logger)
    {
        _scanner = scanner;
        _stateMachine = stateMachine;
        _chatLog = chatLog;
        _prompts = prompts;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false);
        if (!enabled)
        {
            _logger.LogInformation("ReviewDecisionOrchestrator disabled via configuration.");
            return;
        }

        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace))
        {
            _logger.LogWarning("TaskRepository not configured; ReviewDecisionOrchestrator idle.");
            return;
        }

        var intervalSeconds = _configuration.GetValue("ReviewDecisionOrchestrator:IntervalSeconds", 30);

        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(workspace!, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator tick failed"); }

            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One tick: scan every watched project's 4-review lane, find jobs
    /// with an unresolved <c>[[TASK_NEEDS_INPUT]]</c>, and route each
    /// through the decision flow. Public so tests can drive it directly
    /// against a temporary workspace.
    /// </summary>
    public async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var maxPerHour = _configuration.GetValue("ReviewDecisionOrchestrator:CallsPerHour", 30);
        var cliBinary = _configuration.GetValue("ReviewDecisionOrchestrator:Cli", "claude");
        var model = _configuration.GetValue("ReviewDecisionOrchestrator:Model", "claude-haiku-4-5");

        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;
            if (!Directory.Exists(entry.Path)) continue;

            foreach (var pending in EnumeratePending(entry))
            {
                if (ct.IsCancellationRequested) return;

                if (!RateLimitOk(maxPerHour))
                {
                    _logger.LogInformation(
                        "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); deferring {JobId}",
                        maxPerHour, pending.Job.Id);
                    return;
                }

                if (HasOpenDecisionForCurrentTurn(workspace, entry.Name, pending))
                {
                    // We already wrote a decision for this exact NEEDS_INPUT line in
                    // a previous tick (the chat log line that resolves it has not
                    // been persisted yet). Skip to avoid duplicate spend.
                    continue;
                }

                try
                {
                    await ProcessPendingAsync(workspace, entry, pending, cliBinary, model, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "ReviewDecisionOrchestrator failed to process {Project}/{JobId}",
                        entry.Name, pending.Job.Id);
                }
            }
        }
    }

    private async Task ProcessPendingAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string cliBinary,
        string model,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(entry, pending, workspace);
        string response = string.Empty;
        try
        {
            response = await CliRunner(cliBinary, model, prompt, TimeSpan.FromSeconds(120), ct);
            _callTimestamps.Enqueue(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator CLI invocation failed for {Project}/{JobId}",
                entry.Name, pending.Job.Id);
            return;
        }

        var verdict = ReviewDecisionParsing.ParseDecision(response);
        if (verdict == null)
        {
            _logger.LogInformation(
                "ReviewDecisionOrchestrator: no decision sentinel parsed for {Project}/{JobId}; skipping",
                entry.Name, pending.Job.Id);
            ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: pending.Job.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Skipped,
                Reason: "no [[ORCHESTRATOR_DECISION]] sentinel in response",
                Prompt: prompt,
                Response: response,
                FollowUp: string.Empty));
            return;
        }

        switch (verdict.Action)
        {
            case OrchestratorDecisionAction.Reissue:
                await HandleReissueAsync(workspace, entry, pending, prompt, response, verdict, ct);
                break;
            case OrchestratorDecisionAction.Escalate:
                await HandleEscalateAsync(workspace, entry, pending, prompt, response, verdict, ct);
                break;
            case OrchestratorDecisionAction.AcceptAsDone:
                HandleAcceptAsDone(workspace, entry, pending, prompt, response, verdict);
                break;
        }
    }

    private async Task HandleReissueAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string prompt,
        string response,
        OrchestratorDecisionVerdict verdict,
        CancellationToken ct)
    {
        var followUp = BuildReissueFollowUp(verdict);
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        // Append-only chat-log note BEFORE the lane move, so the resolved-marker
        // (the [orchestrator] line) is in place even if the move races with
        // another tick.
        _chatLog.Append(current, OrchestratorMessageKind.Reissue,
            $"Decision: reissue. Reason: {verdict.Reason}. Follow-up: {followUp}");

        var move = _stateMachine.MoveJob(current.Id, JobStates.Progress, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} back to progress: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }
        else
        {
            // Persist the orchestrator's answer next to the prompt so the next
            // run picks it up. A small follow-up file is enough; the runner
            // already knows how to compose continue-prompts from the chat log.
            try
            {
                var moved = _scanner.FindJob(current.Id, entry.Path);
                if (moved != null)
                {
                    var followUpPath = Path.Combine(moved.FolderPath, "orchestrator-follow-up.md");
                    await File.WriteAllTextAsync(
                        followUpPath,
                        $"# Orchestrator follow-up\n\n{followUp}\n",
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ReviewDecisionOrchestrator: failed to write follow-up file for {JobId}", current.Id);
            }
        }

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: verdict.Reason,
            Prompt: prompt,
            Response: response,
            FollowUp: followUp));
    }

    private async Task HandleEscalateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string prompt,
        string response,
        OrchestratorDecisionVerdict verdict,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        // Stay in 4-review; write a [supervisor] banner so the user sees the
        // escalation in the activity log. The chat-log line also acts as the
        // "decision recorded" marker that prevents re-processing on the next
        // tick.
        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator could not decide unattended. Reason: {verdict.Reason}");

        await CreateHumanDecisionIntakeAsync(entry, pending, verdict, ct);

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: verdict.Reason,
            Prompt: prompt,
            Response: response,
            FollowUp: string.Empty));
    }

    private void HandleAcceptAsDone(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string prompt,
        string response,
        OrchestratorDecisionVerdict verdict)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        _chatLog.Append(current, OrchestratorMessageKind.Decision,
            $"Decision: accept-as-done. Reason: {verdict.Reason}");

        var move = _stateMachine.MoveJob(current.Id, JobStates.Completed, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to completed: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: verdict.Reason,
            Prompt: prompt,
            Response: response,
            FollowUp: string.Empty));
    }

    private async Task CreateHumanDecisionIntakeAsync(
        WatchPathEntry entry,
        PendingDecision pending,
        OrchestratorDecisionVerdict verdict,
        CancellationToken ct)
    {
        var slug = Slugify(pending.Job.Id);
        var folderName = $"human-decision-needed-{slug}";
        var preparationDir = Path.Combine(entry.Path, JobStates.Preparation, folderName);

        if (Directory.Exists(preparationDir))
        {
            // Same job already has an open intake. Don't multiply.
            return;
        }

        try
        {
            Directory.CreateDirectory(preparationDir);
            var jobJson = $$"""
            {
              "id": "{{folderName}}",
              "title": "Human decision needed: {{pending.Job.Title}}",
              "state": "{{JobStates.Preparation}}",
              "order": 1,
              "agent": "human",
              "createdAt": "{{DateTime.UtcNow:O}}",
              "priority": "high"
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(preparationDir, "job.json"), jobJson, ct);
            var promptBody = $"# Human decision needed for `{pending.Job.Id}`\n\n" +
                             $"The orchestrator could not decide on this 4-review task unattended.\n\n" +
                             $"**Reason from orchestrator:** {verdict.Reason}\n\n" +
                             $"**Original NEEDS_INPUT reason:** {pending.NeedsInput.Reason ?? "(none provided)"}\n\n" +
                             $"Please review the task in 4-review (`{pending.Job.FolderPath}`) and either answer the agent or change scope.\n";
            await File.WriteAllTextAsync(Path.Combine(preparationDir, "prompt.md"), promptBody, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: failed to create human-decision intake for {JobId}",
                pending.Job.Id);
        }
    }

    private string BuildPrompt(WatchPathEntry entry, PendingDecision pending, string workspace)
    {
        var (taskBody, recentLog) = LoadTaskContext(pending);
        var roadmapExcerpt = LoadRoadmap(entry.RootPath);
        var adrTitles = LoadAdrTitles(entry.RootPath);
        var prevDecisions = LoadPreviousDecisionsSummary(workspace, entry.Name, pending.Job.Id);

        var values = new Dictionary<string, string?>
        {
            ["project"] = entry.Name,
            ["job_id"] = pending.Job.Id,
            ["job_title"] = pending.Job.Title,
            ["needs_input_reason"] = pending.NeedsInput.Reason ?? "(none provided)",
            ["task_body"] = taskBody,
            ["recent_log"] = recentLog,
            ["roadmap_excerpt"] = roadmapExcerpt,
            ["adr_titles"] = adrTitles,
            ["previous_decisions"] = prevDecisions,
        };
        try
        {
            return _prompts.Render("orchestrator-review-decision.md", values);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falling back to inline review-decision prompt");
            return InlineFallbackPrompt(values);
        }
    }

    private static string InlineFallbackPrompt(Dictionary<string, string?> v) =>
        $"You are the orchestrator deciding on a 4-review task that ended in [[TASK_NEEDS_INPUT]].\n" +
        $"Project: {v["project"]} / Job: {v["job_id"]} - {v["job_title"]}\n" +
        $"NEEDS_INPUT reason: {v["needs_input_reason"]}\n\n" +
        $"Task body:\n{v["task_body"]}\n\n" +
        $"Recent log:\n{v["recent_log"]}\n\n" +
        $"Reply with exactly one [[ORCHESTRATOR_DECISION: action=<reissue|escalate|accept-as-done>; reason=<short>]] sentinel then [[TASK_DONE]].";

    private static (string TaskBody, string RecentLog) LoadTaskContext(PendingDecision pending)
    {
        var folder = pending.Job.FolderPath;
        var promptPath = Path.Combine(folder, "prompt.md");
        var task = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        var logPath = JobPaths.CliOutputLog(folder);
        var recent = File.Exists(logPath) ? TailLines(File.ReadAllText(logPath), 200) : string.Empty;
        return (Truncate(task, 4_000), Truncate(recent, 6_000));
    }

    private static string LoadRoadmap(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return string.Empty;
        var path = Path.Combine(rootPath, "ROADMAP.md");
        if (!File.Exists(path)) return string.Empty;
        try { return Truncate(File.ReadAllText(path), 1_500); }
        catch { return string.Empty; }
    }

    private static string LoadAdrTitles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return string.Empty;
        var path = Path.Combine(rootPath, "docs", "architecture-decisions.md");
        if (!File.Exists(path)) return string.Empty;
        try
        {
            var titles = new List<string>();
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("## ADR", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("## ", StringComparison.Ordinal) && line.Contains("ADR", StringComparison.OrdinalIgnoreCase))
                {
                    titles.Add(line.TrimStart('#').Trim());
                    if (titles.Count >= 30) break;
                }
            }
            return string.Join('\n', titles);
        }
        catch { return string.Empty; }
    }

    private static string LoadPreviousDecisionsSummary(string workspace, string project, string jobId)
    {
        var records = ReviewDecisionLog.ReadAll(workspace, project)
            .Where(r => r.JobId == jobId)
            .TakeLast(5)
            .ToList();
        if (records.Count == 0) return "(none)";
        return string.Join('\n', records.Select(r => $"- {r.CreatedAt:u} [{r.Kind}] {r.Reason}"));
    }

    private IEnumerable<PendingDecision> EnumeratePending(WatchPathEntry entry)
    {
        var reviewDir = Path.Combine(entry.Path, JobStates.Review);
        if (!Directory.Exists(reviewDir)) yield break;

        foreach (var jobDir in Directory.GetDirectories(reviewDir))
        {
            JobInfo? info;
            try
            {
                var dirName = Path.GetFileName(jobDir);
                info = _scanner.ScanAllJobs().FirstOrDefault(j =>
                    string.Equals(j.WatchPath, entry.Path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(j.FolderPath, jobDir, StringComparison.OrdinalIgnoreCase));
                if (info == null)
                {
                    // The scanner might not have caught a brand-new folder yet. Skip
                    // this tick; the next one will pick it up.
                    continue;
                }
            }
            catch { continue; }

            var logPath = JobPaths.CliOutputLog(info.FolderPath);
            if (!File.Exists(logPath)) continue;

            string log;
            try { log = File.ReadAllText(logPath); }
            catch { continue; }

            var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);
            if (needs == null) continue;

            yield return new PendingDecision(info, needs);
        }
    }

    private bool HasOpenDecisionForCurrentTurn(string workspace, string project, PendingDecision pending)
    {
        var path = JobPaths.CliOutputLog(pending.Job.FolderPath);
        if (!File.Exists(path)) return false;
        try
        {
            // After we wrote any [orchestrator] / [supervisor] line for this job
            // the FindUnresolvedNeedsInput check would already mark the chain
            // resolved, so reaching this point means the parser saw none. Nothing
            // else to dedupe here today; left as a structured hook for future
            // mid-tick retries.
            return false;
        }
        catch { return false; }
    }

    private static string BuildReissueFollowUp(OrchestratorDecisionVerdict verdict) =>
        $"The orchestrator answered your NEEDS_INPUT request. Decision: {verdict.Reason}. " +
        "Apply this decision and continue the task. End with [[TASK_DONE]] when complete.";

    private static string Slugify(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "unknown";
        var sb = new StringBuilder(id.Length);
        foreach (var ch in id.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_' or '/') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }

    private static string TailLines(string text, int n)
    {
        if (string.IsNullOrEmpty(text) || n <= 0) return string.Empty;
        var lines = text.Split('\n');
        if (lines.Length <= n) return text;
        return string.Join('\n', lines[^n..]);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..max] + "\n... (truncated)";
    }

    private bool RateLimitOk(int maxPerHour)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
        while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff) _callTimestamps.Dequeue();
        return _callTimestamps.Count < maxPerHour;
    }

    private static async Task<string> DefaultRunCliAsync(string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
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

    private sealed record PendingDecision(JobInfo Job, NeedsInputState NeedsInput);
}
