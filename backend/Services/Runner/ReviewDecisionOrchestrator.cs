using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.TaskAccess;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Background loop that reads the <c>4-auto-review</c> lane (ADR-0025) and
/// acts on tasks that ended in <c>[[TASK_NEEDS_INPUT]]</c>,
/// <c>[[TASK_NOOP]]</c>, or <c>[[TASK_BLOCKED]]</c>. Reissue moves the
/// task to <c>2-ready</c> at order 0 (next pickup) so the runner picks
/// it ahead of fresh queued tasks but never displaces a currently
/// running job - the race where the runner saw an empty
/// <c>3-progress</c> mid-verdict and picked the next ready job is gone.
/// Accept-as-done moves the task forward to <c>5-human-review</c> (the
/// user always gets the final say on completion); escalate also moves
/// it to <c>5-human-review</c> with a <c>[supervisor]</c> chat-note
/// explaining why the orchestrator could not decide.
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
/// through <see cref="TaskStateMachine"/>.
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

    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _stateMachine;
    private readonly ITaskAccess _taskAccess;
    private readonly OrchestratorChatLog _chatLog;
    private readonly RuntimePromptService _prompts;
    private readonly AspectRunnerService _aspectRunner;
    private readonly AutoReviewStatusSnapshot _statusSnapshot;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ReviewDecisionOrchestrator> _logger;
    private readonly TaskSessionLog? _sessions;
    private readonly GitService? _git;

    /// <summary>
    /// Default aspect runner ids when <c>ReviewDecisionOrchestrator:AspectRunners</c>
    /// is not configured. ADR-0025 slice 1 ships these four; missing
    /// config falls back to default-on rather than no-aspects.
    /// </summary>
    private static readonly string[] DefaultAspectRunners =
    {
        "requirement-fit",
        "code-quality",
        "documentation-impact",
        "tests-and-evidence"
    };

    private readonly Queue<DateTime> _callTimestamps = new();

    /// <summary>
    /// CLI runner injection point. Tests substitute a deterministic stub.
    /// Args: cliBinary, model, prompt, timeout, ct → captured stdout/stderr.
    /// </summary>
    public Func<string, string, string, TimeSpan, CancellationToken, Task<string>> CliRunner { get; set; }
        = DefaultRunCliAsync;

    private readonly OrchestratorApi.Services.AdHoc.AdHocUsageRecorder? _usage;
    private readonly OrchestratorApi.Services.Cli.OneShot.CliOneShotRegistry? _oneShotRegistry;
    private readonly PipelineExecutionLog? _pipelineLog;
    private readonly ILintScssRunner? _lintScssRunner;
    private readonly TimelineLog? _timeline;
    private readonly ProjectSettingsService? _projectSettings;

    /// <summary>
    /// Stable prefix on the <c>Reason</c> field of every
    /// <see cref="ReviewDecisionRecord"/> that the lint-scss post-step
    /// emitted. Used for the infinite-spin guard (a prior reissue with
    /// this prefix means the agent already had one chance to clear the
    /// gate; the next failure escalates to human review).
    /// </summary>
    internal const string LintScssReissueReasonPrefix = "lint-scss reissue: ";

    public ReviewDecisionOrchestrator(
        TaskScannerService scanner,
        TaskStateMachine stateMachine,
        ITaskAccess taskAccess,
        OrchestratorChatLog chatLog,
        RuntimePromptService prompts,
        AspectRunnerService aspectRunner,
        AutoReviewStatusSnapshot statusSnapshot,
        IConfiguration configuration,
        ILogger<ReviewDecisionOrchestrator> logger,
        OrchestratorApi.Services.AdHoc.AdHocUsageRecorder? usage = null,
        OrchestratorApi.Services.Cli.OneShot.CliOneShotRegistry? oneShotRegistry = null,
        TaskSessionLog? sessions = null,
        GitService? git = null,
        PipelineExecutionLog? pipelineLog = null,
        ILintScssRunner? lintScssRunner = null,
        TimelineLog? timeline = null,
        ProjectSettingsService? projectSettings = null)
    {
        _scanner = scanner;
        _stateMachine = stateMachine;
        _taskAccess = taskAccess;
        _chatLog = chatLog;
        _prompts = prompts;
        _aspectRunner = aspectRunner;
        _statusSnapshot = statusSnapshot;
        _configuration = configuration;
        _logger = logger;
        _usage = usage;
        _oneShotRegistry = oneShotRegistry;
        _sessions = sessions;
        _git = git;
        _pipelineLog = pipelineLog;
        _lintScssRunner = lintScssRunner;
        _timeline = timeline;
        _projectSettings = projectSettings;

        // Route production CLI calls through ICliOneShot (stdin-piped,
        // stderr-captured, exit-code-surfaced). The CliRunner property
        // stays the test seam.
        if (_oneShotRegistry != null)
        {
            CliRunner = (cli, model, prompt, timeout, ct) =>
                RunViaOneShotAsync(cli, model, prompt, timeout, ct);
        }
    }

    private async Task<string> RunViaOneShotAsync(string cli, string model, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var oneShot = _oneShotRegistry?.Get("claude");
        if (oneShot == null) return await DefaultRunCliAsync(cli, model, prompt, timeout, ct);

        var result = await oneShot.RunAsync(new OrchestratorApi.Services.Cli.OneShot.CliOneShotRequest(
            CliType: "claude",
            Model: model,
            Prompt: prompt)
        {
            Timeout = timeout,
            Source = OrchestratorApi.Models.AdHocUsageSources.ReviewDecision,
            RecordUsage = false,
        }, ct);

        if (!result.Ok)
        {
            _logger.LogWarning(
                "Review-decision CLI call failed: exit={ExitCode} duration={Duration}ms error={Error}",
                result.ExitCode, result.Duration.TotalMilliseconds, result.Error);
        }
        return result.Stdout;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

        if (_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
        {
            try
            {
                _logger.LogInformation("ReviewDecisionOrchestrator boot sweep starting (one-shot full backfill).");
                await TickOnceAsync(workspace!, stoppingToken);
                _logger.LogInformation("ReviewDecisionOrchestrator boot sweep complete; entering recurring tick loop.");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator boot sweep failed"); }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            intervalSeconds = _configuration.GetValue("ReviewDecisionOrchestrator:IntervalSeconds", 30);
            try { await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                if (_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
                    await TickOnceAsync(workspace!, stoppingToken);
            }
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
        var aspectModel = _configuration.GetValue("ReviewDecisionOrchestrator:AspectModel", model);
        var aspectTimeoutSeconds = _configuration.GetValue("ReviewDecisionOrchestrator:AspectTimeoutSeconds", 60);
        var maxReissues = _configuration.GetValue("ReviewDecisionOrchestrator:MaxAutoReissueAttempts", MaxAutoReissueAttempts);
        var aspects = ResolveAspectRunners();

        _statusSnapshot.BeginTick();
        try
        {
            foreach (var entry in _scanner.GetWatchPaths())
            {
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (!Directory.Exists(entry.Path)) continue;

                foreach (var pending in EnumeratePending(entry))
                {
                    if (ct.IsCancellationRequested) return;
                    _statusSnapshot.RecordPending();
                    _statusSnapshot.SetCurrent(entry.Name, pending.Job.Id);

                    try
                    {
                        if (pending.Kind == ReviewSignalKind.NoOp)
                        {
                            // NOOP is fully deterministic: no fast-model call,
                            // no per-hour rate consumption.
                            await ProcessNoOpAsync(workspace, entry, pending, maxReissues, ct);
                            _statusSnapshot.RecordReissue();
                            continue;
                        }

                        if (pending.Kind == ReviewSignalKind.Blocked)
                        {
                            // BLOCKED is also deterministic: the agent has
                            // declared it cannot proceed, so we always
                            // escalate to a human-decision intake rather
                            // than spending a fast-model call to re-decide.
                            await ProcessBlockedAsync(workspace, entry, pending, ct);
                            _statusSnapshot.RecordEscalate();
                            continue;
                        }

                        if (pending.Kind == ReviewSignalKind.Done)
                        {
                            // DONE: run the multi-aspect pass. ADR-0025
                            // slice 1: form an opinion across multiple
                            // quality aspects rather than waving the job
                            // through silently. Rate-limited only on the
                            // sum of aspect calls, since the per-aspect
                            // cost is small and tests substitute a stub.
                            if (aspects.Count == 0 ||
                                !_configuration.GetValue("ReviewDecisionOrchestrator:AspectsEnabled", true))
                            {
                                continue;
                            }
                            if (!RateLimitOk(maxPerHour))
                            {
                                _logger.LogInformation(
                                    "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); deferring DONE aspects for {JobId}",
                                    maxPerHour, pending.Job.Id);
                                return;
                            }
                            await ProcessDoneAsync(workspace, entry, pending, aspects, cliBinary, aspectModel,
                                TimeSpan.FromSeconds(aspectTimeoutSeconds), ct);
                            _statusSnapshot.RecordAspectsRun(aspects.Count);
                            _callTimestamps.Enqueue(DateTime.UtcNow);
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
        finally
        {
            _statusSnapshot.EndTick();
        }
    }

    private IReadOnlyList<string> ResolveAspectRunners()
    {
        var section = _configuration.GetSection("ReviewDecisionOrchestrator:AspectRunners");
        if (!section.Exists()) return DefaultAspectRunners;
        var configured = section.GetChildren()
            .Select(c => c.Value)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .ToList();
        // Empty configured array is the explicit kill switch
        // (`AspectRunners: []`) - return empty rather than defaulting.
        return configured;
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
            var sw = OrchestratorApi.Services.AdHoc.AdHocClaudeInvoker.StartTiming();
            var rawResponse = await CliRunner(cliBinary, model, prompt, TimeSpan.FromSeconds(120), ct);
            sw.Stop();
            var (parsedText, callUsage) = OrchestratorApi.Services.AdHoc.AdHocClaudeInvoker.ParseOrFallback(rawResponse, model);
            OrchestratorApi.Services.AdHoc.AdHocClaudeInvoker.Record(
                _usage,
                OrchestratorApi.Models.AdHocUsageSources.ReviewDecision,
                model,
                callUsage,
                sw.ElapsedMilliseconds,
                ok: true,
                project: entry.Name,
                jobId: pending.Job.Id);
            response = parsedText;
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

        var prior = CountPriorReissues(workspace, entry.Name, pending.Job.Id);

        // Branch 2: a reissued Codex NOOP that came back as another
        // sentinel-only NOOP without any tool/file activity is not
        // recoverable by another sharpened prompt. Route it back to the
        // early human-review lane so it is not picked again automatically.
        var noProgressEvidence = InspectNoOpProgressSinceLastRecovery(pending);
        if (prior > 0
            && noProgressEvidence.SawNoOpRecoveryReissue
            && noProgressEvidence.ToolCalls == 0
            && noProgressEvidence.FileChanges == 0
            && noProgressEvidence.AgentSubstanceChars == 0)
        {
            var count = prior + 1;
            var reason =
                $"Escalated: {count} consecutive NOOPs without progress. " +
                "The latest retry emitted only [[TASK_NOOP]] after NOOP recovery, with 0 tool calls and 0 file changes.";
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct,
                targetState: TaskStates.NeedsHumanReview,
                createHumanDecisionIntake: false);
            _logger.LogWarning(
                "ReviewDecisionOrchestrator escalated {Project}/{JobId} after {NoOpCount} consecutive NOOPs without progress: toolCalls={ToolCalls} fileChanges={FileChanges} agentSubstanceChars={AgentSubstanceChars}",
                entry.Name, pending.Job.Id, count, noProgressEvidence.ToolCalls,
                noProgressEvidence.FileChanges, noProgressEvidence.AgentSubstanceChars);
            return;
        }

        // Branch 3: budget exhausted -> escalate. We share the counter
        // with the existing NEEDS_INPUT-driven reissues so a job that has
        // already been retried multiple times by either path doesn't get
        // double-spent here.
        if (prior >= maxReissues)
        {
            var reason = $"NOOP after {prior} prior orchestrator reissue(s); user attention required.";
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct);
            return;
        }

        // Branch 4: usable prompt + budget left -> reissue with the
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

        var moved = MoveReissueToReadyTop(current, entry, "NOOP");
        if (moved != null)
        {
            await WriteFollowUpFileAsync(moved, followUp, ct);
            EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
                TimelineActors.QualityLoop,
                "Reopened: NOOP recovery, reissued with sharpened framing.",
                BuildReopenDetails("noop-recovery",
                    CountPriorReissues(workspace, entry.Name, current.Id),
                    reason));
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
        CancellationToken ct,
        string targetState = TaskStates.HumanReview,
        bool createHumanDecisionIntake = true)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator could not auto-recover NOOP. Reason: {reason}. Promoted to {targetState}.");

        var verdict = new OrchestratorDecisionVerdict(OrchestratorDecisionAction.Escalate, reason);

        var move = _stateMachine.MoveJob(current.Id, targetState, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to {TargetState} after NOOP escalate: {Status} {Message}",
                current.Id, targetState, move.Status, move.Message);
        }

        EmitVerdictTimeline(move.NewFolderPath ?? current.FolderPath,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("noop-escalate", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        if (createHumanDecisionIntake)
        {
            await CreateHumanDecisionIntakeAsync(entry, pending, verdict, ct);
        }

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
            $"Orchestrator escalated BLOCKED to human review. Reason: {reason}. Promoted to 5-human-review.");

        var verdict = new OrchestratorDecisionVerdict(OrchestratorDecisionAction.Escalate, reason);

        // ADR-0025: BLOCKED escalations move the task to 5-human-review.
        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after BLOCKED: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        EmitVerdictTimeline(move.NewFolderPath ?? current.FolderPath,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("agent-blocked", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

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

    private async Task ProcessDoneAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        IReadOnlyList<string> aspects,
        string cliBinary,
        string aspectModel,
        TimeSpan perAspectTimeout,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var (taskBody, recentLog) = LoadTaskContext(pending);
        var statusSummary = LoadStatusSummary(current.FolderPath);
        var diffSummary = LoadDiffSummary(entry.Name, entry.Path, current);

        var inputs = new AspectRunInputs(
            Project: entry.Name,
            JobId: current.Id,
            JobTitle: current.Title ?? current.Id,
            JobFolderPath: current.FolderPath,
            TaskBody: taskBody,
            RecentLog: recentLog,
            DiffSummary: diffSummary,
            StatusSummary: statusSummary);

        // Bracket the aspect run with a pipeline-execution record so the
        // Overview pipeline view can show "ran 4 aspects in N ms, used X
        // tokens" without having to reconstruct it from cli-output.log.
        // The aspect runner records each step's outcome inside RunAsync;
        // we own the start / complete marks. Stand-alone tests that wire
        // the orchestrator without a PipelineExecutionLog skip this
        // entirely (the recorder is fully optional).
        _pipelineLog?.Begin(current.FolderPath, PipelineCatalogue.Standard, entry.Name, current.Id);

        // Per-project pipeline config: drop aspects the project disabled and
        // route each remaining aspect's CLI call to its configured model
        // (falling back to the run-wide aspectModel). The resolver keys on
        // the catalogue step id (aspect-{id}); see PipelineStepConfigResolver.
        var settings = _projectSettings?.Get(entry.Name);
        var enabledAspects = aspects
            .Where(id => PipelineStepConfigResolver.IsEnabled(settings, $"aspect-{id}"))
            .ToList();
        Func<string, string>? modelForAspect = settings is null
            ? null
            : aspectId => PipelineStepConfigResolver.ResolveModel(settings, $"aspect-{aspectId}", aspectModel);

        var report = await _aspectRunner.RunAsync(inputs, enabledAspects, cliBinary, aspectModel, perAspectTimeout, ct, modelForAspect);

        // ASS-563: run the lint-scss post-step BEFORE the pipeline Complete
        // mark so its step record lands in pipeline-execution.json while
        // the file is still in its in-flight state. Skipped/Ok/Warn just
        // record; Fail short-circuits the move-to-review path with a
        // reissue (or, if we've already reissued once, an escalation).
        var lintResult = await RunLintScssPostStepAsync(workspace, entry, current, ct);

        _pipelineLog?.Complete(current.FolderPath);

        if (report.Overall == AspectStatus.Block)
        {
            await ReissueOnBlockAsync(workspace, entry, pending, current, report, ct);
            return;
        }

        if (lintResult?.Verdict == LintScssVerdict.Fail)
        {
            // The aspect verdict was pass/concerns but lint-scss broke; in
            // fail mode the post-step routes to its own reissue / escalate
            // path and we stop here. In warn mode the verdict was Warn,
            // not Fail, and we fall through to the normal move-to-review.
            await HandleLintScssFailureAsync(workspace, entry, pending, current, lintResult, ct);
            return;
        }

        if (report.ConcernTagIds.Count > 0)
        {
            ConcernTagWriter.MergeConcernTags(current.FolderPath, report.ConcernTagIds, _logger);
        }

        // Promote to 5-human-review with or without concern tags. ADR-0025:
        // accept-as-done routes to human-review, never directly to completed.
        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            // Move failed -> do NOT fire the operator-facing "accepted as
            // done" banner: it would claim the task moved while the folder
            // is still in 4-auto-review. The aspect work is sunk; the next
            // tick will re-attempt the move.
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after multi-aspect accept: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return;
        }

        // Re-merge tags after the lane move so the tags array on disk
        // reflects the post-move folder. The state machine moves the
        // folder atomically, so the tags written before the move travel
        // with it; this re-merge is a defensive idempotent reapplication
        // on the authoritative post-move path returned by MoveJob.
        // Using move.NewFolderPath rather than re-scanning avoids the
        // stale-cache race that previously fell back to the source folder
        // and left orphan logs/cli-output.log skeletons in 4-auto-review.
        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var movedInfo = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
        if (report.ConcernTagIds.Count > 0 &&
            !string.Equals(movedFolderPath, current.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            ConcernTagWriter.MergeConcernTags(movedFolderPath, report.ConcernTagIds, _logger);
        }

        // Append the operator-facing chat-log line ONLY after the lane move
        // has succeeded, so the banner cannot fire while the task is still
        // in 4-auto-review. F29: keep the headline short (one sentence +
        // count) so the workspace banner / notification toast stays
        // readable. The full per-aspect verdict list lives in the
        // aspect-*.md files inside the job folder and on the decision
        // record below.
        var titleAccept = string.IsNullOrWhiteSpace(movedInfo.Title) ? movedInfo.Id : movedInfo.Title;
        var verdictNote = report.Overall == AspectStatus.Concerns
            ? $"Auto-review accepted \"{titleAccept}\" with concerns ({FormatConcernCount(report)}). Moved to 5-human-review for your approval."
            : $"Auto-review accepted \"{titleAccept}\" as done. Moved to 5-human-review for your approval.";
        _chatLog.Append(movedInfo, OrchestratorMessageKind.Decision, verdictNote);

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorVerdictAccepted,
            TimelineActors.Orchestrator, verdictNote, new Dictionary<string, string>
            {
                ["verdict"] = "accept",
                ["aspectOutcome"] = report.Overall == AspectStatus.Concerns ? "concerns" : "pass",
                ["aspects"] = AspectSummaryLine(report),
            });

        if (report.Overall == AspectStatus.Concerns)
        {
            _statusSnapshot.RecordAccept();
        }
        else
        {
            _statusSnapshot.RecordAccept();
        }

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.AcceptAsDone,
            Reason: report.Overall == AspectStatus.Concerns
                ? $"Multi-aspect: accept with concerns ({string.Join(", ", report.ConcernTagIds)})"
                : "Multi-aspect: all aspects pass",
            Prompt: "(multi-aspect run; per-aspect prompts written to aspect-*.md)",
            Response: AspectSummaryLine(report),
            FollowUp: string.Empty));
    }

    private async Task ReissueOnBlockAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        AspectRunReport report,
        CancellationToken ct)
    {
        var followUp =
            "Auto-review found one or more blocking aspect verdicts. Address each item below, " +
            "then re-run the task and end with [[TASK_DONE]]:\n\n" +
            report.FollowUpSummary;

        var moved = MoveReissueToReadyTop(current, entry, "multi-aspect block");
        if (moved == null)
        {
            // Move failed -> no operator-facing "sent back to ready" banner.
            // The aspect work is sunk; the next tick will retry.
            return;
        }
        await WriteFollowUpFileAsync(moved, followUp, ct);

        // F29: keep the operator-facing reissue note short. The full
        // per-aspect verdict list is in the decision journal record below
        // and in the aspect-*.md files written by the runner.
        var titleReissue = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        var blockCount = report.Verdicts.Count(v => v.Status == AspectStatus.Block);
        var blockNoun = blockCount == 1 ? "aspect" : "aspects";
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{titleReissue}\" back to 2-ready ({blockCount} blocking {blockNoun}).");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: {blockCount} blocking {blockNoun} from auto-review.",
            BuildReopenDetails("multi-aspect-block",
                CountPriorReissues(workspace, entry.Name, current.Id),
                report.FollowUpSummary));

        _statusSnapshot.RecordReissue();

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: "Multi-aspect block: " + AspectSummaryLine(report),
            Prompt: "(multi-aspect run; per-aspect prompts written to aspect-*.md)",
            Response: AspectSummaryLine(report),
            FollowUp: followUp));
    }

    /// <summary>
    /// Drive the lint-scss post-step: resolve mode through
    /// <see cref="PostStepConfigResolver"/>, invoke the runner, record
    /// the verdict on <c>pipeline-execution.json</c>, and write the
    /// per-run log file under <c>post-steps/</c>. Returns null when the
    /// runner is not wired (test path) or when mode = off so the caller
    /// can short-circuit. Never throws: a broken local stylelint
    /// toolchain falls through to a <see cref="LintScssVerdict.Skipped"/>
    /// verdict rather than crash the orchestrator tick.
    /// </summary>
    private async Task<LintScssResult?> RunLintScssPostStepAsync(
        string workspace,
        WatchPathEntry entry,
        TaskInfo current,
        CancellationToken ct)
    {
        if (_lintScssRunner == null) return null;

        var mode = PostStepConfigResolver.Resolve(
            _configuration, current.FolderPath, PipelineCatalogue.LintScssStepId);

        if (mode == PostStepMode.Off)
        {
            RecordLintScssStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off",
                reason: "post-step disabled by config");
            return new LintScssResult(LintScssVerdict.Skipped, null, 0, "", "mode=off");
        }

        var repoPath = string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RootPath : entry.RepositoryPath;
        var timeoutSeconds = _configuration.GetValue($"PostSteps:{PipelineCatalogue.LintScssStepId}:TimeoutSeconds", 120);

        LintScssResult result;
        try
        {
            result = await _lintScssRunner.RunAsync(repoPath, mode, TimeSpan.FromSeconds(timeoutSeconds), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: lint-scss post-step threw for {Project}/{JobId}; treating as skipped",
                entry.Name, current.Id);
            RecordLintScssStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "error", reason: ex.Message);
            return null;
        }

        var status = result.Verdict switch
        {
            LintScssVerdict.Ok      => PipelineStepStatus.Passed,
            LintScssVerdict.Warn    => PipelineStepStatus.Passed, // ran, no reissue
            LintScssVerdict.Fail    => PipelineStepStatus.Failed,
            LintScssVerdict.Skipped => PipelineStepStatus.Skipped,
            _ => PipelineStepStatus.Skipped,
        };
        var verdictToken = result.Verdict switch
        {
            LintScssVerdict.Ok      => "ok",
            LintScssVerdict.Warn    => "warn",
            LintScssVerdict.Fail    => "fail",
            LintScssVerdict.Skipped => "skipped",
            _ => "skipped",
        };
        RecordLintScssStep(current.FolderPath, status, result.DurationMs, verdictToken, result.Reason);
        WriteLintScssLog(current.FolderPath, result);
        return result;
    }

    private void RecordLintScssStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.LintScssStepId,
            Kind = StepKind.Tool,
            Status = status,
            StartedAt = now - TimeSpan.FromMilliseconds(durationMs),
            CompletedAt = now,
            DurationMs = durationMs,
            Verdict = verdictToken,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Persist the truncated stylelint output for the FE timeline to
    /// render on click-to-expand. One file per run-index so the user can
    /// see previous-run output side-by-side with the current attempt;
    /// the index is derived from how many <c>lint-scss-*.log</c> files
    /// already exist in <c>post-steps/</c>.
    /// </summary>
    private void WriteLintScssLog(string jobFolderPath, LintScssResult result)
    {
        try
        {
            var dir = Path.Combine(jobFolderPath, "post-steps");
            Directory.CreateDirectory(dir);
            var index = Directory.EnumerateFiles(dir, "lint-scss-*.log").Count() + 1;
            var path = Path.Combine(dir, $"lint-scss-{index}.log");
            var body = $"verdict={result.Verdict} exit={result.ExitCode?.ToString() ?? "n/a"} durationMs={result.DurationMs}\n" +
                       $"reason={result.Reason}\n" +
                       "---\n" +
                       result.Output;
            File.WriteAllText(path, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: failed to persist lint-scss log under {TaskFolder}",
                jobFolderPath);
        }
    }

    /// <summary>
    /// Resolve a Fail verdict from the lint-scss post-step. If the job has
    /// no prior lint-scss reissue, send it back to <c>2-ready</c> with a
    /// follow-up that includes the truncated stylelint output. If a prior
    /// reissue already exists in the decision journal, the budget is
    /// spent and the job escalates to <c>5-human-review</c> instead — the
    /// spec's infinite-spin guard.
    /// </summary>
    private async Task HandleLintScssFailureAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        LintScssResult result,
        CancellationToken ct)
    {
        var priorLintReissues = ReviewDecisionLog.ReadAll(workspace, entry.Name)
            .Count(r => r.JobId == current.Id
                        && r.Kind == ReviewDecisionKind.Reissue
                        && r.Reason != null
                        && r.Reason.StartsWith(LintScssReissueReasonPrefix, StringComparison.Ordinal));

        if (priorLintReissues >= 1)
        {
            var reason = $"lint-scss failed twice in a row (exit {result.ExitCode}); escalating per ASS-46 infinite-spin guard.";
            var verdict = new OrchestratorDecisionVerdict(OrchestratorDecisionAction.Escalate, reason);
            var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
            if (move.Status == MoveJobStatus.Success)
            {
                var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
                var moved = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
                _chatLog.AppendSupervisor(moved, "escalate",
                    $"Lint-scss post-step failed twice in a row. Promoted to 5-human-review. Output:\n{result.Output}");
                EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.Orchestrator, reason,
                    BuildEscalateDetails("lint-scss-double-fail", reason,
                        CountPriorReissues(workspace, entry.Name, current.Id)));
                await CreateHumanDecisionIntakeAsync(entry, pending, verdict, ct);
            }
            else
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to escalate {JobId} after lint-scss double-fail: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }
            _statusSnapshot.RecordEscalate();
            ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: reason,
                Prompt: "(deterministic lint-scss post-step)",
                Response: result.Output,
                FollowUp: string.Empty));
            return;
        }

        var moved2 = MoveReissueToReadyTop(current, entry, "lint-scss fail");
        if (moved2 == null) return;

        var followUp = BuildLintScssFollowUp(result);
        await WriteFollowUpFileAsync(moved2, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved2.Title) ? moved2.Id : moved2.Title;
        _chatLog.Append(moved2, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready: lint-scss failed (exit {result.ExitCode}).");

        EmitVerdictTimeline(moved2.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: lint-scss post-step failed (exit {result.ExitCode}).",
            BuildReopenDetails("lint-scss-fail",
                CountPriorReissues(workspace, entry.Name, current.Id),
                result.Output));

        _statusSnapshot.RecordReissue();
        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: LintScssReissueReasonPrefix + $"stylelint exit {result.ExitCode}",
            Prompt: "(deterministic lint-scss post-step)",
            Response: result.Output,
            FollowUp: followUp));
    }

    private static string BuildLintScssFollowUp(LintScssResult result) =>
        "Auto-review re-opened this task because the lint-scss post-step found stylelint errors. " +
        "Run `npm run lint:scss` from the frontend tree, address every error (warnings can stay), " +
        "and end with [[TASK_DONE]] once the gate is green again.\n\n" +
        "Truncated stylelint output (first 50 lines):\n" +
        "```\n" +
        result.Output + "\n" +
        "```";

    private static string AspectSummaryLine(AspectRunReport report)
    {
        if (report.Verdicts.Count == 0) return "(no aspects ran)";
        return string.Join(", ", report.Verdicts.Select(v =>
            $"{v.Aspect}={AspectVerdictParsing.StatusToken(v.Status)}"));
    }

    /// <summary>
    /// F29: produce a compact aspect-count phrase for the operator-facing
    /// verdict toast ("2 of 4 aspects flagged"). Keeps the headline a
    /// single sentence; the full per-aspect list stays on
    /// <see cref="AspectSummaryLine"/> for the decision journal.
    /// </summary>
    private static string FormatConcernCount(AspectRunReport report)
    {
        if (report.Verdicts.Count == 0) return "no aspects ran";
        var concerns = report.Verdicts.Count(v => v.Status == AspectStatus.Concerns);
        if (concerns == 0) return $"0 of {report.Verdicts.Count} aspects flagged";
        var noun = concerns == 1 ? "aspect" : "aspects";
        return $"{concerns} of {report.Verdicts.Count} {noun} flagged";
    }

    private static string LoadStatusSummary(string folderPath)
    {
        var statusPath = Path.Combine(folderPath, "status.md");
        if (!File.Exists(statusPath)) return string.Empty;
        try { return Truncate(File.ReadAllText(statusPath), 4_000); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Build the diff summary fed to the per-aspect prompts. Aggregates
    /// every commit attributed to the job across all of its runs (via
    /// session-events SHA ranges) plus the auto-commit on
    /// <see cref="TaskInfo.Commit"/>. Same aggregation pipeline that
    /// powers <c>/api/tasks/{id}/commits</c>, so the aspect reviewer sees
    /// the same picture the human reviewer sees.
    ///
    /// <para>
    /// Why not just <c>job.Commit</c>: a crash-recovery commit lands as a
    /// near-empty fixup on top of the real work, then becomes
    /// <see cref="TaskInfo.Commit"/>. Reading that alone gives "0 files
    /// changed" and false-positive blocks the verdict (2026-05-11
    /// incident). Walking the full run range is the only source of truth
    /// for the actual change set.
    /// </para>
    ///
    /// <para>
    /// Falls back to the legacy single-commit summary only when neither
    /// dependency is wired - that path exists for tests that construct
    /// the orchestrator without the full graph; production always has
    /// both services from DI.
    /// </para>
    /// </summary>
    private string LoadDiffSummary(string project, string? watchPath, TaskInfo job)
    {
        if (_sessions == null || _git == null)
        {
            return BuildDiffSummary(EmptyAggregate, job.Commit);
        }
        try
        {
            var events = _sessions.ReadSessionEvents(job.Id, watchPath);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(job.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
            var aggregate = TaskCommitsAggregator.Aggregate(job, timeline.Runs,
                (before, after) => _git!.GetCommitsInShaRange(job.Id, watchPath, before, after));
            return BuildDiffSummary(aggregate, job.Commit);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: full-range diff summary failed for {Project}/{JobId}; falling back to single-commit view",
                project, job.Id);
            return BuildDiffSummary(EmptyAggregate, job.Commit);
        }
    }

    private static readonly TaskCommitsAggregate EmptyAggregate = new() { Count = 0, Commits = [] };

    /// <summary>
    /// Pure renderer: turn an aggregate plus the legacy auto-commit into the
    /// diff-summary string handed to the aspect prompts. Pure so it can
    /// be pinned by unit tests against a synthetic commit stack
    /// (including the empty-HEAD-recovery-commit + non-empty prior
    /// commits scenario that triggered the 2026-05-11 false positive).
    /// </summary>
    internal static string BuildDiffSummary(TaskCommitsAggregate aggregate, TaskCommitInfo? legacyAutoCommit)
    {
        if (aggregate.Count == 0)
        {
            if (legacyAutoCommit == null)
            {
                return "No commits attributed to this task (no run-window SHA range produced commits and no auto-commit was recorded).";
            }
            // Aggregator could not be run (test path / missing deps). Fall
            // back to the legacy single-commit view so the prompt still has
            // something to chew on.
            var c = legacyAutoCommit;
            var sb0 = new StringBuilder();
            sb0.AppendLine($"Commit: {c.ShortSha}");
            if (!string.IsNullOrWhiteSpace(c.Message))
            {
                var firstLine = c.Message.Split('\n', 2)[0].Trim();
                if (firstLine.Length > 0) sb0.AppendLine($"Subject: {firstLine}");
            }
            sb0.AppendLine($"Files changed: {c.FilesChanged}");
            return sb0.ToString();
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Commits attributed to this task: {aggregate.Count}");
        sb.AppendLine($"Total files changed (sum across commits; a file touched twice counts twice): {aggregate.TotalFilesChanged}");
        sb.AppendLine($"Total lines added: {aggregate.TotalAdded}");
        sb.AppendLine($"Total lines removed: {aggregate.TotalRemoved}");
        sb.AppendLine();
        sb.AppendLine("Per commit (newest first):");
        foreach (var c in aggregate.Commits)
        {
            var subject = string.IsNullOrWhiteSpace(c.Subject) ? "(no subject)" : c.Subject;
            sb.AppendLine($"- {c.ShortSha} {subject} ({c.FilesChanged} files, +{c.Added}, -{c.Removed})");
        }
        return sb.ToString();
    }

    private static int CountPriorReissues(string workspace, string project, string jobId)
    {
        return ReviewDecisionLog.ReadAll(workspace, project)
            .Count(r => r.JobId == jobId && r.Kind == ReviewDecisionKind.Reissue);
    }

    /// <summary>
    /// Configured reissue budget (shared by NEEDS_INPUT / NOOP / aspect /
    /// lint-scss reissues). Defaults to <see cref="MaxAutoReissueAttempts"/>.
    /// Drives the <c>maxAttempts</c> detail on the completion-loop timeline
    /// events so the FE can render "Attempt N of M" without re-reading
    /// config itself.
    /// </summary>
    private int ConfiguredMaxReissues() =>
        _configuration.GetValue("ReviewDecisionOrchestrator:MaxAutoReissueAttempts", MaxAutoReissueAttempts);

    /// <summary>
    /// Tee one orchestrator verdict (accept / reopen / escalate) into the
    /// unified per-task timeline ledger (ADR-0049) so the Overview/Timeline
    /// surfaces can show the completion-loop cycle (ASS-566) without
    /// re-deriving it from the decision journal and chat log. Best-effort:
    /// the timeline is observability, never a state-machine input, so a
    /// missing writer (test path) or a write failure cannot affect the
    /// verdict. Always pass the POST-MOVE folder so the event lands in the
    /// lane the card actually moved to, mirroring the chat-log firing-order
    /// rule.
    /// </summary>
    private void EmitVerdictTimeline(
        string? folderPath,
        string kind,
        string actor,
        string summary,
        Dictionary<string, string>? details = null)
    {
        if (_timeline == null || string.IsNullOrWhiteSpace(folderPath)) return;
        _timeline.Append(folderPath!, kind, actor, summary, details: details);
    }

    /// <summary>
    /// Build the <c>Details</c> bag shared by every "go again" timeline
    /// event. <paramref name="priorReissues"/> is the count BEFORE this
    /// reopen (the decision-journal record is appended after the emit), so
    /// the upcoming attempt is <c>priorReissues + 2</c> (initial run = 1).
    /// </summary>
    private Dictionary<string, string> BuildReopenDetails(string cause, int priorReissues, string? gap = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var details = new Dictionary<string, string>
        {
            ["cause"] = cause,
            ["attempt"] = (priorReissues + 2).ToString(inv),
            ["maxAttempts"] = (ConfiguredMaxReissues() + 1).ToString(inv),
        };
        if (!string.IsNullOrWhiteSpace(gap))
        {
            details["gap"] = Truncate(gap!.Trim(), 600);
        }
        return details;
    }

    /// <summary>
    /// Build the <c>Details</c> bag shared by every "hand to a human"
    /// timeline event. The run that just finished is attempt
    /// <c>priorReissues + 1</c> (initial run = 1); recording it next to the
    /// budget makes a budget-exhaustion escalation legible on the timeline.
    /// </summary>
    private Dictionary<string, string> BuildEscalateDetails(string cause, string reason, int priorReissues)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new Dictionary<string, string>
        {
            ["cause"] = cause,
            ["reason"] = Truncate(reason ?? string.Empty, 600),
            ["attempt"] = (priorReissues + 1).ToString(inv),
            ["maxAttempts"] = (ConfiguredMaxReissues() + 1).ToString(inv),
        };
    }

    private static NoOpProgressEvidence InspectNoOpProgressSinceLastRecovery(PendingDecision pending)
    {
        var logPath = TaskPaths.CliOutputLog(pending.Job.FolderPath);
        if (!File.Exists(logPath)) return NoOpProgressEvidence.None;

        string log;
        try { log = File.ReadAllText(logPath); }
        catch { return NoOpProgressEvidence.None; }

        if (string.IsNullOrWhiteSpace(log)) return NoOpProgressEvidence.None;
        var lines = log.Split('\n');
        var noopIndex = Math.Clamp(pending.LineNumber - 1, 0, Math.Max(lines.Length - 1, 0));

        var reissueIndex = -1;
        for (var i = noopIndex - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("[orchestrator]", StringComparison.Ordinal)
                && line.Contains("[reissue]", StringComparison.Ordinal)
                && line.Contains("NOOP recovery", StringComparison.OrdinalIgnoreCase))
            {
                reissueIndex = i;
                break;
            }
        }

        if (reissueIndex < 0) return NoOpProgressEvidence.None;

        var toolCalls = 0;
        var fileChanges = 0;
        var agentSubstanceChars = 0;

        for (var i = reissueIndex + 1; i <= noopIndex && i < lines.Length; i++)
        {
            InspectProgressLine(lines[i], ref toolCalls, ref fileChanges, ref agentSubstanceChars);
        }

        return new NoOpProgressEvidence(
            SawNoOpRecoveryReissue: true,
            ToolCalls: toolCalls,
            FileChanges: fileChanges,
            AgentSubstanceChars: agentSubstanceChars);
    }

    private static void InspectProgressLine(
        string line,
        ref int toolCalls,
        ref int fileChanges,
        ref int agentSubstanceChars)
    {
        var jsonStart = line.IndexOf('{');
        if (jsonStart >= 0)
        {
            var json = line[jsonStart..].Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();
                if (!string.Equals(type, "item.started", StringComparison.Ordinal)
                    && !string.Equals(type, "item.completed", StringComparison.Ordinal))
                {
                    return;
                }

                if (!root.TryGetProperty("item", out var item)
                    || item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var itemTypeProp))
                {
                    return;
                }

                var itemType = itemTypeProp.GetString();
                if (string.Equals(itemType, "agent_message", StringComparison.Ordinal))
                {
                    var text = item.TryGetProperty("text", out var textProp)
                        ? textProp.GetString()
                        : null;
                    agentSubstanceChars += CountAgentSubstanceChars(text);
                    return;
                }

                if (string.Equals(itemType, "file_change", StringComparison.Ordinal))
                {
                    fileChanges++;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(itemType))
                {
                    toolCalls++;
                }
                return;
            }
            catch (JsonException)
            {
                // Fall through and treat it as ordinary output text.
            }
        }

        var payloadText = ExtractStreamPayload(line);
        agentSubstanceChars += CountAgentSubstanceChars(payloadText);
    }

    private static string ExtractStreamPayload(string line)
    {
        var stdoutMarker = "] [stdout] ";
        var stderrMarker = "] [stderr] ";
        var stdoutAt = line.IndexOf(stdoutMarker, StringComparison.Ordinal);
        if (stdoutAt >= 0) return line[(stdoutAt + stdoutMarker.Length)..];
        var stderrAt = line.IndexOf(stderrMarker, StringComparison.Ordinal);
        if (stderrAt >= 0) return line[(stderrAt + stderrMarker.Length)..];
        return line;
    }

    private static int CountAgentSubstanceChars(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("[[TASK_NOOP", StringComparison.OrdinalIgnoreCase)
            && trimmed.EndsWith("]]", StringComparison.Ordinal))
        {
            return 0;
        }

        var withoutNoOp = text
            .Replace("[[TASK_NOOP]]", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return withoutNoOp.Length;
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

        // Move first so the operator-visible "sent back to ready" notification
        // only fires once the folder has actually left 4-auto-review. A failed
        // move must not produce a banner that claims the task moved.
        var moved = MoveReissueToReadyTop(current, entry, "NEEDS_INPUT");
        if (moved == null)
        {
            return;
        }
        await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready for another attempt. Reason: {verdict.Reason}. Follow-up: {followUp}");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: orchestrator answered NEEDS_INPUT. {verdict.Reason}",
            BuildReopenDetails("needs-input",
                CountPriorReissues(workspace, entry.Name, current.Id),
                verdict.Reason));

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

        // ADR-0025: escalate moves the job from 4-auto-review to
        // 5-human-review and writes a [supervisor] chat-note so the user
        // sees one concise reason for the handover. The intake under
        // 1-preparation is kept for now as an extra surface, but the
        // primary signal is the lane move itself.
        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after escalate: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return;
        }

        // Pin the chat-log line to the post-move folder via MoveJob's
        // authoritative path. FindJob can briefly return null or the
        // pre-move snapshot when the cache has not refreshed yet, and
        // the chat-log auto-creates its parent folder on write — so a
        // stale path resurrects the source lane as a one-line skeleton.
        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var moved = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.AppendSupervisor(moved, "escalate",
            $"Auto-review escalated \"{title}\" to 5-human-review for human attention. Reason: {verdict.Reason}.");

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
            TimelineActors.Orchestrator, verdict.Reason,
            BuildEscalateDetails("needs-input-escalate", verdict.Reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

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

        // ADR-0025: accept-as-done routes to 5-human-review, NOT directly
        // to 6-completed. The user always gets the final say on whether a
        // task is done; the orchestrator's accept signal is "the agent's
        // answer looks complete to me, please confirm."
        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            // Move failed -> do NOT write the operator-facing "accepted as
            // done" line: the banner would then claim the task moved while
            // it is still sitting in 4-auto-review.
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after accept: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return;
        }

        // Pin the chat-log line to the post-move folder via MoveJob's
        // authoritative path; see HandleEscalate for the rationale. The
        // chat-log auto-creates its parent folder, so a stale path would
        // resurrect the source lane as a one-line skeleton.
        var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
        var moved = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        _chatLog.Append(moved, OrchestratorMessageKind.Decision,
            $"Auto-review accepted \"{title}\" as done. Moved to 5-human-review for your approval. Reason: {verdict.Reason}");

        EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorVerdictAccepted,
            TimelineActors.Orchestrator,
            $"Accepted as done. {verdict.Reason}", new Dictionary<string, string>
            {
                ["verdict"] = "accept",
                ["reason"] = Truncate(verdict.Reason ?? string.Empty, 600),
            });

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

        // ADR-0024: idempotency check + create both go through the
        // typed TaskAccess layer instead of building the lane path
        // locally. Same job already has an open intake -> don't
        // multiply.
        if (_taskAccess.SlugExistsInLane(entry.Path, TaskStates.Preparation, folderName))
        {
            return;
        }

        var promptBody = $"# Human decision needed for `{pending.Job.Id}`\n\n" +
                         $"The orchestrator could not decide on this 4-review task unattended.\n\n" +
                         $"**Reason from orchestrator:** {verdict.Reason}\n\n" +
                         $"**Original signal:** {OriginalSignalLabel(pending.Kind)}\n\n" +
                         $"**Original reason:** {pending.Reason ?? "(none provided)"}\n\n" +
                         $"Please review the task in 4-review (`{pending.Job.FolderPath}`) and either answer the agent or change scope.\n";

        try
        {
            var result = await _taskAccess.MutateAsync(new TaskMutationRequest
            {
                Kind = TaskMutationKind.Create,
                CreateRequest = new CreateJobRequest
                {
                    Id = folderName,
                    Title = $"Human decision needed: {pending.Job.Title}",
                    Agent = "human",
                    Order = 1,
                    TargetState = TaskStates.Preparation,
                    WatchPath = entry.Path,
                    PromptMarkdown = promptBody,
                },
            }, ct);

            if (result.Status != TaskMutationStatus.Applied)
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: human-decision intake refused for {JobId}: {Status} {Message}",
                    pending.Job.Id, result.Status, result.Message);
            }
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
        var logPath = TaskPaths.CliOutputLog(folder);
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
        // ADR-0024: list 4-auto-review through the typed layer
        // instead of walking the lane directory by hand. The cache
        // dominates the cost, so iterating typed records is also
        // faster than the original folder-walk + ScanAllJobs FirstOrDefault.
        foreach (var info in _taskAccess.ListByLaneInWorkspace(entry.Path, TaskStates.AutoReview))
        {
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
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
            var done = ReviewDecisionParsing.FindUnresolvedDone(log);

            if (needs == null && noop == null && blocked == null && done == null) continue;

            int needsLine = needs?.LineNumber ?? -1;
            int noopLine = noop?.LineNumber ?? -1;
            int blockedLine = blocked?.LineNumber ?? -1;
            int doneLine = done?.LineNumber ?? -1;

            // Priority is "latest unresolved sentinel wins". The terminal
            // markers (BLOCKED, NOOP, DONE) all live at the same level;
            // the latest one in the log is the one to act on. NEEDS_INPUT
            // keeps the existing handling.
            var maxTerminal = Math.Max(Math.Max(blockedLine, noopLine), doneLine);

            if (maxTerminal >= needsLine && blockedLine == maxTerminal && blocked != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.Blocked, blocked.LineNumber, blocked.Reason, NeedsInput: null);
            }
            else if (maxTerminal >= needsLine && noopLine == maxTerminal && noop != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.NoOp, noop.LineNumber, noop.Reason, NeedsInput: null);
            }
            else if (maxTerminal >= needsLine && doneLine == maxTerminal && done != null)
            {
                yield return new PendingDecision(info, ReviewSignalKind.Done, done.LineNumber, Reason: null, NeedsInput: null);
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

    /// <summary>
    /// Legacy fallback for tests that construct the orchestrator without
    /// a <see cref="OrchestratorApi.Services.Cli.OneShot.CliOneShotRegistry"/>.
    /// Stdin-piped — replaces the previous <c>-p &lt;prompt&gt;</c> argv path
    /// that caused the 2026-05-11 empty-reply incident on Windows.
    /// </summary>
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
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("--dangerously-skip-permissions");

        using var p = Process.Start(psi);
        if (p == null) return string.Empty;
        try
        {
            await p.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            p.StandardInput.Close();
        }
        catch { /* stdin may already be closed by CLI */ }

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return await stdoutTask;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return string.Empty;
        }
    }

    /// <summary>
    /// Reissue tag id stamped on the job's tags array when an auto-review
    /// reissue parks it in <c>2-ready</c>. UI-only signal so the kanban
    /// can render the card distinctly from a fresh queued task; the
    /// runtime priority is carried by order 0, not by this tag.
    /// </summary>
    internal const string ReissueTagId = "reissue:autoreview";

    /// <summary>
    /// Lane-target for every auto-review reissue path (NEEDS_INPUT,
    /// NOOP recovery, multi-aspect block). Routes the task to
    /// <c>2-ready</c> at order 0, stamps the reissue tag for UI
    /// highlighting, and returns the moved <see cref="TaskInfo"/> so the
    /// caller can write follow-up evidence next to the prompt. Returning
    /// <c>null</c> means the move did not complete (logged and recorded
    /// in the decision journal upstream); the caller then skips the
    /// follow-up file write.
    ///
    /// <para>
    /// Why not 3-progress: the pre-fix path moved straight to
    /// <c>3-progress</c> while the runner-pickup tick observed an empty
    /// lane and grabbed the next queued job (the 2026-05-11 incident).
    /// Routing to <c>2-ready</c> instead keeps reissues out of the
    /// "currently running" bucket; order 0 guarantees the runner picks
    /// the reissue as the very next task without displacing the active
    /// one. See ADR-0025 and the lane-write-3-progress-forbidden drift
    /// rule.
    /// </para>
    /// </summary>
    private TaskInfo? MoveReissueToReadyTop(TaskInfo current, WatchPathEntry entry, string causeLabel)
    {
        var move = _stateMachine.MoveJob(current.Id, TaskStates.Ready, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to ready (reissue after {Cause}): {Status} {Message}",
                current.Id, causeLabel, move.Status, move.Message);
            return null;
        }

        // Use the authoritative post-move path from MoveJob rather than
        // a re-scan: the cache may not yet reflect the move, and writes
        // through OrchestratorChatLog auto-create their parent, so a
        // stale path would resurrect 4-auto-review as a one-line skeleton
        // (see HandleAcceptAsDone for the same fix).
        var movedFolderPath = move.NewFolderPath
            ?? Path.Combine(entry.Path, TaskStates.Ready, Path.GetFileName(current.FolderPath));
        var moved = current with { FolderPath = movedFolderPath, State = TaskStates.Ready };

        // Order 0 lifts the reissue ahead of any fresh ready job (which
        // typically uses order >= 10) without rewriting their orders.
        // The runner picks by OrderBy(j => j.Order).
        TaskJsonFile.UpdateOrder(moved.FolderPath, 0, _logger);
        // UI hint only; the kanban shows the reissue tag distinctly.
        // Routed through ConcernTagWriter because the tag id uses the
        // namespace:value grammar that TaskMutationService.NormalizeTagId
        // would strip the colon from.
        ConcernTagWriter.MergeConcernTags(moved.FolderPath, new[] { ReissueTagId }, _logger);
        _scanner.InvalidateCache();
        return moved;
    }

    private async Task WriteFollowUpFileAsync(TaskInfo moved, string followUp, CancellationToken ct)
    {
        try
        {
            var followUpPath = Path.Combine(moved.FolderPath, "orchestrator-follow-up.md");
            await File.WriteAllTextAsync(
                followUpPath,
                $"# Orchestrator follow-up\n\n{followUp}\n",
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: failed to write follow-up file for {JobId}",
                moved.Id);
        }
    }

    private enum ReviewSignalKind
    {
        NeedsInput,
        NoOp,
        Blocked,
        Done
    }

    private sealed record PendingDecision(
        TaskInfo Job,
        ReviewSignalKind Kind,
        int LineNumber,
        string? Reason,
        NeedsInputState? NeedsInput);

    private sealed record NoOpProgressEvidence(
        bool SawNoOpRecoveryReissue,
        int ToolCalls,
        int FileChanges,
        int AgentSubstanceChars)
    {
        public static readonly NoOpProgressEvidence None = new(false, 0, 0, 0);
    }
}
