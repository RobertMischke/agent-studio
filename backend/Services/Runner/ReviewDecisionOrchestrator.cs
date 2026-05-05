using System.Diagnostics;
using System.Text;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Background loop that reads the 4-review lane and acts on tasks that
/// ended in <c>[[TASK_NEEDS_INPUT]]</c>, <c>[[TASK_NOOP]]</c>, or
/// <c>[[TASK_BLOCKED]]</c>.
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
/// NOOP is treated as a recoverable signal, not a terminal state: a job
/// that ends in <c>[[TASK_NOOP]]</c> with a real prompt body is reissued
/// once with a sharpened framing (reusing
/// <see cref="RunOutcomePolicy.BuildReissueFollowupPrompt"/>); a NOOP on
/// an empty / placeholder prompt escalates to a human-decision intake;
/// repeated NOOPs past the shared reissue budget escalate too. The
/// branch is deterministic (no fast-model CLI call) so it does not
/// charge the per-hour rate budget.
/// </para>
///
/// <para>
/// BLOCKED is also handled deterministically: the agent has explicitly
/// declared that it cannot proceed, so the orchestrator does not retry
/// silently. Every BLOCKED in 4-review is escalated to a
/// <c>human-decision-needed-*</c> intake under <c>1-preparation</c> and
/// surfaced via the chat log + Agent Message Bus so the user sees one
/// "this card needs your attention" entry on the workspace banner.
/// </para>

/// <para>
/// On boot the service performs one explicit backfill sweep across every
/// watched project's 4-review lane before the recurring tick loop
/// starts. This catches jobs that landed in 4-review while the backend
/// was offline (or before this service shipped) and brings them into the
/// orchestrator-review pipeline immediately, rather than waiting for the
/// next scheduled tick.
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
    /// <summary>
    /// Maximum number of automatic reissues the review-decision tick will
    /// drive against a single job before escalating. Counts every Reissue
    /// record in the per-project decision journal for that job, so the
    /// budget is shared between NEEDS_INPUT-driven and NOOP-driven
    /// reissues (the agent only sees one stream of follow-ups).
    /// </summary>
    public const int MaxAutoReissueAttempts = 2;

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
        var bootDelaySeconds = _configuration.GetValue("ReviewDecisionOrchestrator:BootDelaySeconds", 5);

        // Brief warm-up so the scanner has indexed the lanes; then the
        // explicit boot sweep picks up anything that landed in 4-review
        // while the backend was offline (or before this service existed)
        // before the recurring loop kicks in.
        if (bootDelaySeconds > 0)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(bootDelaySeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }

        try
        {
            _logger.LogInformation("ReviewDecisionOrchestrator boot sweep starting (one-shot full backfill).");
            await TickOnceAsync(workspace!, stoppingToken);
            _logger.LogInformation("ReviewDecisionOrchestrator boot sweep complete; entering recurring tick loop.");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator boot sweep failed"); }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await TickOnceAsync(workspace!, stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator tick failed"); }
        }
    }

    /// <summary>
    /// One tick: scan every watched project's 4-review lane, find jobs
    /// with an unresolved <c>[[TASK_NEEDS_INPUT]]</c> or
    /// <c>[[TASK_NOOP]]</c>, and route each through the decision flow.
    /// Public so tests can drive it directly against a temporary
    /// workspace.
    /// </summary>
    public async Task TickOnceAsync(string workspace, CancellationToken ct)
    {
        var maxPerHour = _configuration.GetValue("ReviewDecisionOrchestrator:CallsPerHour", 30);
        var cliBinary = _configuration.GetValue("ReviewDecisionOrchestrator:Cli", "claude");
        var model = _configuration.GetValue("ReviewDecisionOrchestrator:Model", "claude-haiku-4-5");
        var maxReissues = _configuration.GetValue("ReviewDecisionOrchestrator:MaxAutoReissueAttempts", MaxAutoReissueAttempts);

        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Path)) continue;
            if (!Directory.Exists(entry.Path)) continue;

            foreach (var pending in EnumeratePending(entry))
            {
                if (ct.IsCancellationRequested) return;

                try
                {
                    if (pending.Kind == ReviewSignalKind.NoOp)
                    {
                        // NOOP is fully deterministic: no fast-model call,
                        // no per-hour rate consumption.
                        await ProcessNoOpAsync(workspace, entry, pending, maxReissues, ct);
                        continue;
                    }

                    if (pending.Kind == ReviewSignalKind.Blocked)
                    {
                        // BLOCKED is also deterministic: the agent has
                        // declared it cannot proceed, so we always
                        // escalate to a human-decision intake rather
                        // than spending a fast-model call to re-decide.
                        await ProcessBlockedAsync(workspace, entry, pending, ct);
                        continue;
                    }

                    if (!RateLimitOk(maxPerHour))
                    {
                        _logger.LogInformation(
                            "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); deferring {JobId}",
                            maxPerHour, pending.Job.Id);
                        return;
                    }

                    await ProcessNeedsInputAsync(workspace, entry, pending, cliBinary, model, ct);
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

    private async Task ProcessNeedsInputAsync(
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

    private async Task ProcessNoOpAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        int maxReissues,
        CancellationToken ct)
    {
        var (taskTitle, taskBody) = LoadTaskTitleAndBody(pending);
        var promptUsable = IsPromptUsable(taskTitle, taskBody);

        // Branch 1: empty / placeholder prompt -> escalate. The agent's
        // NOOP is justified; the human needs to scope the task before any
        // run is worthwhile.
        if (!promptUsable)
        {
            var reason = "Task prompt is empty or placeholder; agent's [[TASK_NOOP]] is justified.";
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct);
            return;
        }

        // Branch 2: budget exhausted -> escalate. We share the counter
        // with the existing NEEDS_INPUT-driven reissues so a job that has
        // already been retried multiple times by either path doesn't get
        // double-spent here.
        var prior = CountPriorReissues(workspace, entry.Name, pending.Job.Id);
        if (prior >= maxReissues)
        {
            var reason = $"NOOP after {prior} prior orchestrator reissue(s); user attention required.";
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct);
            return;
        }

        // Branch 3: usable prompt + budget left -> reissue with the
        // sharpened framing from RunOutcomePolicy.
        var followUp = RunOutcomePolicy.BuildReissueFollowupPrompt(taskBody);
        await ReissueNoOpAsync(workspace, entry, pending, followUp, ct);
    }

    private async Task ReissueNoOpAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string followUp,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var reason = "Agent emitted [[TASK_NOOP]] but the task description is real; reissuing with sharpened framing.";

        _chatLog.Append(current, OrchestratorMessageKind.Reissue,
            $"Decision: reissue (NOOP recovery). Reason: {reason}");

        var move = _stateMachine.MoveJob(current.Id, JobStates.Progress, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} back to progress after NOOP: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }
        else
        {
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
                _logger.LogWarning(ex,
                    "ReviewDecisionOrchestrator: failed to write NOOP follow-up file for {JobId}",
                    current.Id);
            }
        }

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: reason,
            Prompt: "(deterministic NOOP branch)",
            Response: "(no fast-model call)",
            FollowUp: followUp));
    }

    private async Task EscalateNoOpAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string reason,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator could not auto-recover NOOP. Reason: {reason}");

        var verdict = new OrchestratorDecisionVerdict(OrchestratorDecisionAction.Escalate, reason);
        await CreateHumanDecisionIntakeAsync(entry, pending, verdict, ct);

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic NOOP branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty));
    }

    private async Task ProcessBlockedAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var reason = string.IsNullOrWhiteSpace(pending.Reason)
            ? "Agent emitted [[TASK_BLOCKED]] without further detail; user attention required."
            : $"Agent emitted [[TASK_BLOCKED]]: {pending.Reason}";

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator escalated BLOCKED to human review. Reason: {reason}");

        var verdict = new OrchestratorDecisionVerdict(OrchestratorDecisionAction.Escalate, reason);
        await CreateHumanDecisionIntakeAsync(entry, pending, verdict, ct);

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic BLOCKED branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty));
    }

    private static int CountPriorReissues(string workspace, string project, string jobId)
    {
        return ReviewDecisionLog.ReadAll(workspace, project)
            .Count(r => r.JobId == jobId && r.Kind == ReviewDecisionKind.Reissue);
    }

    private static (string Title, string Body) LoadTaskTitleAndBody(PendingDecision pending)
    {
        var title = pending.Job.Title ?? string.Empty;
        var promptPath = Path.Combine(pending.Job.FolderPath, "prompt.md");
        var body = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;
        return (title, body);
    }

    /// <summary>
    /// Heuristic: a prompt is "usable" if it has a non-empty title, a
    /// non-empty body, and the body has at least 20 characters of
    /// non-heading content. Catches the obvious placeholder cases
    /// (untouched template, single heading, "TODO: fill in") without
    /// trying to score real prose.
    /// </summary>
    internal static bool IsPromptUsable(string title, string body)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var trimmedTitle = title.Trim();
        if (LooksLikePlaceholder(trimmedTitle)) return false;
        if (string.IsNullOrWhiteSpace(body)) return false;

        var contentChars = 0;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) continue; // markdown heading
            if (LooksLikePlaceholder(line)) continue;
            contentChars += line.Length;
        }
        return contentChars >= 20;
    }

    private static bool LooksLikePlaceholder(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var lower = s.ToLowerInvariant();
        return lower.Contains("{title}")
            || lower.Contains("<title>")
            || lower.Contains("placeholder")
            || lower == "todo"
            || lower.StartsWith("todo:")
            || lower.StartsWith("tbd")
            || lower == "(empty)";
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
                             $"**Original signal:** {OriginalSignalLabel(pending.Kind)}\n\n" +
                             $"**Original reason:** {pending.Reason ?? "(none provided)"}\n\n" +
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
            ["needs_input_reason"] = pending.Reason ?? "(none provided)",
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

            // The agent contract guarantees only one terminal sentinel per
            // run, but a job folder may have been continued and accumulated
            // multiple kinds across runs. Pick the latest unresolved one so
            // we act on the most recent state.
            var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);
            var noop = ReviewDecisionParsing.FindUnresolvedNoOp(log);
            var blocked = ReviewDecisionParsing.FindUnresolvedBlocked(log);

            if (needs == null && noop == null && blocked == null) continue;

            int needsLine = needs?.LineNumber ?? -1;
            int noopLine = noop?.LineNumber ?? -1;
            int blockedLine = blocked?.LineNumber ?? -1;

            if (blockedLine >= needsLine && blockedLine >= noopLine && blocked != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.Blocked, blocked.LineNumber, blocked.Reason, NeedsInput: null);
            }
            else if (noopLine > needsLine && noop != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.NoOp, noop.LineNumber, noop.Reason, NeedsInput: null);
            }
            else if (needs != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.NeedsInput, needs.LineNumber, needs.Reason, NeedsInput: needs);
            }
        }
    }

    private static string BuildReissueFollowUp(OrchestratorDecisionVerdict verdict) =>
        $"The orchestrator answered your NEEDS_INPUT request. Decision: {verdict.Reason}. " +
        "Apply this decision and continue the task. End with [[TASK_DONE]] when complete.";

    private static string OriginalSignalLabel(ReviewSignalKind kind) => kind switch
    {
        ReviewSignalKind.NoOp     => "[[TASK_NOOP]]",
        ReviewSignalKind.Blocked  => "[[TASK_BLOCKED]]",
        _                         => "[[TASK_NEEDS_INPUT]]"
    };

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

    private enum ReviewSignalKind
    {
        NeedsInput,
        NoOp,
        Blocked
    }

    private sealed record PendingDecision(
        JobInfo Job,
        ReviewSignalKind Kind,
        int LineNumber,
        string? Reason,
        NeedsInputState? NeedsInput);
}
