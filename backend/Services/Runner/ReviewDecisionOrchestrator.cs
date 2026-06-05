using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.RegressionRadar;
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

    /// <summary>
    /// Default cap on concurrently-running DONE aspect-reviews in the read-only
    /// parallel pool (ADR-0052). Mirrors the in-aspect WhenAll cap of 4. Override
    /// with <c>ReviewDecisionOrchestrator:MaxParallelReviews</c>.
    /// </summary>
    public const int DefaultMaxParallelReviews = 4;

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

    // Guards _callTimestamps: the read-only review pool processes several DONE
    // tasks concurrently, each charging the per-hour rate budget, so the
    // sliding-window queue is mutated from multiple threads.
    private readonly object _callTimestampsLock = new();

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
    private readonly RegressionRadarService? _regressionRadar;

    /// <summary>
    /// Regression-radar analysis injection point. Tests substitute a
    /// deterministic stub so the post-step can be exercised without git or
    /// a session timeline. Args: jobId, watchPath -> classification result.
    /// Null in production, where the post-step calls the injected
    /// <see cref="RegressionRadarService"/> directly.
    /// </summary>
    public Func<string, string?, RegressionRadarResult>? RegressionRadarAnalyzer { get; set; }
    private readonly TimelineLog? _timeline;
    private readonly ProjectSettingsService? _projectSettings;
    // The system-escalation funnel. Used here only for the boot-time repair of
    // legacy 5-human-review cards that carry no verdict (RecordVerdictAndStatus,
    // no move). Optional so test fixtures that do not exercise the backfill keep
    // their existing constructor; production DI always supplies it.
    private readonly HumanReviewEscalation? _humanReviewEscalation;

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
        ProjectSettingsService? projectSettings = null,
        HumanReviewEscalation? humanReviewEscalation = null,
        RegressionRadarService? regressionRadar = null)
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
        _humanReviewEscalation = humanReviewEscalation;
        _regressionRadar = regressionRadar;

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

        // Pure data-repair, run on EVERY boot regardless of the Enabled flag:
        // give any 5-human-review card that carries no orchestrator verdict a
        // retroactive Escalate verdict + status.md stub so the board can explain
        // it. These are the cards that landed there before the escalation funnel
        // existed (the bug this fixes). Idempotent - a card with a verdict is
        // skipped, so repeated boots are no-ops.
        try { BackfillVerdictlessHumanReview(workspace!, stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogWarning(ex, "ReviewDecisionOrchestrator: verdict-less human-review backfill failed"); }

        if (_configuration.GetValue("ReviewDecisionOrchestrator:Enabled", false))
        {
            try
            {
                _logger.LogInformation("ReviewDecisionOrchestrator boot sweep starting (one-shot full backfill).");
                await TickOnceAsync(workspace!, stoppingToken);
                // One-shot migration for the "concern tags bleiben kleben" bug:
                // strip stale concern chips from already-parked accept cards in
                // 4-auto-review / 5-human-review that the merge-only path left
                // behind before the reconcile fix shipped. Idempotent.
                BackfillStaleConcernTags(workspace!, stoppingToken);
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
        var maxParallelReviews = ParallelSlotPolicy.ClampMax(
            _configuration.GetValue("ReviewDecisionOrchestrator:MaxParallelReviews", DefaultMaxParallelReviews));
        var aspects = ResolveAspectRunners();

        _statusSnapshot.BeginTick();
        try
        {
            // DONE aspect-reviews write no repo files, so they are not bound to
            // the sequential code-seat: collect them here and run them through a
            // bounded read-only parallel pool below (Req 1 / ADR-0052). Every
            // other kind stays sequential - they are cheap/deterministic and
            // some mutate shared lane state, so parallelising them buys nothing.
            var doneReviews = new List<(WatchPathEntry Entry, PendingDecision Pending)>();

            foreach (var entry in _scanner.GetWatchPaths())
            {
                if (string.IsNullOrWhiteSpace(entry.Path)) continue;
                if (!Directory.Exists(entry.Path)) continue;

                foreach (var pending in EnumeratePending(workspace, entry))
                {
                    if (ct.IsCancellationRequested) return;
                    _statusSnapshot.RecordPending();

                    if (pending.Kind == ReviewSignalKind.Done)
                    {
                        // Defer to the read-only parallel pool. The cheap
                        // enabled-checks happen here so a disabled/no-aspect
                        // project never occupies a slot.
                        if (aspects.Count == 0 ||
                            !_configuration.GetValue("ReviewDecisionOrchestrator:AspectsEnabled", true))
                        {
                            continue;
                        }
                        doneReviews.Add((entry, pending));
                        continue;
                    }

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

                        if (pending.Kind == ReviewSignalKind.NoCompletionSignal)
                        {
                            // No terminal sentinel arrived. Ask the
                            // review-decision model to classify the real
                            // reply first (ASS-684); fall back to the
                            // deterministic sentinel-demanding reissue only
                            // if the model is unavailable or malformed.
                            await ProcessNoCompletionSignalAsync(
                                workspace, entry, pending, maxReissues, ct);
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

            // Req 1: drain the collected DONE reviews through the read-only
            // parallel pool. Runs after the sequential pass so the deterministic
            // kinds above never wait behind a multi-aspect review.
            await RunReadOnlyReviewPoolAsync(
                workspace, doneReviews, aspects, cliBinary, aspectModel,
                TimeSpan.FromSeconds(aspectTimeoutSeconds), maxPerHour, maxParallelReviews, ct);
        }
        finally
        {
            _statusSnapshot.EndTick();
        }
    }

    /// <summary>
    /// Drains the DONE aspect-reviews collected in one tick through a bounded
    /// read-only parallel pool. ADR-0052: an aspect review writes no repo files,
    /// so each task is a <see cref="TaskParallelism.ReadOnlyTask"/> candidate the
    /// <see cref="ParallelSlotPolicy"/> admits without scope analysis, decoupled
    /// from the sequential code-seat (<c>ProjectRunner._activeJobId</c>). The pool
    /// is capped at <paramref name="maxParallel"/> concurrent reviews and shares
    /// the per-hour rate budget: each admitted review charges one call, and once
    /// the budget is spent the remaining reviews are left for the next tick.
    /// </summary>
    private async Task RunReadOnlyReviewPoolAsync(
        string workspace,
        IReadOnlyList<(WatchPathEntry Entry, PendingDecision Pending)> doneReviews,
        IReadOnlyList<string> aspects,
        string cliBinary,
        string aspectModel,
        TimeSpan perAspectTimeout,
        int maxPerHour,
        int maxParallel,
        CancellationToken ct)
    {
        if (doneReviews.Count == 0) return;
        var max = ParallelSlotPolicy.ClampMax(maxParallel);

        var running = new List<(Task Task, RunningTask Slot)>();
        try
        {
            foreach (var (entry, pending) in doneReviews)
            {
                if (ct.IsCancellationRequested) break;

                // Wait for a slot. For read-only candidates ParallelSlotPolicy
                // admits as soon as one of the `max` slots is free, so the loop
                // parks on WhenAny until a running review drains.
                while (true)
                {
                    running.RemoveAll(r => r.Task.IsCompleted);
                    var slots = running.Select(r => r.Slot).ToList();
                    var admission = ParallelSlotPolicy.Decide(
                        pending.Job.Id, TaskParallelism.ReadOnlyTask, slots, max);
                    if (admission.Admitted) break;
                    await Task.WhenAny(running.Select(r => r.Task));
                }

                // Share the per-hour budget across the whole pool: charge before
                // launching, and stop admitting (but let in-flight reviews finish)
                // once the budget is spent so we never exceed CallsPerHour.
                if (!RateLimitOk(maxPerHour))
                {
                    _logger.LogInformation(
                        "ReviewDecisionOrchestrator rate limit reached ({MaxPerHour}/h); deferring {Count} DONE review(s) to next tick",
                        maxPerHour, doneReviews.Count - running.Count);
                    break;
                }
                RecordRateLimitedCall();

                var slot = new RunningTask(pending.Job.Id, TaskParallelism.ReadOnlyTask);
                var task = Task.Run(async () =>
                {
                    _statusSnapshot.SetCurrent(entry.Name, pending.Job.Id);
                    try
                    {
                        await ProcessDoneAsync(workspace, entry, pending, aspects, cliBinary,
                            aspectModel, perAspectTimeout, ct);
                        _statusSnapshot.RecordAspectsRun(aspects.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "ReviewDecisionOrchestrator failed to process {Project}/{JobId}",
                            entry.Name, pending.Job.Id);
                    }
                }, ct);
                running.Add((task, slot));
            }
        }
        finally
        {
            // Always await everything we launched, even on cancellation, so a
            // review never outlives the tick that owns its status-snapshot frame.
            await Task.WhenAll(running.Select(r => r.Task));
        }
    }

    /// <summary>
    /// One-shot boot-sweep backfill for the "concern tags bleiben kleben" bug.
    /// Scans <c>4-auto-review</c> and <c>5-human-review</c> and, for every card
    /// whose latest verdict is <c>accept</c> with no active runner-outcome
    /// issue, strips the aspect-concern tags the latest accept did not actually
    /// raise (accept-with-concerns cards keep exactly the concerns recorded on
    /// the decision). Deterministic, idempotent, and safe to run on every boot:
    /// once cleaned a card has no drift, so subsequent sweeps are no-ops. Public
    /// so tests can drive it against a temp workspace. See <see cref="TagDriftRule"/>.
    /// </summary>
    public void BackfillStaleConcernTags(string workspace, CancellationToken ct)
    {
        List<TaskInfo> jobs;
        try { jobs = _scanner.ScanAllJobs(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReviewDecisionOrchestrator: concern-tag backfill scan failed");
            return;
        }

        var decisionsByProject = new Dictionary<string, IReadOnlyList<ReviewDecisionRecord>>(StringComparer.OrdinalIgnoreCase);
        var cleaned = 0;

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) return;
            if (job.State != TaskStates.AutoReview && job.State != TaskStates.HumanReview) continue;
            if (job.Tags.Count == 0 || !job.Tags.Any(TagDriftRule.IsAspectConcernTag)) continue;
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;

            if (!decisionsByProject.TryGetValue(job.ProjectName, out var records))
            {
                try { records = ReviewDecisionLog.ReadAll(workspace, job.ProjectName); }
                catch { records = Array.Empty<ReviewDecisionRecord>(); }
                decisionsByProject[job.ProjectName] = records;
            }

            ReviewDecisionRecord? latest = null;
            for (var i = records.Count - 1; i >= 0; i--)
            {
                if (records[i].JobId == job.Id) { latest = records[i]; break; }
            }

            // AC2 gate: only touch cards the orchestrator accepted, with no
            // active outcome issue. accept-with-concerns is preserved because we
            // reconcile against the concern set the accept actually recorded.
            if (latest?.Kind != ReviewDecisionKind.AcceptAsDone) continue;
            if (job.OutcomeIssue != null) continue;

            var justified = TagDriftRule.ExtractConcernTagIds(latest.Reason);
            var drift = TagDriftRule.FindDriftingConcernTags(job.Tags, justified, "accept", hasOutcomeIssue: false);
            if (drift.Count == 0) continue;

            ConcernTagWriter.ReconcileConcernTags(job.FolderPath, justified, _logger);
            cleaned++;
            _logger.LogInformation(
                "ReviewDecisionOrchestrator: tag-drift backfill stripped {Count} stale concern tag(s) from {Project}/{JobId} in {State}: [{Drift}] (kept [{Kept}])",
                drift.Count, job.ProjectName, job.Id, job.State, string.Join(", ", drift), string.Join(", ", justified));
        }

        if (cleaned > 0)
        {
            _logger.LogInformation("ReviewDecisionOrchestrator: tag-drift backfill cleaned {Cleaned} card(s).", cleaned);
        }
    }

    /// <summary>
    /// One-shot boot repair for the bug
    /// <c>karten-landen-in-5-human-review-ohne-verdict-und-ohne-statusmarkdown</c>:
    /// every card parked in <c>5-human-review</c> whose per-project decision
    /// journal holds NO record for that job gets a retroactive
    /// <see cref="ReviewDecisionKind.Escalate"/> verdict (category
    /// <see cref="HumanReviewEscalationCategories.UnknownLegacy"/>) and a minimal
    /// <c>status.md</c> stub, written through <see cref="HumanReviewEscalation"/>.
    /// These are the cards that reached the lane through the pre-funnel
    /// ProjectRunner paths, so the board showed them as done-but-blank with
    /// <c>orchestratorVerdict == null</c>. Idempotent: the gate is "no existing
    /// verdict record", and the status stub is never written over a real summary,
    /// so re-running on later boots is a no-op. Public so tests can drive it.
    /// </summary>
    public void BackfillVerdictlessHumanReview(string workspace, CancellationToken ct)
    {
        if (_humanReviewEscalation == null)
        {
            _logger.LogDebug("ReviewDecisionOrchestrator: no HumanReviewEscalation funnel injected; skipping verdict-less backfill.");
            return;
        }

        List<TaskInfo> jobs;
        try { jobs = _scanner.ScanAllJobs(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ReviewDecisionOrchestrator: verdict-less backfill scan failed");
            return;
        }

        var decisionsByProject = new Dictionary<string, IReadOnlyList<ReviewDecisionRecord>>(StringComparer.OrdinalIgnoreCase);
        var repairedKeys = new HashSet<string>(StringComparer.Ordinal);
        var repaired = 0;

        foreach (var job in jobs)
        {
            if (ct.IsCancellationRequested) return;
            if (job.State != TaskStates.HumanReview) continue;
            if (string.IsNullOrWhiteSpace(job.ProjectName)) continue;

            if (!decisionsByProject.TryGetValue(job.ProjectName, out var records))
            {
                try { records = ReviewDecisionLog.ReadAll(workspace, job.ProjectName); }
                catch { records = Array.Empty<ReviewDecisionRecord>(); }
                decisionsByProject[job.ProjectName] = records;
            }

            // Gate: a card that already has ANY verdict record is explained -
            // the endpoint-derived OrchestratorVerdict reads the latest of these.
            // Only verdict-less cards are the legacy ones this repairs. The
            // HashSet keeps a second folder for the same job id (rare, e.g. mid
            // crash-recovery) from being appended twice in one sweep.
            if (records.Any(r => r.JobId == job.Id)) continue;
            if (!repairedKeys.Add($"{job.ProjectName} {job.Id}")) continue;

            _humanReviewEscalation.RecordVerdictAndStatus(
                job.ProjectName, job.Id, job.FolderPath,
                HumanReviewEscalationCategories.UnknownLegacy,
                "Parked in human review before the escalation funnel existed; no automated review ran.");

            repaired++;
            _logger.LogInformation(
                "ReviewDecisionOrchestrator: verdict-less backfill gave {Project}/{JobId} a retroactive escalate verdict (category={Category}).",
                job.ProjectName, job.Id, HumanReviewEscalationCategories.UnknownLegacy);
        }

        if (repaired > 0)
            _logger.LogInformation("ReviewDecisionOrchestrator: verdict-less human-review backfill repaired {Repaired} card(s).", repaired);
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
            RecordRateLimitedCall();
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
        // recoverable by another sharpened prompt. Escalate it to
        // 5-human-review so it is not picked again automatically.
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
            await EscalateNoOpAsync(workspace, entry, pending, reason, ct);
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

    private Task EscalateNoOpAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string reason,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator could not auto-recover NOOP. Reason: {reason}. Promoted to {TaskStates.HumanReview}.");

        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to {TargetState} after NOOP escalate: {Status} {Message}",
                current.Id, TaskStates.HumanReview, move.Status, move.Message);
        }

        // ADR-0049: escalation records the event on the original card's
        // timeline and leaves it in the human-review lane - no wrapper card.
        EmitVerdictTimeline(move.NewFolderPath ?? current.FolderPath,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("noop-escalate", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic NOOP branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty));

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deterministic-completion contract (requirement 4): the run finished
    /// without emitting any terminal sentinel. Mirrors <see cref="ProcessNoOpAsync"/> -
    /// reissue with a sentinel-demanding follow-up while the shared reissue
    /// budget allows, otherwise escalate to human review. The run is NEVER
    /// accepted as done here: a job counts as completed only when the
    /// deterministic signal is present.
    /// </summary>
    private async Task ProcessNoCompletionSignalAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        int maxReissues,
        CancellationToken ct)
    {
        var (taskTitle, taskBody) = LoadTaskTitleAndBody(pending);
        var promptUsable = IsPromptUsable(taskTitle, taskBody);

        // Empty / placeholder prompt: re-running cannot help because there is
        // nothing actionable to drive toward a sentinel. Hand to a human.
        if (!promptUsable)
        {
            var reason = "Run finished without a terminal sentinel and the task prompt is empty or placeholder; cannot auto-recover.";
            await EscalateNoCompletionSignalAsync(workspace, entry, pending, reason, ct);
            return;
        }

        var prior = CountPriorReissues(workspace, entry.Name, pending.Job.Id);

        // Budget exhausted: stop looping and hand to a human. Crucially we do
        // not fall back to "accept as done" - the deterministic completion
        // signal never arrived, so a human must judge the work.
        if (prior >= maxReissues)
        {
            var reason = $"Run finished without a terminal sentinel after {prior} prior orchestrator reissue(s); user attention required.";
            await EscalateNoCompletionSignalAsync(workspace, entry, pending, reason, ct);
            return;
        }

        // Budget left: reissue, explicitly demanding a terminal sentinel on
        // close-out. A silent finish is a reviewable signal, not something to
        // ignore: run the same completion-gate scan over the run's own evidence
        // and, when it finds unfinished work (open items / build failures), append
        // those items so the reissue foregrounds them instead of only demanding a
        // sentinel.
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var (_, recentLog) = LoadTaskContext(pending);
        var findings = CompletionGate.ExtractFindings(LoadStatusSummary(current.FolderPath), recentLog);

        var followUp = RunOutcomePolicy.BuildMissingSentinelInterventionPrompt(
            "the previous run ended without any [[TASK_DONE]] / [[TASK_BLOCKED]] / [[TASK_NEEDS_INPUT]] / [[TASK_NOOP]] sentinel");
        if (findings.Count > 0)
        {
            followUp += "\n\n" + CompletionGate.BuildFollowUp(findings);
        }
        await ReissueNoCompletionSignalAsync(workspace, entry, pending, followUp, findings.Count, ct);
    }

    private async Task ReissueNoCompletionSignalAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string followUp,
        int gateFindings,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;
        var reason = gateFindings > 0
            ? $"Run finished without a terminal sentinel and its own close-out lists {gateFindings} unfinished item(s); reissuing with them foregrounded."
            : "Run finished without a terminal sentinel; reissuing and demanding a deterministic close-out signal.";

        _chatLog.Append(current, OrchestratorMessageKind.Reissue,
            $"Decision: reissue (no completion signal). Reason: {reason}");

        var moved = MoveReissueToReadyTop(current, entry, "NO-SIGNAL");
        if (moved != null)
        {
            // Post-core Orchestrator-Review row: the silent-finish reissue is the
            // same completeness gate firing without a sentinel, so record it for
            // the Overview pipeline.
            RecordOrchestratorReviewStep(moved.FolderPath, PipelineStepStatus.Failed,
                DecisionVerdictReissue, reason);
            await WriteFollowUpFileAsync(moved, followUp, ct);
            EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
                TimelineActors.QualityLoop,
                "Reopened: run finished without a terminal sentinel, reissued demanding one.",
                BuildReopenDetails("no-completion-signal",
                    CountPriorReissues(workspace, entry.Name, current.Id),
                    reason));
        }

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: reason,
            Prompt: "(deterministic no-completion-signal branch)",
            Response: "(no fast-model call)",
            FollowUp: followUp));
    }

    private Task EscalateNoCompletionSignalAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string reason,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        _chatLog.AppendSupervisor(current, "escalate",
            $"Orchestrator could not obtain a deterministic completion signal. Reason: {reason}. Promoted to 5-human-review.");

        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after no-completion-signal escalate: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
        RecordOrchestratorReviewStep(escalatedFolder, PipelineStepStatus.Failed,
            DecisionVerdictEscalate, reason);

        EmitVerdictTimeline(escalatedFolder,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("no-completion-signal", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic no-completion-signal branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty));

        return Task.CompletedTask;
    }

    private Task ProcessBlockedAsync(
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

        // ADR-0025: BLOCKED escalations move the task to 5-human-review.
        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after BLOCKED: {Status} {Message}",
                current.Id, move.Status, move.Message);
        }

        // ADR-0049: the lane move + this timeline event are the handover -
        // no wrapper card.
        EmitVerdictTimeline(move.NewFolderPath ?? current.FolderPath,
            TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, reason,
            BuildEscalateDetails("agent-blocked", reason,
                CountPriorReissues(workspace, entry.Name, current.Id)));

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: reason,
            Prompt: "(deterministic BLOCKED branch)",
            Response: "(no fast-model call)",
            FollowUp: string.Empty));

        return Task.CompletedTask;
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
        // we own the complete mark. Stand-alone tests that wire the
        // orchestrator without a PipelineExecutionLog skip this entirely
        // (the recorder is fully optional).
        //
        // EnsureRun (not Begin): the core agent run already opened this
        // record in ProjectRunner and stamped the CORE "Agent execution"
        // step with its real duration/outcome. Begin would overwrite the
        // file and reset CORE to Pending - the bug where CORE showed "- -"
        // forever while the aspect rows below it completed. EnsureRun
        // resumes the in-flight record so CORE survives, and only begins a
        // fresh one when no run record exists yet (legacy / hand-moved job).
        _pipelineLog?.EnsureRun(current.FolderPath, PipelineCatalogue.Standard, entry.Name, current.Id);

        // Post-core completeness gate (Orchestrator-Review, the first post-step):
        // before spending the parallel aspect review, scan the run's OWN close-out
        // evidence - status Open Items / Notes, the Result line, and the log tail -
        // for unfinished-work signals: open checklist boxes, self-reported build /
        // compile / test failures, or a success claim contradicted by a build
        // error. A hit short-circuits the accept and drives the task to a
        // conclusion: reissue with the items foregrounded while the shared reissue
        // budget allows, otherwise escalate to 5-human-review. This closes the
        // silent-completion gap (ASS-764 self-reported build error accepted with
        // concerns; ASS-766 silent finish + open items parked without a verdict)
        // where a run says done while its own evidence still lists open work.
        var gate = CompletionGate.Evaluate(
            statusSummary, recentLog,
            CountPriorReissues(workspace, entry.Name, current.Id),
            ConfiguredMaxReissues());
        if (gate.IsIncomplete)
        {
            await HandleCompletionGateAsync(workspace, entry, pending, current, gate, ct);
            return;
        }

        // Gate clean: record the post-core Orchestrator-Review row as a passed
        // completeness check so the Overview pipeline shows the gate ran ahead of
        // the aspect verdicts, then fall through to the normal aspect review and
        // the final Orchestrator-Review decision below.
        RecordOrchestratorReviewStep(current.FolderPath, PipelineStepStatus.Passed,
            ReviewVerdictComplete, gate.Reason);

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
        Func<string, string?>? thinkingLevelForAspect = settings is null
            ? null
            : aspectId =>
            {
                var resolvedModel = modelForAspect?.Invoke(aspectId) ?? aspectModel;
                return PipelineStepConfigResolver.ResolveThinkingLevel(
                    settings,
                    $"aspect-{aspectId}",
                    CliTypes.Claude,
                    resolvedModel);
            };

        var report = await _aspectRunner.RunAsync(inputs, enabledAspects, cliBinary, aspectModel, perAspectTimeout, ct, modelForAspect, thinkingLevelForAspect);

        // ASS-563: run the lint-scss post-step BEFORE the pipeline Complete
        // mark so its step record lands in pipeline-execution.json while
        // the file is still in its in-flight state. Skipped/Ok/Warn just
        // record; Fail short-circuits the move-to-review path with a
        // reissue (or, if we've already reissued once, an escalation).
        var lintResult = await RunLintScssPostStepAsync(workspace, entry, current, ct);

        // Regression radar post-step: a deterministic spec-change classification
        // recorded alongside lint so the Overview pipeline lists it with a
        // status + duration. Reporting only - the verdict never gates the move
        // to review (unlike lint Fail above), so it sits between lint and the
        // Complete mark and feeds nothing into the decision branches below.
        RunRegressionRadarPostStep(entry, current);

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

        // Evidence gate (ASS-764): a bare success claim is not acceptance. For a
        // UI/bug task that left no visual proof, or when the tests-and-evidence
        // aspect is not clean (failing build/tests, missing evidence, +0/-0
        // "test" commit), demand verification instead of accepting with concerns:
        // reissue with a screenshot/e2e + green-build demand while the shared
        // reissue budget allows, otherwise escalate to 5-human-review.
        var evidenceGate = EvidenceGate.Evaluate(
            EvidenceGate.RequiresVisualEvidence(current.TaskType, current.Tags, current.Title),
            EvidenceGate.HasVisualEvidence(current.FolderPath),
            report,
            CountPriorReissues(workspace, entry.Name, current.Id),
            ConfiguredMaxReissues());
        if (evidenceGate.IsBlocking)
        {
            await HandleEvidenceGateAsync(workspace, entry, pending, current, report, evidenceGate, ct);
            return;
        }

        // Reconcile (not merge-only): set the concern tags to exactly this
        // pass's set. A follow-up pass that now passes cleanly - or raises
        // fewer concerns than before - must STRIP the stale concern chips an
        // earlier pass left behind, not leave them stuck. Runs even when the
        // current set is empty. See the "concern tags bleiben kleben" bug.
        ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

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
        if (!string.Equals(movedFolderPath, current.FolderPath, StringComparison.OrdinalIgnoreCase))
        {
            ConcernTagWriter.ReconcileConcernTags(movedFolderPath, report.ConcernTagIds, _logger);
        }

        // Final verdict step: the orchestrator's single ruling after the
        // parallel aspects. Recorded on the post-move folder (the pipeline
        // record travelled with the lane move) so the Overview pipeline shows
        // it as the distinct "Auto-review decision" row below the aspects.
        RecordOrchestratorDecisionStep(movedFolderPath, PipelineStepStatus.Passed,
            report.Overall == AspectStatus.Concerns
                ? DecisionVerdictAcceptWithConcerns
                : DecisionVerdictAccept,
            report.Overall == AspectStatus.Concerns
                ? FormatConcernCount(report)
                : "all aspects pass");

        // Provenance: the orchestrator (not a human) advanced this task
        // toward Completed. Stamp on the authoritative post-move path.
        ConcernTagWriter.MergeConcernTags(movedFolderPath, new[] { OrchestratorMovedTagId }, _logger);

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

        // Final verdict step: reissue. Recorded on the post-move folder so the
        // Overview pipeline shows the orchestrator's ruling distinctly from the
        // parallel aspect rows that drove it.
        RecordOrchestratorDecisionStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, "Multi-aspect block: " + AspectSummaryLine(report));

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
    /// Drive a blocking evidence-gate decision (ASS-764) to a conclusion. The
    /// aspects passed (or only raised non-blocking concerns), but the run is
    /// unverified: a UI/bug task with no visual proof, or an unclean
    /// tests-and-evidence aspect. Reissue (budget left) sends the card back to
    /// 2-ready with a verification demand foregrounded; escalate (budget spent)
    /// hands it to 5-human-review. Records the final
    /// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> row so the
    /// Overview pipeline shows the ruling. The in-flight pipeline run was already
    /// completed by the caller, so the record travels with the lane move.
    /// </summary>
    private async Task HandleEvidenceGateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        AspectRunReport report,
        EvidenceGate.Decision gate,
        CancellationToken ct)
    {
        var findingsBlock = string.Join("; ", gate.Findings.Take(EvidenceGate.MaxFindings));
        var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);

        if (gate.Action == EvidenceGate.EvidenceGateAction.Escalate)
        {
            // Reconcile the aspect-concern chips to this pass's set before
            // handing to a human, same as the accept path, so the review starts
            // from a current tag set rather than stale chips.
            ConcernTagWriter.ReconcileConcernTags(current.FolderPath, report.ConcernTagIds, _logger);

            _chatLog.AppendSupervisor(current, "escalate",
                $"Auto-review could not verify this task's result. Reason: {gate.Reason}. Promoted to 5-human-review.");

            var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
            if (move.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after evidence-gate escalate: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }

            var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
            RecordOrchestratorDecisionStep(escalatedFolder, PipelineStepStatus.Failed,
                DecisionVerdictEscalate, gate.Reason);

            EmitVerdictTimeline(escalatedFolder,
                TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, gate.Reason,
                BuildEscalateDetails("evidence-gate", gate.Reason, priorReissues));

            ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: gate.Reason,
                Prompt: "(evidence-gate static check)",
                Response: findingsBlock,
                FollowUp: string.Empty));

            _statusSnapshot.RecordEscalate();
            return;
        }

        // Reissue: foreground the verification demand so the next run proves the
        // result instead of re-asserting it.
        var followUp = EvidenceGate.BuildFollowUp(gate);
        var moved = MoveReissueToReadyTop(current, entry, "evidence-gate");
        if (moved == null)
        {
            // Move failed -> no operator-facing banner; the DONE stays unresolved
            // and the next tick retries.
            return;
        }

        RecordOrchestratorDecisionStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, gate.Reason);

        await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        var count = gate.Findings.Count;
        var noun = count == 1 ? "item" : "items";
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready ({count} unverified {noun}; evidence required).");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: evidence gate requires verification for {count} {noun}.",
            BuildReopenDetails("evidence-gate",
                CountPriorReissues(workspace, entry.Name, current.Id), findingsBlock));

        _statusSnapshot.RecordReissue();

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: gate.Reason,
            Prompt: "(evidence-gate static check)",
            Response: findingsBlock,
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
    /// Verdict tokens stamped on the
    /// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> step. This is
    /// the orchestrator's single final ruling, aggregated from the parallel
    /// aspect verdicts (plus the lint gate): <c>accept</c> when every aspect
    /// passed, <c>accept-with-concerns</c> when it advances despite non-blocking
    /// concerns, <c>reissue</c> when it sends the task back to 2-ready, and
    /// <c>escalate</c> when it hands the task to a human. The FE Overview
    /// pipeline renders the token on the "Auto-review decision" row's pill.
    /// </summary>
    internal const string DecisionVerdictAccept = "accept";
    internal const string DecisionVerdictAcceptWithConcerns = "accept-with-concerns";
    internal const string DecisionVerdictReissue = "reissue";
    internal const string DecisionVerdictEscalate = "escalate";

    /// <summary>
    /// Verdict token for a clean post-core
    /// <see cref="PipelineCatalogue.OrchestratorReviewStepId"/> completeness
    /// check: the run's own close-out carried no unfinished-work evidence, so the
    /// gate let the task proceed to the aspect review. A non-clean gate stamps
    /// <see cref="DecisionVerdictReissue"/> or <see cref="DecisionVerdictEscalate"/>
    /// instead, mirroring the final decision row's vocabulary.
    /// </summary>
    internal const string ReviewVerdictComplete = "complete";

    /// <summary>
    /// Drive a non-clean completion-gate decision to a conclusion so the task can
    /// never park in 4-auto-review without a verdict. Reissue (budget left) sends
    /// the card back to 2-ready with the gate's findings foregrounded into a
    /// follow-up; escalate (budget spent) hands it to 5-human-review. Both record
    /// the post-core <see cref="PipelineCatalogue.OrchestratorReviewStepId"/> row
    /// so the gate's ruling is visible in the Overview pipeline. The in-flight
    /// pipeline run is completed before the lane move so the record travels with
    /// the folder.
    /// </summary>
    private async Task HandleCompletionGateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        TaskInfo current,
        CompletionGate.Decision gate,
        CancellationToken ct)
    {
        _pipelineLog?.Complete(current.FolderPath);

        var findingsBlock = string.Join("; ", gate.Findings.Take(CompletionGate.MaxFindings));
        var priorReissues = CountPriorReissues(workspace, entry.Name, current.Id);

        if (gate.Action == CompletionGate.CompletionGateAction.Escalate)
        {
            _chatLog.AppendSupervisor(current, "escalate",
                $"Auto-review completion gate could not clear unfinished-work evidence. Reason: {gate.Reason}. Promoted to 5-human-review.");

            var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
            if (move.Status != MoveJobStatus.Success)
            {
                _logger.LogWarning(
                    "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after completion-gate escalate: {Status} {Message}",
                    current.Id, move.Status, move.Message);
            }

            var escalatedFolder = move.NewFolderPath ?? current.FolderPath;
            RecordOrchestratorReviewStep(escalatedFolder, PipelineStepStatus.Failed,
                DecisionVerdictEscalate, gate.Reason);

            EmitVerdictTimeline(escalatedFolder,
                TimelineEventKinds.OrchestratorEscalated, TimelineActors.Orchestrator, gate.Reason,
                BuildEscalateDetails("completion-gate", gate.Reason, priorReissues));

            ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
                CreatedAt: DateTime.UtcNow,
                JobId: current.Id,
                Project: entry.Name,
                Kind: ReviewDecisionKind.Escalate,
                Reason: gate.Reason,
                Prompt: "(completion-gate static scan)",
                Response: findingsBlock,
                FollowUp: string.Empty));

            _statusSnapshot.RecordEscalate();
            return;
        }

        // Reissue: foreground the gate's findings so the next run finishes the
        // open work instead of restarting blind.
        var followUp = CompletionGate.BuildFollowUp(gate.Findings);
        var moved = MoveReissueToReadyTop(current, entry, "completion-gate");
        if (moved == null)
        {
            // Move failed -> no operator-facing banner; the DONE stays unresolved
            // and the next tick retries.
            return;
        }

        RecordOrchestratorReviewStep(moved.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, gate.Reason);

        await WriteFollowUpFileAsync(moved, followUp, ct);

        var title = string.IsNullOrWhiteSpace(moved.Title) ? moved.Id : moved.Title;
        var count = gate.Findings.Count;
        var noun = count == 1 ? "item" : "items";
        _chatLog.Append(moved, OrchestratorMessageKind.Reissue,
            $"Auto-review sent \"{title}\" back to 2-ready ({count} unfinished {noun} from its own close-out).");

        EmitVerdictTimeline(moved.FolderPath, TimelineEventKinds.QualityLoopReopened,
            TimelineActors.QualityLoop,
            $"Reopened: completion gate found {count} unfinished {noun} in the run's own close-out.",
            BuildReopenDetails("completion-gate",
                CountPriorReissues(workspace, entry.Name, current.Id), findingsBlock));

        _statusSnapshot.RecordReissue();

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Reissue,
            Reason: gate.Reason,
            Prompt: "(completion-gate static scan)",
            Response: findingsBlock,
            FollowUp: followUp));
    }

    /// <summary>
    /// Record the post-core <see cref="PipelineCatalogue.OrchestratorReviewStepId"/>
    /// completeness-check row. This is the FIRST of the two "Orchestrator-Review"
    /// rows the Overview pipeline shows (the second is the final
    /// <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/> decision). No-op
    /// when the pipeline log is not wired (stand-alone test path) or no run record
    /// exists yet.
    /// </summary>
    private void RecordOrchestratorReviewStep(
        string jobFolderPath,
        PipelineStepStatus status,
        string verdict,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorReviewStepId,
            Kind = StepKind.Orchestrator,
            Status = status,
            StartedAt = now,
            CompletedAt = now,
            DurationMs = 0,
            Verdict = verdict,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
        });
    }

    /// <summary>
    /// Record the <see cref="PipelineCatalogue.OrchestratorDecisionStepId"/>
    /// step with the orchestrator's single aggregated final verdict. This is
    /// the step the Overview pipeline shows as the distinct "Auto-review
    /// decision" row beneath the parallel aspect rows - the catalogue defines
    /// it (<see cref="StepKind.Orchestrator"/>, depends on every aspect) but
    /// nothing recorded an outcome before, so it stayed <c>Pending</c> forever.
    /// Recorded on the job's post-move folder so the record lands where the
    /// pipeline-execution.json now lives. No-op when the pipeline log is not
    /// wired (stand-alone test path).
    /// </summary>
    private void RecordOrchestratorDecisionStep(
        string jobFolderPath,
        PipelineStepStatus status,
        string verdict,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.OrchestratorDecisionStepId,
            Kind = StepKind.Orchestrator,
            Status = status,
            StartedAt = now,
            CompletedAt = now,
            DurationMs = 0,
            Verdict = verdict,
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
    /// Drive the regression-radar post-step: resolve the per-project enable
    /// flag, run the deterministic spec-change analysis, and record the
    /// verdict + duration on <c>pipeline-execution.json</c>. Reporting only -
    /// returns void because the outcome never gates the move to review.
    /// Skips silently when neither a test analyzer seam nor the injected
    /// <see cref="RegressionRadarService"/> is wired (stand-alone test path).
    /// Never throws: a broken analysis falls through to a
    /// <see cref="PipelineStepStatus.Skipped"/> record rather than crash the
    /// orchestrator tick.
    /// </summary>
    private void RunRegressionRadarPostStep(WatchPathEntry entry, TaskInfo current)
    {
        var analyze = RegressionRadarAnalyzer
            ?? (_regressionRadar != null
                ? _regressionRadar.Analyze
                : (Func<string, string?, RegressionRadarResult>?)null);
        if (analyze == null) return;

        var settings = _projectSettings?.Get(entry.Name);
        if (!PipelineStepConfigResolver.IsEnabled(settings, PipelineCatalogue.RegressionRadarStepId))
        {
            RecordRegressionRadarStep(current.FolderPath, PipelineStepStatus.Skipped,
                durationMs: 0, verdictToken: "off", reason: "post-step disabled by config");
            return;
        }

        var sw = Stopwatch.StartNew();
        RegressionRadarResult result;
        try
        {
            result = analyze(current.Id, entry.Path);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "ReviewDecisionOrchestrator: regression-radar post-step threw for {Project}/{JobId}; treating as skipped",
                entry.Name, current.Id);
            RecordRegressionRadarStep(current.FolderPath, PipelineStepStatus.Skipped,
                sw.ElapsedMilliseconds, "error", ex.Message);
            return;
        }
        sw.Stop();

        var (status, verdict, reason) = MapRegressionRadarOutcome(result);
        RecordRegressionRadarStep(current.FolderPath, status, sw.ElapsedMilliseconds, verdict, reason);
    }

    private void RecordRegressionRadarStep(
        string jobFolderPath,
        PipelineStepStatus status,
        long durationMs,
        string verdictToken,
        string? reason)
    {
        if (_pipelineLog == null) return;
        var now = DateTime.UtcNow;
        _pipelineLog.RecordStep(jobFolderPath, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.RegressionRadarStepId,
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
    /// Pure mapping from a <see cref="RegressionRadarResult"/> to the
    /// recorded step status + verdict token + reason. The radar never blocks,
    /// so every successful analysis records as
    /// <see cref="PipelineStepStatus.Passed"/> with the spec-change category
    /// carried in the verdict token (clean / intended / at-risk / drift); an
    /// analysis that could not run (no repo / no commit range) records as
    /// <see cref="PipelineStepStatus.Skipped"/>. Static + internal so unit
    /// tests can assert the mapping without the orchestrator.
    /// </summary>
    internal static (PipelineStepStatus Status, string Verdict, string Reason) MapRegressionRadarOutcome(
        RegressionRadarResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error))
            return (PipelineStepStatus.Skipped, "n/a", result.Error!);

        if (result.TotalSpecChanges == 0)
            return (PipelineStepStatus.Passed, "clean", "No spec changes in the commit range");

        var counts = $"{result.TotalSpecChanges} spec change(s): "
            + $"{result.IntendedCount} intended, {result.AtRiskCount} at-risk, {result.DriftCount} drift";

        return result.OverallStatus switch
        {
            SpecChangeCategory.Drift  => (PipelineStepStatus.Passed, "drift", counts),
            SpecChangeCategory.AtRisk => (PipelineStepStatus.Passed, "at-risk", counts),
            _                         => (PipelineStepStatus.Passed, "intended", counts),
        };
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
            var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
            if (move.Status == MoveJobStatus.Success)
            {
                var movedFolderPath = move.NewFolderPath ?? current.FolderPath;
                var moved = current with { FolderPath = movedFolderPath, State = TaskStates.HumanReview };
                // Final verdict step: escalate (lint double-fail infinite-spin guard).
                RecordOrchestratorDecisionStep(movedFolderPath, PipelineStepStatus.Failed,
                    DecisionVerdictEscalate, reason);
                _chatLog.AppendSupervisor(moved, "escalate",
                    $"Lint-scss post-step failed twice in a row. Promoted to 5-human-review. Output:\n{result.Output}");
                // ADR-0049: timeline event on the original card, no wrapper card.
                EmitVerdictTimeline(movedFolderPath, TimelineEventKinds.OrchestratorEscalated,
                    TimelineActors.Orchestrator, reason,
                    BuildEscalateDetails("lint-scss-double-fail", reason,
                        CountPriorReissues(workspace, entry.Name, current.Id)));
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

        // Final verdict step: reissue (lint-scss gate failed once).
        RecordOrchestratorDecisionStep(moved2.FolderPath, PipelineStepStatus.Failed,
            DecisionVerdictReissue, LintScssReissueReasonPrefix + $"stylelint exit {result.ExitCode}");

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
            // Commits surfaced from the persisted chain (job.json) carry only a
            // file count - their +/- line stats are hardcoded 0 because
            // TaskCommitInfo never stored them. Re-derive the real +N/-M per
            // SHA from git so the aspect reviewer never sees "N files, +0/-0"
            // (read as "corrupted / no work") for a genuine multi-line commit.
            aggregate = EnrichLineStats(aggregate,
                sha => _git!.GetCommitStat(job.Id, watchPath, sha));
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
    /// Backfill genuine +added/-removed line stats onto aggregate commits that
    /// arrived without them. The aggregator folds persisted-chain and legacy
    /// auto-commit entries with <c>Added = Removed = 0</c> (the
    /// <see cref="TaskCommitInfo"/> chain only caches a file count), so a task
    /// whose run-window SHA range produced no commits - and therefore surfaces
    /// only via that chain - renders as "N files changed, +0/-0". An aspect
    /// reviewer reads that as corrupted / empty work and false-BLOCKs a real,
    /// tested change (ASS-770). For each such commit we ask <paramref name="statLookup"/>
    /// for the real per-SHA stat and recompute the totals.
    ///
    /// <para>
    /// Pure aside from the injected lookup so it can be pinned by a unit test
    /// with a fake stat source. A commit that already has line data (it came
    /// from the SHA-range path, which carries +/-) is left untouched; a lookup
    /// that returns all-zero (truly empty commit, or repo unresolvable) leaves
    /// the record as-is so we never invent numbers.
    /// </para>
    /// </summary>
    internal static TaskCommitsAggregate EnrichLineStats(
        TaskCommitsAggregate aggregate,
        Func<string, (int FilesChanged, int Added, int Removed)> statLookup)
    {
        if (aggregate.Count == 0) return aggregate;
        if (!aggregate.Commits.Any(c => c.Added == 0 && c.Removed == 0 && !string.IsNullOrWhiteSpace(c.Sha)))
            return aggregate;

        var enriched = new List<TaskCommitRecord>(aggregate.Commits.Count);
        foreach (var c in aggregate.Commits)
        {
            if (c.Added == 0 && c.Removed == 0 && !string.IsNullOrWhiteSpace(c.Sha))
            {
                (int FilesChanged, int Added, int Removed) stat;
                try { stat = statLookup(c.Sha); }
                catch { stat = (0, 0, 0); }
                if (stat.Added != 0 || stat.Removed != 0 || stat.FilesChanged != 0)
                {
                    enriched.Add(c with
                    {
                        Added = stat.Added,
                        Removed = stat.Removed,
                        FilesChanged = stat.FilesChanged > 0 ? stat.FilesChanged : c.FilesChanged
                    });
                    continue;
                }
            }
            enriched.Add(c);
        }

        return aggregate with
        {
            Commits = enriched,
            TotalAdded = enriched.Sum(x => x.Added),
            TotalRemoved = enriched.Sum(x => x.Removed),
            TotalFilesChanged = enriched.Sum(x => x.FilesChanged)
        };
    }

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

        // Defensive: commits exist and touch files, but no +/- line counts
        // could be derived (line stats were not cached on the attributed
        // commits and git could not re-derive them - e.g. an unresolvable
        // worktree). The file counts above are the authoritative signal that
        // work landed; spell that out so a reviewer does NOT read the zero
        // line totals as "no work", "empty", or "corrupted data" and BLOCK a
        // real change (ASS-770).
        if (aggregate.TotalAdded == 0 && aggregate.TotalRemoved == 0 && aggregate.TotalFilesChanged > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Note: per-line +/- counts could not be computed for these commits (line stats were not cached and git could not re-derive them). The file counts above are authoritative and confirm real changes - do NOT treat the zero line totals as missing, empty, or corrupted work.");
        }
        return sb.ToString();
    }

    private static int CountPriorReissues(string workspace, string project, string jobId)
    {
        return ReviewDecisionLog.ReadAll(workspace, project)
            .Count(r => r.JobId == jobId && r.Kind == ReviewDecisionKind.Reissue);
    }

    /// <summary>
    /// No-verdict guard (requirement 7): true when a 4-auto-review card has sat
    /// past the grace window with NO recorded orchestrator verdict at all. This
    /// rescues a card whose terminal sentinel was resolved on a prior tick but
    /// whose decision never landed - e.g. a lane move that silently failed to
    /// stick - so it is driven to a conclusion instead of hanging in
    /// 4-auto-review without a verdict.
    ///
    /// Two cheap preconditions, mtime first so the common (fresh) case never
    /// touches the decision journal: the CLI log must be older than the
    /// configurable grace window
    /// (<c>ReviewDecisionOrchestrator:NoVerdictGraceMinutes</c>, default 15),
    /// and the journal must hold no record for this job. The no-verdict
    /// precondition makes force-processing re-bill-safe: the first force-process
    /// appends a decision record, after which this returns false.
    /// </summary>
    private bool IsStaleWithoutVerdict(string workspace, string project, TaskInfo info, string logPath)
    {
        var graceMinutes = _configuration.GetValue("ReviewDecisionOrchestrator:NoVerdictGraceMinutes", 15);
        if (graceMinutes <= 0) return false;

        DateTime lastWriteUtc;
        try { lastWriteUtc = File.GetLastWriteTimeUtc(logPath); }
        catch { return false; }

        if (DateTime.UtcNow - lastWriteUtc < TimeSpan.FromMinutes(graceMinutes))
            return false;

        try
        {
            return !ReviewDecisionLog.ReadAll(workspace, project).Any(r => r.JobId == info.Id);
        }
        catch
        {
            return false;
        }
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

    private Task HandleEscalateAsync(
        string workspace,
        WatchPathEntry entry,
        PendingDecision pending,
        string prompt,
        string response,
        OrchestratorDecisionVerdict verdict,
        CancellationToken ct)
    {
        var current = _scanner.FindJob(pending.Job.Id, entry.Path) ?? pending.Job;

        // ADR-0049: the orchestrator could not decide this 4-auto-review
        // task unattended. It flips the *original* card to 5-human-review
        // (a genuine "a human must decide" case) and records one
        // orchestrator_escalated event on that card's timeline - the timeline
        // is the explanation. No sibling human-decision-needed-<slug> card is
        // spawned: the wrapper-card pattern (ASS-30) is the bug this ADR ends.
        var move = _stateMachine.MoveJob(current.Id, TaskStates.HumanReview, entry.Path);
        if (move.Status != MoveJobStatus.Success)
        {
            _logger.LogWarning(
                "ReviewDecisionOrchestrator: failed to move {JobId} to human-review after escalate: {Status} {Message}",
                current.Id, move.Status, move.Message);
            return Task.CompletedTask;
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

        ReviewDecisionLog.Append(workspace, new ReviewDecisionRecord(
            CreatedAt: DateTime.UtcNow,
            JobId: current.Id,
            Project: entry.Name,
            Kind: ReviewDecisionKind.Escalate,
            Reason: verdict.Reason,
            Prompt: prompt,
            Response: response,
            FollowUp: string.Empty));

        return Task.CompletedTask;
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

        // Provenance: the orchestrator (not a human) advanced this task
        // toward Completed. Stamp on the authoritative post-move path.
        ConcernTagWriter.MergeConcernTags(movedFolderPath, new[] { OrchestratorMovedTagId }, _logger);

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

    private IEnumerable<PendingDecision> EnumeratePending(string workspace, WatchPathEntry entry)
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

            if (needs == null && noop == null && blocked == null && done == null)
            {
                // Deterministic-completion contract (requirement 4): a job can
                // reach 4-auto-review with NO terminal sentinel at all - the
                // run exited with only heuristic "done"-ish prose, or the
                // terminal classifier force-routed an Unknown / committed-
                // partial outcome here. Such a run must never be silently
                // accepted as completed; it has to keep looping (reissue
                // demanding a sentinel) until the shared reissue budget is
                // spent, then escalate to human review.
                //
                // LacksTerminalSentinelInLatestRun separates that from a
                // sentinel that was already resolved on a prior tick (the
                // orchestrator wrote a follow-up line and nothing ran since):
                // the latter returns false and is left untouched, so we never
                // re-bill an already-handled card.
                if (ReviewDecisionParsing.LacksTerminalSentinelInLatestRun(log))
                {
                    yield return new PendingDecision(info, ReviewSignalKind.NoCompletionSignal, LineNumber: -1, Reason: null, NeedsInput: null);
                }
                else if (IsStaleWithoutVerdict(workspace, entry.Name, info, logPath))
                {
                    // No-verdict guard: a card that has sat in 4-auto-review past
                    // the grace window with no recorded orchestrator verdict at
                    // all (its sentinel was resolved on a prior tick but no
                    // decision ever landed - e.g. a move that silently failed to
                    // stick). Force-process it as a no-completion-signal so it is
                    // driven to a conclusion rather than hanging without a verdict.
                    // The no-verdict precondition makes this re-bill-safe: the
                    // first force-process appends a decision record, after which
                    // the guard no longer fires.
                    yield return new PendingDecision(info, ReviewSignalKind.NoCompletionSignal, LineNumber: -1, Reason: "no-verdict-timeout", NeedsInput: null);
                }
                continue;
            }

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
        lock (_callTimestampsLock)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            while (_callTimestamps.Count > 0 && _callTimestamps.Peek() < cutoff) _callTimestamps.Dequeue();
            return _callTimestamps.Count < maxPerHour;
        }
    }

    /// <summary>
    /// Charge one call against the per-hour rate budget. Thread-safe: the
    /// read-only review pool records calls from several review threads at once.
    /// </summary>
    private void RecordRateLimitedCall()
    {
        lock (_callTimestampsLock)
        {
            _callTimestamps.Enqueue(DateTime.UtcNow);
        }
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
    /// Provenance tag stamped on a task when the orchestrator advances it
    /// toward Completed via accept-as-done (both the multi-aspect and the
    /// single-verdict paths route to <c>5-human-review</c> per ADR-0025).
    /// Distinguishes an orchestrator-advanced task from one a human
    /// accepted by hand: human acceptance happens in the UI and never
    /// stamps this tag. Registered in the workspace tag registry (see
    /// <c>TagRegistryService</c>) so the kanban renders it with a label
    /// and colour. Unlike <see cref="ReissueTagId"/> this id uses the
    /// plain registry grammar (no colon).
    /// </summary>
    internal const string OrchestratorMovedTagId = "orchestrator-moved";

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
        Done,
        /// <summary>
        /// The run landed in 4-auto-review with no terminal sentinel at all
        /// (heuristic "done"-ish prose, an Unknown/committed-partial outcome
        /// force-routed here, etc.). The deterministic-completion contract
        /// forbids treating this as completed: the loop reissues demanding a
        /// sentinel until the shared reissue budget is spent, then escalates
        /// to human review. Detected via
        /// <see cref="ReviewDecisionParsing.LacksTerminalSentinelInLatestRun"/>.
        /// </summary>
        NoCompletionSignal
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
