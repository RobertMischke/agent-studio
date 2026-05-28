using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Quota;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Per-project runner: owns the lifecycle state for one watched workspace
/// (active job, mode, processing flag) and applies the side-effects of one
/// CLI invocation. The decision tree itself lives in <see cref="RunPlanner"/>;
/// this class is intentionally thin so the lifecycle and the planning concerns
/// can be read and tested independently.
/// </summary>
public class ProjectRunner
{
    private readonly ILogger _logger;
    private readonly JobScannerService _scanner;
    private readonly JobStateMachine _states;
    private readonly JobSessionLog _sessions;
    private readonly CliRouter _router;
    private readonly SummaryGenerationService _summaryService;
    private readonly RuntimePromptService _prompts;
    private readonly JobTransitionService _transitions;
    private readonly OrchestratorChatLog _chatLog;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly JobMutationService _mutations;
    private readonly OrchestratorRunner _orchestratorRunner;
    private readonly OrchestratorSessionStore _orchestratorSessions;
    private readonly ProjectSettingsService _projectSettings;
    private readonly QuotaService _quotaService;
    private readonly CliQuotaCapsService _quotaCaps;
    private readonly GitService _git;
    private readonly PickupFailureLog _pickupFailures;
    private readonly CrossSlugInfraCircuitBreaker _infraBreaker;
    private readonly AgentMessageBusBridge? _bus;
    private readonly OrchestratorApi.Services.TaskAccess.ITaskAccess _taskAccess;
    private string _mode = "manual";
    // The human-readable reason recorded the last time _mode changed, plus
    // when the change happened. Surfaced via ProjectRunnerStatus so the
    // board can render a different pill (PAUSED vs MANUAL) when the mode
    // was flipped by a circuit-breaker or supervisor rather than by the
    // operator.
    private string? _modeReason;
    private DateTime? _modeChangedAt;
    private string? _modeSource;
    private string? _activeJobId;
    private string? _activeCliType;
    private bool _processing;
    // Tracks the last run's intent and follow-up so OnCliFinished can apply
    // the post-run policy without re-deriving them from job state.
    private RunIntent _activeIntent;
    private string? _activeFollowup;
    private RunPlan? _activePlan;
    private int _activeReissueAttempt;
    // Suppression state for repeated meta messages. When the same heuristic
    // verdict fires twice in a row in Recovery, we skip the second meta
    // message so the chat does not pile orchestrator notes on a stuck run.
    private string? _lastMetaSignature;

    // Per-job stuck-loop counters. The auto-mode loop (agent emits
    // NEEDS_INPUT -> orchestrator decides -> reply re-issued as Continue)
    // can in theory run forever if the agent keeps asking. We track
    // iterations + cumulative orchestrator tokens per job and let
    // StuckLoopGuard decide when to break the loop. State lives in
    // memory only; a backend restart resets it (a restart is itself a
    // recovery boundary, so that's the desired behavior).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StuckLoopState> _stuckLoops = new();
    private StuckLoopBudget _stuckLoopBudget = StuckLoopBudget.Default;

    // Consecutive auto-pickup failures. Auto-mode flips back to manual when
    // this hits the threshold so a single bad event (mid-flight kill, rotated
    // session id, watchdog regression) cannot cascade through every queued
    // job. Reset on any auto-issued run that reaches Review.
    private int _consecutiveAutoFailureCount;

    // Per-job latch: have we already issued the sentinel-detected stop for
    // this run? claude-code can emit multiple TurnCompleted frames in a
    // single run if the model produces several result-shaped responses; we
    // only want to kill once on the first sentinel-bearing one. Cleared on
    // ProcessExited / Killed in OnRunEventReceived.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sentinelStopRequested = new();
    internal const int AutoFailureHaltThreshold = 3;
    // Job ids of the recent auto-failures, kept so the halt message can name
    // the offenders without re-scanning.
    private readonly Queue<string> _recentAutoFailureJobIds = new();

    // Per-job consecutive capture-fail counter. A capture-fail run is one
    // that exited without claude/codex/gemini emitting a usable session id
    // - the prior run's UUID is dead, can't be resumed, and we have no new
    // UUID to chain to. The recovery-marker write that follows is the
    // semantic fix; this counter is the secondary stop-gap so a structural
    // failure (planner re-reads stale cache, scanner returns a snapshot
    // taken before the recovery write completed, etc.) cannot loop forever.
    private string? _consecutiveCaptureFailJobId;
    private int _consecutiveCaptureFailCount;
    internal const int CaptureFailHaltThreshold = 3;

    // Continuous decision review: while a job sits in 3-progress, we scan
    // its live output buffer every tick for an unresolved interruptive
    // sentinel ([[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]]). The latch is
    // cleared when the active job changes, when the scan returns null
    // (resolved or no longer present), or on backend restart. Surfaced
    // via GET /api/runner/{project}/pending-decisions so the project
    // view can render a prominent banner. See ADR-0027 and
    // docs/research/orchestrator-decision-protocol-2026-05.md.
    private PendingDecisionEntry? _activePendingDecision;
    private readonly object _pendingDecisionLock = new();

    public string ProjectName { get; }
    public WatchPathEntry Entry { get; }

    public event Action<ProjectRunnerStatus>? OnStatusChanged;
    /// <summary>
    /// Raised whenever the mode changes through any path (explicit
    /// <see cref="SetMode"/> or implicit auto-single → manual revert).
    /// Wired by <see cref="TaskRunnerService"/> to persist the new mode.
    /// Restoration via <see cref="RestoreMode"/> does NOT fire this event.
    /// </summary>
    public event Action<string>? OnModePersist;

    public ProjectRunner(
        string projectName,
        WatchPathEntry entry,
        ILogger logger,
        JobScannerService scanner,
        JobStateMachine states,
        JobSessionLog sessions,
        CliRouter router,
        SummaryGenerationService summaryService,
        RuntimePromptService prompts,
        JobTransitionService transitions,
        OrchestratorChatLog chatLog,
        JobMutationService mutations,
        OrchestratorLog orchestratorLog,
        OrchestratorRunner orchestratorRunner,
        OrchestratorSessionStore orchestratorSessions,
        ProjectSettingsService projectSettings,
        QuotaService quotaService,
        CliQuotaCapsService quotaCaps,
        GitService git,
        PickupFailureLog pickupFailures,
        CrossSlugInfraCircuitBreaker infraBreaker,
        OrchestratorApi.Services.TaskAccess.ITaskAccess taskAccess,
        AgentMessageBusBridge? bus = null)
    {
        ProjectName = projectName;
        Entry = entry;
        _logger = logger;
        _scanner = scanner;
        _states = states;
        _sessions = sessions;
        _router = router;
        _summaryService = summaryService;
        _prompts = prompts;
        _transitions = transitions;
        _chatLog = chatLog;
        _mutations = mutations;
        _orchestratorLog = orchestratorLog;
        _orchestratorRunner = orchestratorRunner;
        _orchestratorSessions = orchestratorSessions;
        _projectSettings = projectSettings;
        _quotaService = quotaService;
        _quotaCaps = quotaCaps;
        _git = git;
        _pickupFailures = pickupFailures;
        _infraBreaker = infraBreaker;
        _taskAccess = taskAccess;
        _bus = bus;

        // Listen across all CLI backends for completion of the active job.
        _router.OnFinished += (cliType, jobKey, exec) => OnCliFinished(cliType, jobKey, exec);
        // ADR-0013: typed events drive the phase-aware watchdog. Each
        // adapter advances the phase as its native protocol moves; the
        // runner stores per-job phase + last-event timestamp and uses
        // PhaseAwareWatchdog.DecideState below.
        _router.OnRunEvent += (_, jobKey, evt) => OnRunEventReceived(jobKey, evt);
    }

    /// <summary>
    /// Mutate the runner's auto-pickup mode and persist it. <paramref name="reason"/>
    /// is the human-readable cause that lands in the structured log so the
    /// "why did the runner flip" question is answerable from the day's log
    /// alone (F16). Default reason names the API toggle, since that is the
    /// only path that calls <c>SetMode</c> without supplying its own
    /// motivation.
    /// </summary>
    public void SetMode(string mode, string? reason = null)
    {
        var fromMode = _mode;
        _mode = mode;
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "api-toggle" : reason!;
        _modeReason = effectiveReason;
        _modeChangedAt = DateTime.UtcNow;
        _modeSource = ClassifyModeSource(effectiveReason);
        _logger.LogInformation(
            "Runner '{Project}' mode '{From}' -> '{To}' because '{Reason}' (source={Source})",
            ProjectName, fromMode, mode, effectiveReason, _modeSource);
        try { OnModePersist?.Invoke(mode); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnModePersist subscriber threw for {Project}", ProjectName); }
        NotifyStatus();
    }

    /// <summary>
    /// Coarse classification of where a mode change came from so the board
    /// can render circuit-breaker-induced pauses differently from operator
    /// toggles. Kept as a static string lookup against the reason text the
    /// caller passes to <see cref="SetMode"/>; new circuit-breaker callsites
    /// only need to keep their reason text starting with "circuit-breaker"
    /// or containing "circuit-breaker:" to be recognised here.
    /// </summary>
    private static string ClassifyModeSource(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return "system";
        if (reason.Contains("circuit-breaker", StringComparison.OrdinalIgnoreCase))
            return "circuit-breaker";
        if (reason.StartsWith("supervisor", StringComparison.OrdinalIgnoreCase))
            return "supervisor";
        if (reason.StartsWith("api", StringComparison.OrdinalIgnoreCase))
            return "user";
        return "system";
    }

    /// <summary>
    /// Re-applies a previously saved mode at startup without re-firing the persist
    /// hook (the value already came from the store). Status is broadcast so any
    /// already-connected clients see the restored mode.
    /// </summary>
    public void RestoreMode(string mode)
    {
        _mode = mode;
        NotifyStatus();
    }

    /// <summary>
    /// Cheap read-only check against the latest cached quota snapshot for
    /// <paramref name="cliType"/>. Returns "not blocked" when no snapshot is
    /// cached yet (the user hasn't loaded any quota in this session) - we
    /// prefer "let the run start" over "stall the queue waiting for a probe".
    /// </summary>
    public CapEvaluation EvaluateQuotaCap(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return CapEvaluation.NotBlocked;
        var snap = _quotaService.GetCachedFor(cliType);
        return _quotaCaps.Evaluate(snap);
    }

    /// <summary>
    /// If a job is currently running on this project and its CLI has gone
    /// past a configured cap, request a stop. Returns the cap evaluation that
    /// triggered the stop (or "not blocked" when nothing was stopped) so the
    /// caller can produce a single chat note instead of one per tick.
    /// </summary>
    public CapEvaluation EnforceQuotaCapsOnActiveJob(RunStopReason reason = RunStopReason.UserStop)
    {
        var jobId = _activeJobId;
        var cliType = _activeCliType;
        if (jobId == null || string.IsNullOrWhiteSpace(cliType)) return CapEvaluation.NotBlocked;
        var ev = EvaluateQuotaCap(cliType);
        if (!ev.Blocked) return CapEvaluation.NotBlocked;

        _logger.LogWarning(
            "[taskboard] stopping active job {JobId} on {Project}: quota cap exceeded ({Reason})",
            jobId, ProjectName, ev.DescribeReason());

        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info != null)
            {
                _router.Get(info.CliType).Stop(info.JobKey, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "EnforceQuotaCapsOnActiveJob: stop failed for {JobId} on {Project}",
                jobId, ProjectName);
        }
        return ev;
    }

    public ProjectRunnerStatus GetStatus()
    {
        var queued = GetQueuedJobIds();
        var activeJobKey = GetActiveJobKey();
        CliExecution? activeExec = null;
        if (activeJobKey != null && _activeCliType != null)
        {
            activeExec = _router.Get(_activeCliType).GetExecution(activeJobKey);
        }
        return new ProjectRunnerStatus
        {
            ProjectName = ProjectName,
            Mode = _mode,
            ActiveJobId = _activeJobId,
            ActiveExecution = activeExec,
            QueuedJobIds = queued,
            ModeReason = _modeReason,
            ModeChangedAt = _modeChangedAt,
            ModeSource = _modeSource
        };
    }

    public async Task TickAsync(CancellationToken ct)
    {
        // Watchdog ticks regardless of runner mode: even when auto-pickup is
        // disabled, an active CLI on this project still needs to be watched
        // for hangs. Cheap (one timestamp arithmetic per active job).
        TickWatchdog();

        // Continuous decision review (ADR-0027): scan the active job's live
        // output buffer for an unresolved [[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]]
        // sentinel so the project banner can stand out the moment the agent
        // emits one, not only after the run ends.
        TickPendingDecision();

        // Defensive reconciliation: if the in-memory active-job latch is
        // pointing at a job whose folder is no longer in 3-progress, release
        // it now so the rest of this tick (and future pickup ticks) are not
        // wedged. Covers external-script moves and the boot-time stuck-folder
        // sweep where no API event fired to clear us synchronously.
        ReconcileActiveJobAgainstDisk();

        if (_mode is "manual" or "paused") return;
        if (_processing || _activeJobId != null) return;

        // Check if there's a running process for this project on any CLI
        if (_router.All.Any(c => c.IsRunningForProject(Entry.RootPath))) return;

        // Pickup gating ends here. The picker below considers 3-progress and
        // 2-ready only; jobs sitting in 1-preparation, 1a-orchestrator-prep,
        // 1b-needs-human-review, 4-auto-review, or 5-human-review do NOT
        // block this tick. Those lanes are owned by their own background
        // services (OrchestratorPrepHostedService, IntakeHostedService,
        // ReviewDecisionOrchestrator) and run in parallel with the runner.
        // ADR-0001 is preserved by the active-job latch above (one coding
        // CLI per project at a time); ADR-0026 was clarified to make the
        // parallelism explicit. See ParallelLanesPickupTests.

        // Strict progress-first pickup: walk every 3-progress folder oldest-first
        // by mtime BEFORE considering 2-ready. A folder qualifies for resume
        // regardless of whether it carries a captured session id or even a
        // cli-output.log: the "no log" case means the previous attempt died
        // before the CLI streamed anything, which is the most-restartable
        // case, not the most-skippable. Folders that have failed silently
        // for the configured retry budget get dead-lettered into 7-archive
        // here so the iteration drains and falls through to the ready queue.
        var nextJob = TryPickProgressJobOrDeadLetter() ?? GetNextReadyJob();
        if (nextJob == null)
        {
            if (_mode == "auto-single")
            {
                // Route through SetMode so the revert is persisted - otherwise a
                // backend restart right after this would resurrect "auto-single"
                // and immediately pick up another job.
                SetMode("manual", "auto-single revert: pickup queue empty");
            }
            return;
        }

        await RunCliAsync(nextJob.Id, RunIntent.AutoPickup, followupPrompt: null, reissueAttempt: 0, mode: null, ct);
    }

    public Task<RunOutcome> StartJobManualAsync(string jobId, CancellationToken ct)
        => RunCliAsync(jobId, RunIntent.ManualStart, followupPrompt: null, reissueAttempt: 0, mode: null, ct);

    /// <summary>
    /// Sends a follow-up prompt into the CLI session that was originally created
    /// for this job (via <c>--resume</c>). When no compatible session is on
    /// record, the planner falls back to <b>recovery mode</b>: a fresh CLI run
    /// instructed to reconstruct context from the job folder. Moves the job back
    /// to <c>3-progress</c> if it sits in <c>4-review</c> or <c>5-completed</c>.
    /// </summary>
    public Task<RunOutcome> ContinueJobAsync(string jobId, string followupPrompt, string? mode, CancellationToken ct)
        => RunCliAsync(jobId, RunIntent.UserContinue, followupPrompt, reissueAttempt: 0, mode: mode, ct);

    /// <summary>
    /// Single entry point for spawning the CLI for a job. <see cref="RunPlanner.PlanRun"/>
    /// owns the full decision tree (resume vs recovery vs fresh, prompt choice,
    /// session-event shape, state moves); this method only applies the side
    /// effects the plan describes. Both the start endpoints and the continue
    /// endpoint route through here so a fix in one path can never miss its
    /// sibling - that divergence is the bug class this design exists to prevent.
    /// </summary>
    private async Task<RunOutcome> RunCliAsync(
        string jobId, RunIntent intent, string? followupPrompt, int reissueAttempt, string? mode, CancellationToken ct)
    {
        if (_activeJobId != null)
        {
            if (intent == RunIntent.ManualStart)
                _logger.LogWarning("Runner '{Project}' already has active job {JobId}", ProjectName, _activeJobId);
            // Look up the active job's title for the queued response so the
            // TaskRunnerService can shape a friendly meta message without
            // re-scanning. Best-effort; null title is fine.
            string? activeTitle = null;
            try { activeTitle = _scanner.FindJob(_activeJobId!, Entry.Path)?.Title; } catch { }
            return RunOutcome.Reject(new RunRejection(
                Reason: RunRejectReason.ProjectBusy,
                Message: $"Runner '{ProjectName}' is already executing job '{_activeJobId}'",
                BusyJobId: _activeJobId,
                BusyJobTitle: activeTitle));
        }

        _processing = true;
        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info == null) return RunOutcome.Reject(new RunRejection(RunRejectReason.JobNotFound, "Job not found"));

            // Quota cap gate: refuse to start when the CLI is past its
            // configured per-window cap. Auto-pickup will retry on the next
            // tick (and the next, ...) until the user lifts the cap or the
            // window resets - this is intentional: the user wants the job
            // queued, not failed.
            var capBlock = EvaluateQuotaCap(info.CliType);
            if (capBlock.Blocked)
            {
                _logger.LogInformation(
                    "[taskboard] {Intent} for job {JobId} blocked by quota cap: {Reason}",
                    intent, jobId, capBlock.DescribeReason());
                return RunOutcome.Reject(new RunRejection(
                    Reason: RunRejectReason.QuotaCapExceeded,
                    Message: $"Quota cap exceeded: {capBlock.DescribeReason()}"));
            }

            // Auto-pickup consumes a saved pending-intent if there is one,
            // turning what would have been a fresh-start run into a
            // UserContinue with the saved prompt + mode. This is the runtime
            // half of the busy-project queue: TaskRunnerService writes the
            // intent + promotes to 2-ready; the auto-pickup here picks the
            // job and runs the saved continue.
            if (intent == RunIntent.AutoPickup && info.PendingIntent != null)
            {
                var stashed = _mutations.ReadAndStashPendingIntent(info.FolderPath);
                if (stashed != null && !string.IsNullOrWhiteSpace(stashed.Prompt))
                {
                    _logger.LogInformation(
                        "[taskboard] auto-pickup of {JobId} consuming saved {Mode} intent ({Chars} chars)",
                        jobId, stashed.Mode, stashed.Prompt.Length);
                    intent = RunIntent.UserContinue;
                    followupPrompt = stashed.Prompt;
                    mode = stashed.Mode;
                }
            }

            var cli = GetCliFor(info);
            var initialState = info.State;
            var promptPath = Path.Combine(info.FolderPath, "prompt.md");
            var jobFolder = info.FolderPath;

            var plan = RunPlanner.PlanRun(
                intent,
                initialState,
                info.SessionName,
                cli.CliType,
                cli.IsCompatibleSessionName,
                jobId,
                promptPath,
                jobFolder,
                followupPrompt,
                info.SessionChain,
                continueMode: mode);

            if (plan.MoveJobToProgress && info.State != JobStates.Progress)
            {
                _states.MoveJob(jobId, JobStates.Progress, Entry.Path);
                info = _scanner.FindJob(jobId, Entry.Path) ?? info;
            }

            var prompt = RenderPrompt(plan, info);

            _activeJobId = jobId;
            _activeIntent = intent;
            _activeFollowup = followupPrompt;
            _activePlan = plan;
            _activeReissueAttempt = reissueAttempt;
            NotifyStatus();

            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));

            // Diagnostic logs - surface the planner's decision in one place so
            // operators reading the log can tell which branch fired without
            // grepping for old per-method messages.
            _logger.LogInformation(
                "[taskboard] {Intent} for job {JobId} on {Cli}: kind={Kind} resume={Resume} session={Session} reason={Reason}",
                intent, jobId, cli.CliType, plan.EventKind, plan.ResumeFlag,
                plan.SessionToResume ?? "<none>", plan.EventReason ?? "<none>");
            _logger.LogInformation("[taskboard] using working directory {Path}", Entry.RootPath);

            if (plan.ClearStaleSessionName)
                _sessions.SetJobSessionName(jobId, null, Entry.Path);
            if (plan.PersistSessionName != null)
                _sessions.SetJobSessionName(jobId, plan.PersistSessionName, Entry.Path);
            if (plan.MarkSessionChainRecovery)
                _sessions.MarkSessionChainRecovery(jobId, Entry.Path);
            if (plan.WriteCutMarker)
                AppendSessionCutMarkerToCliLog(info, plan.CutMarkerReason ?? "session lost");

            // Continue-routed-to-Recovery: the user clicked Send (or chose
            // Continue / Steer / Extend / NewTask), but no resumable session
            // is on record. The cut marker tells the activity log a chain
            // break happened; this orchestrator note explains, in user-
            // language, why their conversation context did not carry over.
            if (intent == RunIntent.UserContinue
                && string.Equals(plan.EventKind, "recovery", StringComparison.OrdinalIgnoreCase))
            {
                var modeLabel = ContinueModes.Normalize(mode);
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[fallback] No {cli.CliType} session on record (mode: {modeLabel}); rebuilding context from job folder.");
            }

            // Capture the project's HEAD SHA right before the CLI starts.
            // Combined with the post-run capture in OnCliFinishedAsync, this
            // gives us the deterministic SHA range the per-run commits
            // endpoint uses ("commits made during this run" = git rev-list
            // HeadShaBefore..HeadShaAfter). Best-effort: a missing repo or
            // a git failure leaves the SHAs null and we fall back to the
            // wall-clock window. See docs/design-principles.md for why we
            // treat the software-side change set as a first-class signal.
            var headShaBefore = SafeGetHeadSha(jobId);

            _sessions.AppendSessionEvent(jobId, new SessionEvent
            {
                Ts = DateTime.UtcNow,
                Kind = plan.EventKind,
                Cli = cli.CliType,
                InputSessionId = plan.EventInputSessionId,
                CapturedSessionId = null,
                Resumed = plan.ResumeFlag,
                Reason = plan.EventReason,
                HeadShaBefore = headShaBefore
            }, Entry.Path);

            _activeCliType = cli.CliType;
            var (execution, cliError) = await cli.StartAsync(
                jobId, GetJobKey(jobId), prompt, Entry.RootPath,
                plan.SessionToResume, plan.ResumeFlag, info.Model, info.FolderPath, ct);

            if (execution == null)
            {
                _activeJobId = null;
                _activeCliType = null;
                NotifyStatus();
                // Roll back the consumed pending-intent on spawn failure so
                // the next auto-pickup retries instead of losing the user's
                // input.
                _mutations.RollbackStashedPendingIntent(info.FolderPath);
                // A spawn failure on autopickup is a silent attempt for
                // dead-letter purposes: the CLI never produced output. The
                // OnCliFinished path that normally records this never fires
                // because there is no execution.
                if (intent == RunIntent.AutoPickup && info.State == JobStates.Progress)
                {
                    RecordPickupAttemptResult(
                        slug: jobId,
                        outputLines: 0,
                        durationSeconds: 0.0,
                        executionStatus: "spawn-failed");
                }
                return RunOutcome.Reject(new RunRejection(
                    Reason: RunRejectReason.CliUnavailable,
                    Message: cliError ?? $"Failed to start {cli.CliType} CLI process"));
            }

            // Spawn succeeded; drop the stashed intent (we've consumed it).
            _mutations.DiscardStashedPendingIntent(info.FolderPath);

            // Mirror run-start onto the bus. Existing canonical signals
            // (session-events.jsonl + cli-output.log "[taskboard] Started ..."
            // marker) stay; the bus message is a typed projection so the
            // project screen does not need to scan log text for run boundaries.
            try { _ = _bus?.EmitRunStartedAsync(info, cli.CliType, execution.StartedAt, plan.SessionToResume, intent.ToString()); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of run-start failed for {JobId}", jobId); }

            return RunOutcome.Started(execution);
        }
        finally
        {
            _processing = false;
        }
    }

    /// <summary>
    /// Per-tick watchdog pass. Walks the active CLI run for this project,
    /// computes silence + age, calls <see cref="Watchdog.DecideState"/>,
    /// and on a state transition either posts a chat meta message
    /// (Quiet -> Suspicious, Suspicious -> Hung, etc.) or kills the
    /// process tree (when transitioning into Hung). Same-state ticks are
    /// silent so the chat does not pile up identical notes.
    /// </summary>
    private void TickWatchdog()
    {
        var jobId = _activeJobId;
        var cliType = _activeCliType;
        if (jobId == null || cliType == null) return;

        ICliExecutionService cli;
        try { cli = _router.Get(cliType); }
        catch { return; }

        var jobKey = JobIdentity.CreateKey(Entry.Path, jobId);
        var exec = cli.GetExecution(jobKey);
        if (exec == null || !string.Equals(exec.Status, "running", StringComparison.OrdinalIgnoreCase))
            return;

        var lastStreamed = cli.GetLastStreamedAt(jobKey) ?? exec.StartedAt;
        var now = DateTime.UtcNow;
        var age = (now - exec.StartedAt).TotalSeconds;

        // ADR-0013: prefer the typed-event phase tracker when available.
        // It advances on actual protocol events, so silence here means
        // "no protocol activity in this phase", not "no stdout byte" -
        // a stronger signal that surfaces via the per-phase budgets.
        // Fall back to the legacy silence-only signal for CLIs that do
        // not yet emit run events.
        WatchdogState next;
        double silence;
        RunPhase? phase = null;
        if (_phaseByJob.TryGetValue(jobKey, out var phaseSnap))
        {
            phase = phaseSnap.Phase;
            silence = (now - phaseSnap.LastActivityAt).TotalSeconds;
            next = PhaseAwareWatchdog.DecideState(silence, age, phaseSnap.Phase, _watchdogConfig);
        }
        else
        {
            silence = (now - lastStreamed).TotalSeconds;
            next = Watchdog.DecideState(silence, age, _watchdogConfig);
        }

        var prev = cli.GetWatchdogState(jobKey);
        if (!Watchdog.ShouldAnnounce(prev, next)) return;

        cli.SetWatchdogState(jobKey, next);

        var info = _scanner.FindJob(jobId, Entry.Path);
        if (info == null) return;

        var hungAtSeconds = phase is null
            ? _watchdogConfig.HungSeconds
            : PhaseBudget.For(phase.Value).HungSeconds;
        var phaseTag = phase is null ? "" : $" [{PhaseAwareWatchdog.FormatBudgetReason(phase.Value, silence)}]";
        var title = string.IsNullOrWhiteSpace(info.Title) ? info.Id : info.Title;
        var cliLabel = string.IsNullOrWhiteSpace(info.CliType) ? cliType : info.CliType;
        switch (next)
        {
            case WatchdogState.Quiet:
                // Soft first warning. Operator-friendly copy with task title +
                // CLI so the notification stands on its own without context.
                _chatLog.Append(info, OrchestratorMessageKind.WatchdogWarning,
                    $"\"{title}\" ({cliLabel}): no output for {silence:F0}s yet. No action needed unless this repeats.{phaseTag}");
                break;
            case WatchdogState.Suspicious:
                _chatLog.Append(info, OrchestratorMessageKind.WatchdogWarning,
                    $"\"{title}\" ({cliLabel}): no output for {silence:F0}s. Run will be auto-cancelled at {hungAtSeconds:F0}s. No action needed unless this repeats.{phaseTag}");
                break;
            case WatchdogState.Hung:
                _chatLog.Append(info, OrchestratorMessageKind.WatchdogTimeout,
                    $"\"{title}\" ({cliLabel}): auto-cancelled after {silence:F0}s of silence. The run will finalize as failed.{phaseTag}");
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Action,
                    Topic = OrchestratorLogTopics.Watchdog,
                    JobId = jobId,
                    Summary = $"Watchdog auto-cancelled \"{info.Title}\" after {silence:F0}s of silence.",
                    Reasoning = $"No streamed activity for {silence:F0}s (run age {age:F0}s){phaseTag}. Process tree terminated; the run finalizes as failed."
                });
                try { cli.Stop(jobKey, RunStopReason.Watchdog); }
                catch (Exception ex) { _logger.LogWarning(ex, "Watchdog kill failed for {JobId}", jobId); }
                break;
            case WatchdogState.Healthy:
                if (prev != WatchdogState.Healthy)
                {
                    _chatLog.Append(info, OrchestratorMessageKind.WatchdogWarning,
                        $"\"{title}\" ({cliLabel}): streaming output again.");
                }
                break;
        }
    }

    /// <summary>
    /// Watchdog thresholds for this runner. Loaded once from configuration
    /// when the runner is constructed; reuse across ticks. Defaults applied
    /// when nothing is configured.
    /// </summary>
    private WatchdogConfig _watchdogConfig = WatchdogConfig.Default;

    /// <summary>
    /// Per-jobKey phase tracker. Populated as adapters emit
    /// <see cref="CliRunEvent"/> via the router. Cleared on
    /// <see cref="CliRunEvent.ProcessExited"/> / <see cref="CliRunEvent.Killed"/>.
    /// When a jobKey is missing from this map, the runner falls back to
    /// the silence-only watchdog (<see cref="Watchdog.DecideState"/>).
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RunPhaseSnapshot> _phaseByJob = new();

    /// <summary>Last seen phase + UTC of last activity-classified event.</summary>
    private sealed record RunPhaseSnapshot(RunPhase Phase, DateTime LastActivityAt);

    /// <summary>Updates per-job phase + activity clock from a typed event.</summary>
    private void OnRunEventReceived(string jobKey, CliRunEvent evt)
    {
        var prev = _phaseByJob.TryGetValue(jobKey, out var existing) ? existing : new RunPhaseSnapshot(RunPhase.Spawning, DateTime.UtcNow);
        var nextPhase = RunPhaseTransitions.Apply(prev.Phase, evt);
        var lastActivity = RunPhaseTransitions.IsActivitySignal(evt) ? DateTime.UtcNow : prev.LastActivityAt;
        _phaseByJob[jobKey] = new RunPhaseSnapshot(nextPhase, lastActivity);

        // Surface tool-call boundaries to disk so a post-mortem of a
        // watchdog kill can answer "what was the last tool the agent
        // started, with what arguments, did the result come back?".
        // The legacy text log already contains this implicitly; the
        // structured file makes it grep-friendly without parsing.
        try { AppendToolCallLog(jobKey, evt); }
        catch (Exception ex) { _logger.LogDebug(ex, "tool-calls.jsonl append failed for {JobKey}", jobKey); }

        // Sentinel-on-TurnCompleted gate. claude-code in stream-json mode
        // emits a `result:success` frame (mapped to TurnCompleted) and can
        // then linger indefinitely without exiting; AgentOutcomeAnalyzer
        // only fires from OnCliFinished, which only fires on OS exit, so a
        // job whose agent already wrote [[TASK_DONE]] hangs forever in
        // 3-progress until the watchdog or operator intervenes. Detect the
        // sentinel here and kill the lingering process so the existing exit
        // handler runs the analyzer + policy.
        if (evt is CliRunEvent.TurnCompleted && _sentinelStopRequested.TryAdd(jobKey, 1))
        {
            try { TryStopOnSentinel(jobKey); }
            catch (Exception ex) { _logger.LogWarning(ex, "Sentinel-stop check failed for {JobKey}", jobKey); }
        }

        // Mirror per-turn token usage emitted by the coding-agent CLI onto
        // the agent message bus. Without this, the Codex streaming path is
        // invisible to BusAggregationCache, the project token summary, and
        // the workspace quota strip - they read kind:token-usage messages,
        // and a Codex run that never produces an orchestrator decision turn
        // would otherwise leave the workspace timeline blank for the run's
        // own spend. One emit per turn.completed frame.
        if (evt is CliRunEvent.TurnCompleted)
        {
            try { MirrorAgentTurnUsageToBus(jobKey); }
            catch (Exception ex) { _logger.LogDebug(ex, "Per-turn bus mirror failed for {JobKey}", jobKey); }
        }

        // Clean up on terminal events so a later run with the same key
        // does not inherit stale phase state.
        if (evt is CliRunEvent.ProcessExited or CliRunEvent.Killed)
        {
            _phaseByJob.TryRemove(jobKey, out _);
            _sentinelStopRequested.TryRemove(jobKey, out _);
        }
    }

    /// <summary>
    /// Scan the buffered CLI output for a typed sentinel; if one is present
    /// AND the run's owning project is this one (active job match), ask the
    /// CLI service to kill the still-alive process tree. The kill flows back
    /// through the existing ProcessExited path, which fires
    /// <see cref="OnCliFinished"/> and lets the analyzer + policy do their
    /// usual work. Marker reason <see cref="RunStopReason.SentinelDetected"/>
    /// keeps the run-status classifier from labelling the kill as "stopped".
    /// </summary>
    private void TryStopOnSentinel(string jobKey)
    {
        // Only act for the run we're currently tracking. A stale TurnCompleted
        // from a previous run should never reach here, but guard anyway.
        if (GetActiveJobKey() != jobKey) return;
        var cliType = _activeCliType;
        if (string.IsNullOrEmpty(cliType)) return;

        var cli = _router.Get(cliType);
        var snapshot = cli.GetOutput(jobKey);
        if (snapshot == null || snapshot.Count == 0) return;

        // Use the same regex the analyzer uses, so detection here matches
        // the post-run path exactly. SentinelRegex is the published surface.
        var found = false;
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            if (AgentOutcomeAnalyzer.SentinelRegex.IsMatch(snapshot[i].Text ?? string.Empty))
            {
                found = true;
                break;
            }
        }
        if (!found) return;

        _logger.LogInformation(
            "TurnCompleted with sentinel for {JobKey}; killing lingering {Cli} process so OnCliFinished can run.",
            jobKey, cliType);
        cli.Stop(jobKey, RunStopReason.SentinelDetected);
    }

    /// <summary>
    /// Mirror the coding-agent CLI's most recent <c>turn.completed</c> usage
    /// snapshot onto the agent message bus as a <c>kind:token-usage</c>
    /// message attributed to <c>agent:&lt;cli&gt;</c>. The CLI driver parses
    /// the frame (Codex uses <see cref="CodexUsageParser"/>) and stashes a
    /// <see cref="OrchestratorApi.Services.Bus.ParsedTurnUsage"/>; the runner
    /// reads it here when the matching typed event arrives.
    /// <para>
    /// Without this, <c>BusAggregationCache.OnAppended</c> never sees the
    /// coding agent's input/output/cached tokens. The project token summary
    /// and workspace quota strip read off the bus, so a Codex run that
    /// completes without an orchestrator decision turn shows zero spend even
    /// though the CLI reported usage in its <c>turn.completed</c> frame.
    /// </para>
    /// </summary>
    private void MirrorAgentTurnUsageToBus(string jobKey)
    {
        if (_bus == null) return;
        if (GetActiveJobKey() != jobKey) return;
        var jobId = _activeJobId;
        if (string.IsNullOrEmpty(jobId)) return;
        var cliType = _activeCliType;
        if (string.IsNullOrEmpty(cliType)) return;

        var cli = _router.Get(cliType);
        // Today only the Codex driver captures the rich parsed-usage stash.
        // Claude / Gemini will be added behind the same interface as their
        // adapters move onto the shared parser; until then the dispatch is
        // explicit so the call is a clear no-op for those CLIs.
        if (cli is not CodexCliService codex) return;

        var snapshot = codex.GetLastParsedTurnUsage(jobKey);
        if (snapshot is null) return;
        var (usage, observedAt, startedAt) = snapshot.Value;

        var latency = new AgentMessageLatency(
            RequestedAt: startedAt,
            CompletedAt: observedAt,
            TotalMs: (long)Math.Max(0, (observedAt - startedAt).TotalMilliseconds));

        var runId = AgentMessageBusBridge.DeriveRunId(jobId!, startedAt);
        var participantId = AgentMessageBusBridge.ParticipantForCli(cliType);
        var topic = $"{cliType!.ToLowerInvariant()}-turn";

        _ = _bus.EmitTokenUsageRichAsync(
            ProjectName,
            jobId,
            runId,
            participantId,
            topic,
            usage,
            latency);
    }

    /// <summary>
    /// Append one structured line to <c>logs/tool-calls.jsonl</c> per
    /// <see cref="CliRunEvent.ToolStarted"/> / <see cref="CliRunEvent.ToolCompleted"/>
    /// observed. Silent on other event types. The file lives next to
    /// <c>cli-output.log</c> in the job folder so a post-mortem has both
    /// in the same place.
    /// </summary>
    private void AppendToolCallLog(string jobKey, CliRunEvent evt)
    {
        if (evt is not CliRunEvent.ToolStarted and not CliRunEvent.ToolCompleted) return;

        var jobFolder = JobKeyToFolderPath(jobKey);
        if (jobFolder == null) return;
        var logsDir = System.IO.Path.Combine(jobFolder, "logs");
        try { System.IO.Directory.CreateDirectory(logsDir); } catch { return; }
        var path = System.IO.Path.Combine(logsDir, "tool-calls.jsonl");

        object record = evt switch
        {
            CliRunEvent.ToolStarted s   => new { ts = DateTime.UtcNow, kind = "started",   tool = s.ToolName, argument = s.Argument },
            CliRunEvent.ToolCompleted c => new { ts = DateTime.UtcNow, kind = "completed", tool = c.ToolName, isError = c.IsError, firstLine = c.FirstLine },
            _ => new { ts = DateTime.UtcNow, kind = "other" }
        };
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        try { System.IO.File.AppendAllText(path, json + Environment.NewLine); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Resolve a <c>jobKey</c> shaped as <c>watchPath::jobId</c> back into
    /// the on-disk folder for the job. The job may currently live in any
    /// lane; we walk the canonical lane order until one resolves.
    /// </summary>
    private string? JobKeyToFolderPath(string jobKey)
    {
        var sep = jobKey.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return null;
        var watchPath = jobKey[..sep];
        var jobId = jobKey[(sep + 2)..];
        // Most likely to find an active job in 3-progress; fall through
        // the rest of the lifecycle if not.
        foreach (var lane in new[] { "3-progress", "3a-failed-pickup", "4-auto-review", "5-human-review", "1-preparation", "2-ready", "0-backlog", "6-completed", "7-archive" })
        {
            var candidate = System.IO.Path.Combine(watchPath, lane, jobId);
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Set by <see cref="TaskRunnerService"/> on construction.</summary>
    public void ConfigureWatchdog(WatchdogConfig config) => _watchdogConfig = config;

    /// <summary>Set by <see cref="TaskRunnerService"/> on construction.</summary>
    public void ConfigureStuckLoopBudget(StuckLoopBudget budget) => _stuckLoopBudget = budget;

    /// <summary>
    /// Snapshot of the current auto-loop state for a job. Used by the
    /// jobs endpoint so the UI can render a "stuck loop N/5" badge.
    /// Returns null when no loop is in flight for this job.
    /// </summary>
    public StuckLoopState? GetStuckLoopState(string jobId) =>
        _stuckLoops.TryGetValue(jobId, out var s) ? s : null;

    private static bool IsAutoMode(string mode)
        => string.Equals(mode, "auto-continuous", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mode, "auto-single", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Boot the long-lived orchestrator session for this project (Phase H).
    /// Reads the persisted session id; if one is already on disk, we
    /// keep it and skip the boot call so a backend restart does not
    /// re-burn a few thousand input tokens. Otherwise sends a boot
    /// prompt that loads the project's README / AGENTS / ROADMAP plus
    /// recent orchestrator activity, captures the resulting session id,
    /// and persists it. Fire-and-forget from the runner host service.
    /// </summary>
    public async Task BootOrchestratorSessionAsync(CancellationToken ct)
    {
        var existing = _orchestratorSessions.Read(Entry.Path);
        if (existing != null && !string.IsNullOrWhiteSpace(existing.SessionId))
        {
            _logger.LogInformation(
                "[orchestrator] reusing persisted session {SessionId} for {Project} (calls so far: {Calls})",
                existing.SessionId, ProjectName, existing.Calls);
            return;
        }

        var modelOverride = _projectSettings.Get(ProjectName).OrchestratorModel;
        var modelId = string.IsNullOrWhiteSpace(modelOverride) ? OrchestratorRunner.DefaultModel : modelOverride!;

        var bootPrompt = BuildOrchestratorBootPrompt();

        _logger.LogInformation("[orchestrator] booting session for {Project} on {Model}", ProjectName, modelId);
        var result = await _orchestratorRunner.DecideAsync(bootPrompt, modelId, Entry.RootPath, ct);
        if (!result.Success || string.IsNullOrWhiteSpace(result.CapturedSessionId))
        {
            _logger.LogWarning(
                "[orchestrator] boot failed for {Project}: success={Success}, sessionId={SessionId}, error={Error}",
                ProjectName, result.Success, result.CapturedSessionId, result.ErrorMessage);
            return;
        }

        var session = new OrchestratorSession(
            SessionId: result.CapturedSessionId!,
            Model: result.Model,
            BootedAt: DateTime.UtcNow,
            BootPromptPreview: TruncatePreview(bootPrompt, 2000),
            BootReplyPreview: TruncatePreview(result.ReplyText, 600),
            CumulativeInputTokens: result.TokenUsage?.InputTokens ?? 0,
            CumulativeOutputTokens: result.TokenUsage?.OutputTokens ?? 0,
            CumulativeCacheReadTokens: result.TokenUsage?.CacheReadTokens ?? 0,
            CumulativeCacheCreationTokens: result.TokenUsage?.CacheCreationTokens ?? 0,
            Calls: 1,
            LastUsedAt: DateTime.UtcNow,
            LastError: null);
        _orchestratorSessions.Write(Entry.Path, session);

        _orchestratorLog.Append(Entry.Path, new OrchestratorLogEntry
        {
            Kind = OrchestratorLogKinds.Action,
            Topic = "orchestrator-boot",
            Summary = $"Orchestrator session booted on {result.Model}.",
            Reasoning = $"Session id: {session.SessionId}. Boot loaded project README / AGENTS / ROADMAP plus recent orchestrator activity. Subsequent decisions resume this session.",
            TokenUsage = result.TokenUsage
        });

        // Mirror the boot's token spend onto the bus so the workspace timeline
        // captures the boot cost as a first-class event. Prefer the rich emit
        // (carries context-window snapshot + per-call latency) when the runner
        // has the parsed usage; fall back to the legacy emit otherwise.
        if (result.ParsedUsage != null)
        {
            try
            {
                _ = _bus?.EmitTokenUsageRichAsync(
                    ProjectName, jobId: null, runId: null,
                    AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName),
                    topic: "orchestrator-boot",
                    usage: result.ParsedUsage,
                    latency: result.Latency);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator boot token usage failed for {Project}", ProjectName); }
        }
        else if (result.TokenUsage != null)
        {
            try
            {
                _ = _bus?.EmitTokenUsageAsync(
                    ProjectName, jobId: null,
                    AgentMessageBusBridge.ParticipantOrchestratorFor(ProjectName),
                    topic: "orchestrator-boot",
                    usage: result.TokenUsage);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator boot token usage failed for {Project}", ProjectName); }
        }
    }

    /// <summary>
    /// Build the boot prompt: project facts, the top of any README /
    /// AGENTS / ROADMAP at the watched path's repository, and a
    /// summary of recent orchestrator activity. Truncated so the boot
    /// stays cheap. Total target: under 8 KB so even on Opus the boot
    /// is a few cents at most.
    /// </summary>
    private string BuildOrchestratorBootPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"You are the orchestrator for the project \"{ProjectName}\" running in Agent Software Studio.");
        sb.AppendLine();
        sb.AppendLine("Project context:");
        sb.AppendLine($"- Watch path: {Entry.Path}");
        sb.AppendLine($"- Working directory: {Entry.RootPath}");
        if (!string.IsNullOrWhiteSpace(Entry.RepositoryPath))
            sb.AppendLine($"- Git repository: {Entry.RepositoryPath}");
        sb.AppendLine();

        AppendDocSnippet(sb, "AGENTS.md", Entry.RootPath, 2_000);
        AppendDocSnippet(sb, "README.md", Entry.RootPath, 2_000);
        AppendDocSnippet(sb, "ROADMAP.md", Entry.RootPath, 1_500);

        // Recent orchestrator activity, last 10 entries newest-first.
        var entries = _orchestratorLog.Read(Entry.Path);
        if (entries.Count > 0)
        {
            sb.AppendLine("Recent orchestrator activity (newest first, latest 10):");
            foreach (var e in entries.AsEnumerable().Reverse().Take(10))
                sb.AppendLine($"- [{e.Kind}/{e.Topic}] {e.Summary}");
            sb.AppendLine();
        }

        sb.AppendLine("Your role:");
        sb.AppendLine("- When the runner sends you a NEEDS_INPUT decision request, you have three reply shapes:");
        sb.AppendLine("  1) REPLY: plain text, the user-style follow-up to send back to the agent (default).");
        sb.AppendLine("  2) STEER: when you cannot decide alone but a small piece of evidence (a screenshot, a choice between options, a link to a doc) would unblock the user. Format: a leading STEER line, then Need: <one sentence>, Why: <one sentence>, optional Options: list with A) / B) bullets. Prefer STEER over BLOCK whenever a concrete unblocking ask exists.");
        sb.AppendLine("  3) BLOCK: last resort, when you cannot even formulate a steering message. Reply exactly: BLOCK");
        sb.AppendLine("- When the runner sends you a status query, summarize concisely.");
        sb.AppendLine("- The conversation history accumulated in this session is your memory across decisions; you do not need to be re-briefed each turn.");
        sb.AppendLine();
        sb.AppendLine("Acknowledge readiness with a single short sentence describing which docs you saw on boot. The first real decision request will follow.");
        return sb.ToString();
    }

    private static void AppendDocSnippet(System.Text.StringBuilder sb, string fileName, string root, int maxChars)
    {
        try
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path)) return;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            sb.AppendLine($"--- {fileName} (truncated to {maxChars} chars) ---");
            sb.AppendLine(text.Length > maxChars ? text[..maxChars] + "\n... [truncated]" : text);
            sb.AppendLine();
        }
        catch { /* best-effort: missing or unreadable docs are fine */ }
    }

    private static string TruncatePreview(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "...";
    }

    /// <summary>
    /// Phase E. The active agent emitted [[TASK_NEEDS_INPUT:...]] in auto
    /// mode; the user is not here to answer. Spawn the orchestrator with
    /// the project's configured model (default Opus 4.7), ask it for the
    /// reply the user would give, log the decision (with token usage), and
    /// feed the reply back as a Continue follow-up so the run picks up
    /// where it asked. If the orchestrator declines (returns BLOCK or
    /// errors), accept the NeedsInput state and notify the user via the
    /// chat - the same fallback the manual path uses.
    /// </summary>
    private async Task RunOrchestratorDecisionAsync(JobInfo info, string jobId, AgentOutcome outcome)
    {
        try
        {
            // Circuit breaker check BEFORE we release the active-job latch
            // or call the orchestrator. If we've already burned the loop's
            // iteration / token budget on this job, surface a meta line
            // and leave the question for the user instead of spending more.
            // The state survives until cleared on a non-NeedsInput outcome
            // (Done/Blocked) - see OnCliFinishedAsync.
            var existingLoop = _stuckLoops.TryGetValue(jobId, out var prior) ? prior : null;
            if (existingLoop != null
                && StuckLoopGuard.Decide(existingLoop, _stuckLoopBudget) == StuckLoopVerdict.CircuitBreak)
            {
                _logger.LogWarning(
                    "[orchestrator] circuit-breaker fired for {JobId}: {Iters} iters, {Tokens} tokens",
                    jobId, existingLoop.IterationCount, existingLoop.CumulativeOrchestratorTokens);
                _chatLog.Append(info, OrchestratorMessageKind.GiveUp,
                    StuckLoopGuard.FormatBreakerMessage(existingLoop, _stuckLoopBudget));
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Intervention,
                    Topic = "auto-loop-circuit-break",
                    JobId = jobId,
                    Summary = $"Auto-loop circuit-breaker fired for \"{info.Title}\".",
                    Reasoning = $"Iterations {existingLoop.IterationCount}/{_stuckLoopBudget.MaxIterations}; orchestrator tokens {existingLoop.CumulativeOrchestratorTokens}/{_stuckLoopBudget.MaxOrchestratorTokens}. Loop stopped to preserve quota; awaiting user."
                });
                return;
            }

            // Release the active-job latch so the orchestrator's spawned
            // Continue can claim it; we mirror the re-issue path's release.
            _activeJobId = null;
            _activeCliType = null;
            _activeIntent = default;
            _activeFollowup = null;
            _activePlan = null;
            _activeReissueAttempt = 0;
            NotifyStatus();

            var promptPath = Path.Combine(info.FolderPath, "prompt.md");
            var promptText = ReadPromptText(promptPath);
            var lastAgentText = outcome.Summary ?? "(no agent summary captured)";
            var attachmentsList = BuildAttachmentsList(info.FolderPath);

            var orchestratorPrompt = BuildOrchestratorPrompt(info, promptText, lastAgentText, attachmentsList);
            var modelOverride = _projectSettings.Get(info.ProjectName).OrchestratorModel;

            // Resume the long-lived session if one is on disk; the
            // orchestrator already has project context + recent decisions
            // in its history, so a tighter "current question" prompt is
            // enough. Falls back to one-shot if no session is booted yet
            // (boot at app start may still be in flight or have failed).
            var session = _orchestratorSessions.Read(Entry.Path);
            var modelToUse = modelOverride ?? session?.Model ?? OrchestratorRunner.DefaultModel;
            _logger.LogInformation(
                "[orchestrator] auto-deciding for {JobId} on model {Model} (session={SessionId})",
                jobId, modelToUse, session?.SessionId ?? "<one-shot>");

            OrchestratorDecisionResult result;
            if (session != null && !string.IsNullOrWhiteSpace(session.SessionId))
            {
                var resumePrompt = BuildOrchestratorResumePrompt(info, lastAgentText, attachmentsList);
                // Rejection-recovery lives on the runner (ResumeWithFallbackAsync)
                // so the per-job and global-chat orchestrator paths cannot drift
                // apart again - see docs/code-patterns.md "orchestrator-resume-with-fallback".
                var resumeRejected = false;
                result = await _orchestratorRunner.ResumeWithFallbackAsync(
                    session.SessionId,
                    resumePrompt,
                    fallbackPromptBuilder: () => orchestratorPrompt,
                    onSessionRejected: () =>
                    {
                        _orchestratorSessions.Clear(Entry.Path);
                        resumeRejected = true;
                    },
                    modelToUse,
                    Entry.RootPath,
                    CancellationToken.None);

                if (!resumeRejected && result.Success)
                {
                    // Accumulate cumulative usage onto the persisted session.
                    var updated = OrchestratorSessionStore.AccumulateUsage(session, result.TokenUsage, error: null);
                    _orchestratorSessions.Write(Entry.Path, updated);
                }
            }
            else
            {
                result = await _orchestratorRunner.DecideAsync(
                    orchestratorPrompt, modelToUse, Entry.RootPath, CancellationToken.None);
            }

            if (!result.Success || string.IsNullOrWhiteSpace(result.ReplyText))
            {
                // Orchestrator errored. Surface the question to the user the
                // same way the manual path does, plus a meta line explaining
                // why the orchestrator could not decide. Update the loop
                // counter so a series of declines also hits the circuit
                // breaker; the iteration is the "we tried" event, not "we
                // succeeded".
                _stuckLoops[jobId] = StuckLoopGuard.Next(
                    existingLoop, result.TokenUsage,
                    question: outcome.Summary, reply: null,
                    error: result.ErrorMessage,
                    now: DateTime.UtcNow);

                var why = result.ErrorMessage ?? "the orchestrator chose to defer this decision";
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[orchestrator] Declined to auto-decide on agent's NEEDS_INPUT: {why}. Leaving the question for you.");
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Observation,
                    Topic = "agent-needs-input",
                    JobId = jobId,
                    Summary = $"Orchestrator declined to decide for \"{info.Title}\".",
                    Reasoning = why,
                    TokenUsage = result.TokenUsage
                });
                return;
            }

            // Three-way classification on the orchestrator's reply. STEER is
            // the productive escalation: the orchestrator could not pick a
            // path on its own but identified a concrete unblocking ask
            // (screenshot, choice between options, missing doc). REPLY is
            // the existing happy path - feed it back as a Continue. BLOCK
            // is the silent deferral preserved as a last resort.
            var parsed = OrchestratorReplyParser.Parse(result.ReplyText);

            if (parsed.Kind == OrchestratorReplyKind.Block)
            {
                _stuckLoops[jobId] = StuckLoopGuard.Next(
                    existingLoop, result.TokenUsage,
                    question: outcome.Summary, reply: null,
                    error: parsed.ParseWarning,
                    now: DateTime.UtcNow);

                var why = parsed.ParseWarning ?? "the orchestrator chose to defer this decision";
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[orchestrator] Declined to auto-decide on agent's NEEDS_INPUT: {why}. Leaving the question for you.");
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Observation,
                    Topic = "agent-needs-input",
                    JobId = jobId,
                    Summary = $"Orchestrator declined to decide for \"{info.Title}\".",
                    Reasoning = why,
                    TokenUsage = result.TokenUsage
                });
                return;
            }

            if (parsed.Kind == OrchestratorReplyKind.Steer)
            {
                // Productive escalation: write a typed Steer chat message so
                // the frontend renders it distinctly (question-mark glyph,
                // option buttons, screenshot affordance). The job stays in
                // NeedsInput - we never re-issue on Steer; the user answers
                // and that becomes the next Continue.
                var nextLoopSteer = StuckLoopGuard.Next(
                    existingLoop, result.TokenUsage,
                    question: outcome.Summary, reply: parsed.ReplyText,
                    error: null,
                    now: DateTime.UtcNow);
                _stuckLoops[jobId] = nextLoopSteer;

                var formatted = OrchestratorReplyParser.FormatSteerForChat(parsed);
                _chatLog.Append(info, OrchestratorMessageKind.Steer,
                    $"[orchestrator] {formatted}");

                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Decision,
                    Topic = "agent-needs-input",
                    JobId = jobId,
                    Summary = $"Steered for \"{info.Title}\" (loop {nextLoopSteer.IterationCount}/{_stuckLoopBudget.MaxIterations}): {Truncate(parsed.Need ?? "", 140)}",
                    Reasoning = $"Orchestrator could not pick a path alone but identified a concrete unblocking ask. Need: {parsed.Need}. Why: {parsed.Why ?? "(not given)"}. Options: {(parsed.Options is { Count: > 0 } ? string.Join(" | ", parsed.Options) : "(none)")}. Job left in NeedsInput; the user's answer will become the next Continue.",
                    TokenUsage = result.TokenUsage
                });

                if (result.ParsedUsage != null)
                {
                    try
                    {
                        _ = _bus?.EmitTokenUsageRichAsync(
                            info.ProjectName, info.Id, runId: null,
                            AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                            topic: "orchestrator-steer",
                            usage: result.ParsedUsage,
                            latency: result.Latency);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator steer token usage failed for {JobId}", jobId); }
                }
                else if (result.TokenUsage != null)
                {
                    try
                    {
                        _ = _bus?.EmitTokenUsageAsync(
                            info.ProjectName, info.Id,
                            AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                            topic: "orchestrator-steer",
                            usage: result.TokenUsage);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator steer token usage failed for {JobId}", jobId); }
                }
                return;
            }

            var reply = parsed.ReplyText;

            // Successful decision. Advance the loop counter so we know how
            // many auto-decisions this job has burned and bail through the
            // circuit breaker on the next NEEDS_INPUT if budget is gone.
            var nextLoop = StuckLoopGuard.Next(
                existingLoop, result.TokenUsage,
                question: outcome.Summary, reply: reply,
                error: null,
                now: DateTime.UtcNow);
            _stuckLoops[jobId] = nextLoop;

            _chatLog.Append(info, OrchestratorMessageKind.Decision,
                $"[orchestrator] Auto-mode decision (loop {nextLoop.IterationCount}/{_stuckLoopBudget.MaxIterations}): {Truncate(reply, 200)}");
            _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Decision,
                Topic = "agent-needs-input",
                JobId = jobId,
                Summary = $"Auto-decided for \"{info.Title}\" (loop {nextLoop.IterationCount}/{_stuckLoopBudget.MaxIterations}): {Truncate(reply, 140)}",
                Reasoning = $"Project mode is {_mode}; the active agent emitted NEEDS_INPUT and the orchestrator was invoked to reply on the user's behalf. " +
                            $"The reply will be sent as a Continue follow-up. Model: {result.Model}. " +
                            $"Cumulative orchestrator tokens this loop: {nextLoop.CumulativeOrchestratorTokens}.",
                TokenUsage = result.TokenUsage
            });

            // Mirror orchestrator token spend onto the bus so the project
            // screen can rank expensive turns. orchestrator.jsonl stays
            // canonical for the per-job rollup; the bus carries one event
            // per decision turn.
            if (result.ParsedUsage != null)
            {
                try
                {
                    _ = _bus?.EmitTokenUsageRichAsync(
                        info.ProjectName, info.Id, runId: null,
                        AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                        topic: "orchestrator-decision",
                        usage: result.ParsedUsage,
                        latency: result.Latency);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator token usage failed for {JobId}", jobId); }
            }
            else if (result.TokenUsage != null)
            {
                try
                {
                    _ = _bus?.EmitTokenUsageAsync(
                        info.ProjectName, info.Id,
                        AgentMessageBusBridge.ParticipantOrchestratorFor(info.ProjectName),
                        topic: "orchestrator-decision",
                        usage: result.TokenUsage);
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of orchestrator token usage failed for {JobId}", jobId); }
            }

            // Feed the orchestrator's reply back as a Continue. Reuses the
            // existing path (RunPlanner picks Resume vs Recovery based on
            // captured session id), so this is structurally identical to
            // the user typing the same reply in the chat.
            await RunCliAsync(jobId, RunIntent.UserContinue, reply, reissueAttempt: 0,
                              mode: ContinueModes.Continue, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestrator decision flow crashed for {JobId}", jobId);
            try
            {
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    "[orchestrator] Auto-decision flow crashed; left the agent's question for you.");
            }
            catch { }
        }
    }

    /// <summary>
    /// Tighter prompt for an orchestrator session that already has the
    /// project's boot context loaded. We only re-send the current
    /// situation; everything else is in the session memory.
    /// <para>
    /// Attachments: when the user attached files to the task (typically a
    /// screenshot that the agent's question hinges on), we list the
    /// absolute paths so the orchestrator can read them with its Read tool.
    /// Without this, the orchestrator decides blind on tasks whose entire
    /// context lives in an image.
    /// </para>
    /// </summary>
    internal static string BuildOrchestratorResumePrompt(JobInfo info, string lastAgentText, string attachmentsList)
    {
        var attachmentsBlock = AttachmentsHasFiles(attachmentsList)
            ? $"\n\nAttachments on this task (read with your Read tool if relevant - the agent's question often hinges on these):\n{attachmentsList}"
            : string.Empty;
        return
            $"NEEDS_INPUT decision request for task \"{info.Title}\" (id: {info.Id})." +
            attachmentsBlock +
            "\n\nThe agent's last message you need to answer:\n" +
            lastAgentText +
            "\n\nYou have three reply shapes:\n" +
            "1) REPLY (default): plain text, the user-style follow-up to send back to the agent.\n" +
            "2) STEER: when you cannot decide alone but a small piece of evidence (a screenshot, a choice between options, a link to a doc) would unblock. Use this format exactly:\n" +
            "STEER\n" +
            "Need: <one-sentence specific ask>\n" +
            "Why: <one-sentence reasoning>\n" +
            "Options: (optional)\n" +
            "  A) ...\n" +
            "  B) ...\n" +
            "Prefer STEER over BLOCK whenever a screenshot or a choice would unblock the run.\n" +
            "3) BLOCK (last resort): reply with exactly BLOCK only when you have no idea what is going on and cannot even formulate a steering message.\n\n" +
            "Reply now. No markdown headings other than the STEER block above.";
    }

    /// <summary>
    /// Build the prompt the orchestrator's one-shot Claude call sees. Kept
    /// here instead of in a runtime template file because the framing is
    /// load-bearing for the decision contract: the orchestrator must know
    /// it can return BLOCK to defer, and must reply in the user's voice
    /// not the orchestrator's.
    /// <para>
    /// Attachments: when the user attached files to the task (typically a
    /// screenshot that the agent's question hinges on), we list the
    /// absolute paths so the orchestrator can read them with its Read tool.
    /// Without this, the orchestrator decides blind on tasks whose entire
    /// context lives in an image.
    /// </para>
    /// </summary>
    internal static string BuildOrchestratorPrompt(JobInfo info, string promptText, string lastAgentText, string attachmentsList)
    {
        var attachmentsBlock = AttachmentsHasFiles(attachmentsList)
            ? $"\n\nAttachments on this task (read with your Read tool if the agent's question hinges on them - e.g. a screenshot the agent referenced):\n{attachmentsList}"
            : string.Empty;
        return
            "You are the project orchestrator for Agent Software Studio. " +
            "The user has set this project to auto mode and stepped away. " +
            "The active task agent just asked for input and is waiting. " +
            "Your job: decide what the user would have replied, in one short paragraph, in the user's voice. " +
            "The reply will be sent back to the agent as a Continue follow-up.\n\n" +
            $"Project: {info.ProjectName}\n" +
            $"Task: {info.Title}\n\n" +
            "Original task description:\n" +
            (string.IsNullOrWhiteSpace(promptText) ? "(empty)" : promptText) +
            attachmentsBlock +
            "\n\nThe agent's last message you need to answer:\n" +
            lastAgentText +
            "\n\nReasoning style:\n" +
            "- If the agent's question has an obvious right answer in context, give it directly (REPLY).\n" +
            "- If the question is ambiguous and multiple paths are reasonable, pick the simpler path and say why in one short sentence (REPLY).\n" +
            "- Before deferring, check whether reading an attached file (e.g. a screenshot) would resolve the ambiguity; if yes, read it and decide.\n" +
            "- When you cannot decide alone but a small piece of evidence would unblock the user, prefer STEER over BLOCK. STEER is a productive escalation: a one-sentence ask, a one-sentence reason, optionally a small set of labelled options.\n" +
            "- BLOCK is the last resort, only when you cannot even formulate a steering message.\n\n" +
            "Reply shapes:\n" +
            "1) REPLY (default): plain text, the user-style follow-up directly. Do not preface with \"I would say\" or similar. No markdown headings.\n" +
            "2) STEER: use exactly this format:\n" +
            "STEER\n" +
            "Need: <one-sentence specific ask, e.g. \"screenshot of the affected column\" or \"pick option A vs B\">\n" +
            "Why: <one-sentence reasoning>\n" +
            "Options: (optional)\n" +
            "  A) ...\n" +
            "  B) ...\n" +
            "3) BLOCK: reply with exactly the single word BLOCK on its own.";
    }

    private static bool AttachmentsHasFiles(string attachmentsList)
        => !string.IsNullOrWhiteSpace(attachmentsList)
           && !string.Equals(attachmentsList.Trim(), "(none)", StringComparison.Ordinal);

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        return s[..(max - 1)].TrimEnd() + "...";
    }

    private void OnCliFinished(string cliType, string jobKey, CliExecution execution)
    {
        var activeJobId = _activeJobId;
        if (GetActiveJobKey() != jobKey || activeJobId == null) return;
        if (_activeCliType != null && !string.Equals(cliType, _activeCliType, StringComparison.OrdinalIgnoreCase)) return;

        _ = Task.Run(() => OnCliFinishedAsync(cliType, jobKey, execution, activeJobId));
    }

    private async Task OnCliFinishedAsync(string cliType, string jobKey, CliExecution execution, string jobId)
    {
        // Snapshot the run-scoped fields BEFORE any path can clear them. The
        // re-issue branch and RunOrchestratorDecisionAsync both null out
        // _activePlan as part of releasing the active-job latch, and an
        // upstream tick can re-enter RunCliAsync and reassign these fields
        // before the capture-fail block reads them. Reading from a local
        // snapshot makes the recovery decision deterministic w.r.t. THIS
        // run, regardless of what other paths do concurrently.
        var planSnapshot = _activePlan;
        var intentSnapshot = _activeIntent;
        var followupSnapshot = _activeFollowup;
        var reissueAttemptSnapshot = _activeReissueAttempt;
        try
        {
            if (GetActiveJobKey() != jobKey || _activeJobId != jobId) return;
            if (_activeCliType != null && !string.Equals(cliType, _activeCliType, StringComparison.OrdinalIgnoreCase)) return;

            _logger.LogInformation("Job {JobId} finished in project '{Project}' on {Cli} with status {Status}",
                jobId, ProjectName, cliType, execution.Status);

            var cli = _router.Get(cliType);

            // Persist last token/usage summary (best-effort)
            var usage = cli.GetLastUsage(jobKey);
            if (usage != null)
            {
                _sessions.UpdateLastUsage(jobId, usage, Entry.Path);
            }

            // Mirror run-finish + agent-side token usage onto the bus. We emit
            // these even before the post-run policy and lane move so a crash
            // mid-finalisation does not lose the lifecycle event. RunFinished
            // is the matching pair to the RunStarted emitted on spawn.
            try
            {
                var finishedInfo = _scanner.FindJob(jobId, Entry.Path);
                if (finishedInfo != null)
                {
                    _ = _bus?.EmitRunFinishedAsync(
                        finishedInfo, cliType, execution.StartedAt,
                        execution.Status ?? "unknown",
                        execution.DurationSeconds, agentOutcome: null);
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of run-finish failed for {JobId}", jobId); }

            // Persist the captured session UUID so follow-ups can resume.
            // Claude / Codex / Gemini all auto-create a UUID on first run and
            // surface it in their JSON output; we capture it during streaming
            // and write it back here. Without this, Continue always loses
            // context because info.SessionName never advances past the slug.
            var capturedSessionId = cli switch
            {
                ClaudeCliService claude => claude.GetCapturedSessionId(jobKey),
                CodexCliService codex   => codex.GetCapturedSessionId(jobKey),
                GeminiCliService gemini => gemini.GetCapturedSessionId(jobKey),
                _ => null
            };
            // Capture the post-run HEAD SHA so the run's commit set can be
            // derived deterministically via git rev-list HeadShaBefore..After.
            // This must happen before any auto-commit hook fires (the hook
            // is part of the Progress->Review transition, not the run
            // itself, and we want the run to own the agent's own commits).
            var headShaAfter = SafeGetHeadSha(jobId);
            if (!string.IsNullOrWhiteSpace(headShaAfter))
            {
                _sessions.BackfillLatestSessionEventHeadShaAfter(jobId, headShaAfter, Entry.Path);
            }

            if (!string.IsNullOrWhiteSpace(capturedSessionId))
            {
                // Append to the chain (and update sessionName in lockstep). Forking
                // CLIs emit a new id on every --resume; preserving the chain lets the
                // user see how often the session has been continued.
                _sessions.AppendSessionToChain(jobId, capturedSessionId!, Entry.Path);
                _sessions.BackfillLatestSessionEventCapturedId(jobId, capturedSessionId!, Entry.Path);

                // Reset the per-job capture-fail circuit-breaker counter on
                // genuine success. Without this, a job that flaked two runs
                // and then succeeded would carry the prior count forward and
                // a single later capture-fail would trip the breaker as if
                // three failures had occurred in a row.
                if (_consecutiveCaptureFailJobId == jobId)
                {
                    _consecutiveCaptureFailCount = 0;
                    _consecutiveCaptureFailJobId = null;
                }
            }
            else if (cli is ClaudeCliService
                  || cli is CodexCliService
                  || cli is GeminiCliService)
            {
                // The CLI normally emits a session UUID on every run; missing
                // it means the next follow-up will fall back to Recovery. Tell
                // the user explicitly so the loop is not silent.
                //
                // When the just-finished run was a --resume attempt, the
                // resume target is the most likely cause: the CLI rejected
                // the id (e.g. claude prints "No conversation found with
                // session ID: <uuid>" on stdout, exits non-zero, and never
                // emits a new init frame). Leaving the dead id in
                // SessionName would make the next follow-up try the same
                // resume and fail identically. Clear it now and mark the
                // chain as recovery so the planner routes the next user
                // turn to Recovery instead of Continue. ADR-0002 / ADR-0006
                // make Recovery the expected hand-off for session loss.
                var captureFailInfo = _scanner.FindJob(jobId, Entry.Path);
                if (captureFailInfo != null)
                {
                    var resumeTargetWasGone = ShouldMarkSessionChainRecovery(planSnapshot);
                    if (resumeTargetWasGone)
                    {
                        _sessions.SetJobSessionName(jobId, null, Entry.Path);
                        _sessions.MarkSessionChainRecovery(jobId, Entry.Path);
                    }

                    var msg = resumeTargetWasGone
                        ? $"[capture-fail] {cli.CliType} rejected the resume target ({planSnapshot!.SessionToResume}); next follow-up will rebuild from disk via Recovery."
                        : $"[capture-fail] No {cli.CliType} session id from this run; next follow-up will rebuild from disk.";
                    _chatLog.Append(captureFailInfo, OrchestratorMessageKind.Decision, msg);

                    // Per-job consecutive capture-fail circuit-breaker.
                    // The recovery marker above SHOULD prevent the next
                    // pickup from resuming the same dead UUID, but several
                    // failure modes (race with planner, planner reads stale
                    // info, scanner cache) can still re-feed the same
                    // session. Past the threshold we stop the runner so a
                    // structural problem stops burning quota in a tight
                    // loop. Reset on the success path above.
                    var prior = _consecutiveCaptureFailJobId == jobId ? _consecutiveCaptureFailCount : 0;
                    _consecutiveCaptureFailJobId = jobId;
                    _consecutiveCaptureFailCount = prior + 1;
                    if (_consecutiveCaptureFailCount >= CaptureFailHaltThreshold && IsAutoMode(_mode))
                    {
                        _logger.LogWarning(
                            "Runner '{Project}' halting auto-mode after {N} consecutive capture-fails on {JobId}",
                            ProjectName, _consecutiveCaptureFailCount, jobId);
                        _chatLog.Append(captureFailInfo, OrchestratorMessageKind.Decision,
                            $"Auto-mode paused: {_consecutiveCaptureFailCount} consecutive {cli.CliType} runs for this job ended without capturing a session id. The session is unrecoverable; rebuild from prompt.md or rephrase before re-enabling auto.");
                        SetMode("manual",
                            $"capture-fail circuit-breaker: {_consecutiveCaptureFailCount}x no session id on {jobId} ({cli.CliType})");
                        _consecutiveCaptureFailCount = 0;
                        _consecutiveCaptureFailJobId = null;
                    }
                }
            }

            // Snapshot the live output before we flush it to disk. The
            // outcome analyzer needs the buffer to classify the run, and the
            // post-run policy may re-issue another run on top before we let
            // the regular review/summary pipeline proceed.
            var liveOutputSnapshot = cli.GetOutput(jobKey);

            // Strict-iteration progress-first pickup bookkeeping: only
            // autopickup runs feed the per-slug silent-attempt counter.
            // Manual starts and user-driven continues do not count, since
            // they are user-acknowledged and not part of the autonomous
            // queue pacing. A run that streamed any output line resets the
            // counter; a fully silent run increments it. Reaching
            // <see cref="PickupFailureThreshold"/> dead-letters the folder
            // on the next pickup tick.
            if (intentSnapshot == RunIntent.AutoPickup)
            {
                RecordPickupAttemptResult(
                    slug: jobId,
                    outputLines: liveOutputSnapshot.Count,
                    durationSeconds: execution.DurationSeconds ?? 0.0,
                    executionStatus: execution.Status);
            }

            // Write CLI output to log file. The runtime JSONL is the durable
            // backup that lets us recover the Activity Log after a backend
            // restart; once the consolidated cli-output.log has it, the JSONL
            // can go so the disk-fallback path in GetOutput doesn't replay the
            // same lines after the in-memory buffer is evicted.
            var activeInfo = _scanner.FindJob(jobId, Entry.Path);
            if (activeInfo != null && WriteCliLog(activeInfo, cli))
            {
                cli.DiscardPersistedOutput(jobKey);
            }

            // Bump lastProgressAt so CrashRecoveryService can attribute orphan
            // working-tree changes to the most-recently-active job per project
            // on next boot. Cheap (single field write); see ADR-0020.
            if (activeInfo != null)
            {
                _mutations.SetJobLastProgressAt(activeInfo.FolderPath, DateTime.UtcNow);
            }

            // Apply the orchestrator's post-run policy. The policy is pure;
            // we apply its decision here. The activeInfo lookup may fail
            // (job folder moved between completion and lookup), in which
            // case we skip the meta channel and fall through to the
            // existing accept-path - the policy is a refinement, not a gate.
            var capturedIntent = intentSnapshot;
            var capturedFollowup = followupSnapshot;
            var capturedPlan = planSnapshot;
            var capturedAttempt = reissueAttemptSnapshot;
            var outcome = AgentOutcomeAnalyzer.Analyze(
                liveOutputSnapshot,
                execution.Status ?? "completed",
                execution.DurationSeconds ?? 0.0);
            var terminalOutcome = TerminalRunOutcomeClassifier.Classify(execution.Status, outcome);
            OutcomeAction? action = capturedPlan != null
                ? RunOutcomePolicy.Decide(capturedIntent, capturedPlan, outcome, capturedFollowup, capturedAttempt)
                : null;
            if (action != null && activeInfo != null)
            {
                // Build a short signature so we can suppress the second
                // identical heuristic message in a Recovery cascade. Two
                // back-to-back "needsinput" warnings on a stuck loop do not
                // help the user; one is enough.
                var signature = $"{action.Kind}|{action.IssueKind}|{action.IsHeuristicFallback}|{outcome.Kind}|{string.Equals(capturedPlan!.EventKind, "recovery", StringComparison.OrdinalIgnoreCase)}";
                var suppress = action.Kind == OutcomeActionKind.NotifyUserAndAccept
                            && (action.IsHeuristicFallback || action.IssueKind != RunIssueKind.None)
                            && string.Equals(_lastMetaSignature, signature, StringComparison.Ordinal);

                if (!suppress)
                {
                    if (!string.IsNullOrWhiteSpace(action.MetaMessage))
                    {
                        var kind = action.MessageKind != OrchestratorMessageKind.Decision
                            ? action.MessageKind
                            : action.Kind switch
                        {
                            OutcomeActionKind.ReissueWithStrongerFraming => OrchestratorMessageKind.Reissue,
                            OutcomeActionKind.NotifyUserAndStop          => OrchestratorMessageKind.GiveUp,
                            _                                            => OrchestratorMessageKind.Decision
                        };
                        var category = action.IssueKind == RunIssueKind.None ? null : ToIssueTopic(action.IssueKind);
                        var message = category == null
                            ? action.MetaMessage
                            : $"{action.MetaMessage} (category: {category}; run summary: {outcome.Summary ?? "n/a"})";
                        _chatLog.Append(activeInfo, kind, message);
                    }
                }
                _lastMetaSignature = signature;

                if (action.Kind == OutcomeActionKind.ReissueWithStrongerFraming
                    && !string.IsNullOrWhiteSpace(action.FollowupRetryPrompt))
                {
                    // Release the active-job latch on the original run so the
                    // re-issue can claim it. We then schedule the re-issue on
                    // the thread pool so OnCliFinished returns promptly.
                    _activeJobId = null;
                    _activeCliType = null;
                    NotifyStatus();
                    var wasRecovery = string.Equals(capturedPlan!.EventKind, "recovery", StringComparison.OrdinalIgnoreCase);
                    var retryPrompt = action.IsPreframedRetryPrompt
                        ? action.FollowupRetryPrompt!
                        : RunOutcomePolicy.BuildReissueFollowupPrompt(action.FollowupRetryPrompt!, recoveryContext: wasRecovery);
                    var retryAttempt = action.RetryAttempt;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await RunCliAsync(jobId, RunIntent.UserContinue, retryPrompt, retryAttempt, ContinueModes.Continue, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Re-issue run failed for {JobId}", jobId);
                        }
                    });
                    return;
                }

                if (action.Kind == OutcomeActionKind.NotifyUserAndStop
                    && activeInfo != null
                    && ShouldRouteIssueToHumanReview(action.IssueKind))
                {
                    _orchestratorLog.Append(activeInfo.WatchPath, new OrchestratorLogEntry
                    {
                        Kind = OrchestratorLogKinds.Intervention,
                        Topic = ToIssueTopic(action.IssueKind),
                        JobId = jobId,
                        Summary = $"Routed \"{activeInfo.Title}\" to human review after {ToIssueTopic(action.IssueKind)}.",
                        Reasoning = action.MetaMessage
                    });
                    var move = await _transitions.MoveAsync(jobId, JobStates.HumanReview, activeInfo.WatchPath, CancellationToken.None);
                    if (move.Status != MoveJobStatus.Success)
                    {
                        _logger.LogWarning(
                            "Issue routing to human review failed for {JobId}: {Status} {Message}",
                            jobId, move.Status, move.Message);
                    }
                    return;
                }
            }

            // Auto-mode NeedsInput: when the project's runner is in an auto
            // mode and the agent emitted [[TASK_NEEDS_INPUT:...]] (or the
            // heuristic landed on NeedsInput with substantial text), we ask
            // the orchestrator to decide on the user's behalf and feed the
            // decision back as a Continue follow-up. Manual mode keeps
            // today's path: the question stays in the chat for the user.
            if (capturedPlan != null
                && (outcome.Kind == AgentOutcomeKind.NeedsInput)
                && IsAutoMode(_mode)
                && activeInfo != null)
            {
                _ = Task.Run(() => RunOrchestratorDecisionAsync(activeInfo, jobId, outcome));
                return;
            }

            // Loop closed: the agent did NOT come back with another
            // NEEDS_INPUT. Whether that's Done, Blocked, or anything else,
            // the auto-loop is no longer active for this job, so reset
            // the stuck-loop counter. A future NEEDS_INPUT on the same
            // job starts a fresh loop with a fresh budget.
            _stuckLoops.TryRemove(jobId, out _);

            var movedToReview = RunCompletionPolicy.ShouldMoveToReview(terminalOutcome);
            if (movedToReview)
            {
                // Drop a completion marker BEFORE the move so a crash between
                // here and the folder-rename leaves enough state on disk for
                // CrashRecoveryService to finish the transition on next boot.
                // Cleared after a successful move (no point keeping a marker
                // in 4-auto-review). See ADR-0020 + ADR-0025 (post-CLI lane
                // is 4-auto-review now; the orchestrator decides whether to
                // promote to 5-human-review).
                if (activeInfo != null)
                {
                    CompletionMarker.Write(activeInfo.FolderPath, new CompletionMarker
                    {
                        TargetState = JobStates.AutoReview,
                        ExecutionStatus = execution.Status,
                        AgentOutcome = outcome.Kind.ToString()
                    }, _logger);
                }

                var moveOutcome = await _transitions.MoveAsync(jobId, JobStates.AutoReview, Entry.Path, CancellationToken.None);
                if (moveOutcome.Status == MoveJobStatus.Success)
                {
                    var movedInfo = _scanner.FindJob(jobId, Entry.Path);
                    if (movedInfo != null) CompletionMarker.Clear(movedInfo.FolderPath, _logger);
                    // Fire-and-forget Haiku summary on successful completion.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var info = _scanner.FindJob(jobId, Entry.Path);
                            if (info != null) await _summaryService.GenerateAsync(info, terminalOutcome);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Summary generation crashed for {JobId}", jobId);
                        }
                    });
                }
                else
                {
                    _logger.LogWarning(
                        "Job {JobId} completed but could not move to review: {Status} {Message}",
                        jobId, moveOutcome.Status, moveOutcome.Message);
                }
            }
            else
            {
                _logger.LogInformation(
                    "Job {JobId} finished with status {Status}. Leaving it in progress for review or recovery.",
                    jobId, execution.Status);
            }

            // Auto-pickup cascade containment. Only auto-issued runs feed
            // the counter; manual starts and user-driven continues do not.
            // Reaching the threshold flips the runner to manual so a single
            // bad event (mid-flight kill, dead session id, watchdog
            // regression) cannot burn through the entire ready queue.
            if (capturedIntent == RunIntent.AutoPickup)
            {
                if (movedToReview)
                {
                    _consecutiveAutoFailureCount = 0;
                    _recentAutoFailureJobIds.Clear();
                }
                else
                {
                    _consecutiveAutoFailureCount++;
                    _recentAutoFailureJobIds.Enqueue(jobId);
                    while (_recentAutoFailureJobIds.Count > AutoFailureHaltThreshold)
                        _recentAutoFailureJobIds.Dequeue();
                    if (_consecutiveAutoFailureCount >= AutoFailureHaltThreshold && IsAutoMode(_mode))
                    {
                        var offenders = string.Join(", ", _recentAutoFailureJobIds);
                        _logger.LogWarning(
                            "Runner '{Project}' halting auto-mode after {N} consecutive failures: {Offenders}",
                            ProjectName, _consecutiveAutoFailureCount, offenders);

                        // If all N recent failures are the SAME job, the
                        // offender is unambiguous — route it out of
                        // 3-progress into 5-human-review so the user sees
                        // a clear "needs your attention" surface instead
                        // of a job stuck mid-pipeline. We still pause
                        // auto-mode to protect the rest of the queue.
                        var sameJobRepeated = _recentAutoFailureJobIds.Count >= AutoFailureHaltThreshold
                            && _recentAutoFailureJobIds.All(id => string.Equals(id, jobId, StringComparison.Ordinal));

                        if (activeInfo != null)
                        {
                            var note = sameJobRepeated
                                ? $"Auto-mode paused and job moved to human review: {AutoFailureHaltThreshold} consecutive runs of '{jobId}' did not reach review. Investigate before re-running."
                                : $"Auto-mode paused: {AutoFailureHaltThreshold} consecutive auto-pickup runs did not reach review ({offenders}). Investigate before re-enabling.";
                            _chatLog.Append(activeInfo, OrchestratorMessageKind.Decision, note);
                        }

                        if (sameJobRepeated && activeInfo != null)
                        {
                            try
                            {
                                // Fire-and-forget: the move is the right
                                // thing, but a failure here must not
                                // prevent the auto-mode pause below.
                                _ = _transitions.MoveAsync(jobId, JobStates.HumanReview, activeInfo.WatchPath, CancellationToken.None);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Loud-failure routing to human-review failed for {JobId}", jobId);
                            }
                        }

                        SetMode("manual",
                            sameJobRepeated
                                ? $"auto-failure circuit-breaker: {_consecutiveAutoFailureCount}x same job '{jobId}' did not reach review"
                                : $"auto-failure circuit-breaker: {_consecutiveAutoFailureCount} consecutive auto-pickups did not reach review ({offenders})");
                        _consecutiveAutoFailureCount = 0;
                        _recentAutoFailureJobIds.Clear();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Runner finalization crashed for {JobId}", jobId);
        }
        finally
        {
            if (_activeJobId == jobId)
            {
                _activeJobId = null;
                _activeCliType = null;
                _activeIntent = default;
                _activeFollowup = null;
                _activePlan = null;
                _activeReissueAttempt = 0;
                NotifyStatus();
            }
        }
    }

    private static bool ShouldRouteIssueToHumanReview(RunIssueKind issueKind)
        => issueKind is RunIssueKind.PermissionBlocked
                     or RunIssueKind.WatchdogTimeout
                     or RunIssueKind.EnvironmentBlocker;

    private static string ToIssueTopic(RunIssueKind issueKind) => issueKind switch
    {
        RunIssueKind.PermissionBlocked        => "permission-blocked",
        RunIssueKind.WatchdogTimeout          => "watchdog-timeout",
        RunIssueKind.MissingTerminalSentinel  => "missing-terminal-sentinel",
        RunIssueKind.HeuristicDone             => "heuristic-done",
        RunIssueKind.ClassifierUnknown        => "classifier-unknown",
        RunIssueKind.NoAgentOutput            => "no-agent-output",
        RunIssueKind.EnvironmentBlocker       => "environment-blocker",
        _                                     => "none"
    };

    /// <summary>
    /// Appends a clearly-visible separator line into <c>logs/cli-output.log</c>
    /// when continue falls back to recovery. Lets the protocol pane render a
    /// chain break so the user can see the cut instead of being confused why
    /// the agent re-reads the job folder mid-conversation.
    /// </summary>
    private void AppendSessionCutMarkerToCliLog(JobInfo info, string reason)
    {
        try
        {
            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));
            var logPath = JobPaths.CliOutputLog(info.FolderPath);
            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var line = $"[{ts}] [system] --- Session lost ({reason}) - recovering from job folder ---";
            var prefix = File.Exists(logPath) && new FileInfo(logPath).Length > 0
                ? Environment.NewLine
                : string.Empty;
            File.AppendAllText(logPath, prefix + line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write session cut marker for {JobId}", info.Id);
        }
    }

    /// <returns>
    /// True if the consolidated <c>logs/cli-output.log</c> was updated. The
    /// caller uses this signal to decide whether the runtime JSONL backup
    /// can now be discarded.
    /// </returns>
    private bool WriteCliLog(JobInfo info, ICliExecutionService cli)
    {
        try
        {
            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));
            var logPath = JobPaths.CliOutputLog(info.FolderPath);

            var output = cli.GetOutput(info.JobKey);
            if (output.Count == 0)
            {
                // GetOutput already falls back to the on-disk JSONL when the
                // in-memory buffer is gone, so an empty result means nothing
                // to flush - don't truncate the existing log.
                return false;
            }

            var logContent = string.Join(Environment.NewLine,
                output.Select(l => $"[{l.Timestamp:HH:mm:ss.fff}] [{l.Stream}] {l.Text}"));

            // Append so that continuation sessions accumulate rather than overwrite.
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
                File.AppendAllText(logPath, Environment.NewLine + logContent, System.Text.Encoding.UTF8);
            else
                File.WriteAllText(logPath, logContent, System.Text.Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write CLI log for job {JobId}", info.Id);
            return false;
        }
    }

    private ICliExecutionService GetCliFor(JobInfo info) => _router.Get(info.CliType);
    private string GetJobKey(string jobId) => JobIdentity.CreateKey(Entry.Path, jobId);
    private string? GetActiveJobKey() => _activeJobId != null ? GetJobKey(_activeJobId) : null;

    /// <summary>
    /// Atomically releases the in-memory active-job latch when an external
    /// actor (the API move endpoint, the boot-time stuck-folder sweep, a
    /// hand-edited folder move) takes the active job out of <c>3-progress</c>.
    /// Without this, the runner's <c>_activeJobId</c> stays pinned at a slug
    /// whose folder is gone or in another lane, every subsequent pickup tick
    /// short-circuits on <c>_activeJobId != null</c>, and the project is
    /// wedged until a backend restart.
    ///
    /// <para>
    /// Stops a live CLI process for that job first when one is recorded; the
    /// usual cli-finished callback then runs through and would clear the
    /// latch on its own, but we also clear synchronously so the next caller
    /// (orchestrator move side-effect, watcher reconciliation) sees a clean
    /// slate without waiting for the OS to reap the child.
    /// </para>
    /// </summary>
    /// <returns>True if the runner was holding this job and the latch was cleared.</returns>
    public bool ClearActiveJobIfMatches(string jobId, string reason)
    {
        if (string.IsNullOrEmpty(jobId)) return false;
        if (_activeJobId != jobId) return false;

        _logger.LogInformation(
            "Runner '{Project}' clearing active job '{JobId}': {Reason}",
            ProjectName, jobId, reason);

        if (_activeCliType != null)
        {
            try
            {
                var jobKey = GetJobKey(jobId);
                _router.Get(_activeCliType).Stop(jobKey, RunStopReason.Cancelled);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ClearActiveJobIfMatches: cli.Stop failed for {JobId}", jobId);
            }
        }

        _activeJobId = null;
        _activeCliType = null;
        _activeIntent = default;
        _activeFollowup = null;
        _activePlan = null;
        _activeReissueAttempt = 0;

        // Best-effort: drop a chat-log line on the moved folder so the
        // protocol pane shows why the latch was released. The job's folder
        // has already moved, so we look it up by id post-move; if the job
        // is gone (delete + folder-rm), we skip silently.
        try
        {
            var movedInfo = _scanner.FindJob(jobId, Entry.Path);
            if (movedInfo != null)
            {
                _chatLog.Append(movedInfo, OrchestratorMessageKind.Decision,
                    $"Runner active state cleared: {reason}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClearActiveJobIfMatches: chat-log append failed for {JobId}", jobId);
        }

        NotifyStatus();
        return true;
    }

    /// <summary>
    /// Defensive watcher reconciliation: if the in-memory active-job latch
    /// points at a job whose folder is no longer in <c>3-progress</c>
    /// (deleted, moved by an external script, archived by the boot-time
    /// stuck-folder sweep), release the latch so the next pickup tick can
    /// choose freely. Cheap when there is no active job; costs one
    /// <see cref="JobScannerService.FindJob"/> when there is.
    /// </summary>
    /// <returns>True if the latch was held and got cleared by this call.</returns>
    public bool ReconcileActiveJobAgainstDisk()
    {
        var jobId = _activeJobId;
        if (jobId == null) return false;
        if (_processing) return false;

        JobInfo? info = null;
        try { info = _scanner.FindJob(jobId, Entry.Path); }
        catch (Exception ex) { _logger.LogDebug(ex, "Reconcile: FindJob threw for {JobId}", jobId); }

        if (info != null && info.State == JobStates.Progress) return false;

        var reason = info == null
            ? "active job folder no longer exists"
            : $"active job moved out of 3-progress (now in {info.State})";
        return ClearActiveJobIfMatches(jobId, reason);
    }

    /// <summary>Test seam: lets a unit test prime the active-job latch
    /// without spinning up a real CLI run.</summary>
    internal void SetActiveJobForTest(string jobId, string? cliType = null)
    {
        _activeJobId = jobId;
        _activeCliType = cliType;
    }

    private string RenderPrompt(RunPlan plan, JobInfo info)
    {
        if (plan.PromptOverride != null) return plan.PromptOverride;
        if (string.IsNullOrWhiteSpace(plan.PromptTemplate))
            throw new InvalidOperationException("Run plan has neither a prompt template nor a prompt override.");

        var promptPath = Path.Combine(info.FolderPath, "prompt.md");
        var values = new Dictionary<string, string?>(plan.PromptVariables)
        {
            ["prompt_path"] = promptPath,
            ["prompt_text"] = ReadPromptText(promptPath),
            ["job_folder"] = info.FolderPath,
            ["title"] = string.IsNullOrWhiteSpace(info.Title) ? "(untitled)" : info.Title,
            ["working_directory"] = Entry.RootPath,
            ["repository_path"] = string.IsNullOrWhiteSpace(Entry.RepositoryPath) ? Entry.RootPath : Entry.RepositoryPath,
            ["attachments_list"] = BuildAttachmentsList(info.FolderPath)
        };
        return _prompts.Render(plan.PromptTemplate, values);
    }

    private static string ReadPromptText(string promptPath)
    {
        try
        {
            return File.Exists(promptPath) ? File.ReadAllText(promptPath).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string BuildAttachmentsList(string jobFolder)
    {
        try
        {
            var dir = Path.Combine(jobFolder, "attachments");
            if (!Directory.Exists(dir)) return "(none)";
            var files = Directory.EnumerateFiles(dir).OrderBy(p => p).ToList();
            if (files.Count == 0) return "(none)";
            return string.Join("\n", files.Select(f => $"- `{Path.GetFileName(f)}` → `{f}`"));
        }
        catch
        {
            return "(none)";
        }
    }

    /// <summary>
    /// Returns the oldest pickup-eligible job in <c>2-ready</c> for this
    /// project, or <c>null</c> when the lane is empty (or every entry is
    /// blocked by an active intake-running phase).
    /// </summary>
    /// <remarks>
    /// Filters strictly on <c>State == 2-ready</c>. Jobs sitting in
    /// <c>1-preparation</c>, <c>1a-orchestrator-prep</c>,
    /// <c>1b-needs-human-review</c>, <c>4-auto-review</c>, or
    /// <c>5-human-review</c> have no influence here - those lanes are
    /// processed by their own background services in parallel with the
    /// runner. The single-state-machine rule (ADR-0001) is preserved by
    /// the active-job latch in <see cref="TickAsync"/>, not by lane
    /// coupling. Pinned by <c>ParallelLanesPickupTests</c>.
    /// </remarks>
    internal JobInfo? GetNextReadyJob()
    {
        var intakeEnabled = _projectSettings.Get(ProjectName).IntakeEnabled == true;
        return _scanner.ScanAllJobs()
            .Where(j => j.ProjectName == ProjectName
                        && j.State == JobStates.Ready
                        && AgentTypes.IsAutoPickupEligible(j.Agent)
                        && IsPickupAllowed(j, intakeEnabled))
            .OrderBy(j => j.Order)
            .FirstOrDefault();
    }

    /// <summary>
    /// Intake gate. When intake is disabled (default), every 2-ready card is
    /// pickup-eligible. When intake is enabled per project, the runner waits
    /// for the orchestrator-intake hosted service to mark a card
    /// <see cref="LifecyclePhases.IntakePassed"/> before picking it up. Cards
    /// in <c>human-ready</c>, <c>intake-running</c>, or <c>intake-blocked</c>
    /// stay in 2-ready and the runner tick falls through to the next card.
    /// </summary>
    internal static bool IsPickupAllowed(JobInfo job, bool intakeEnabled)
    {
        if (!intakeEnabled) return true;
        return job.Phase == LifecyclePhases.IntakePassed;
    }

    /// <summary>
    /// Returns the oldest 3-progress job for this project that carries a
    /// captured session id we can resume against. The auto-pickup tick
    /// prefers these over jobs in 2-ready so an interrupted in-flight run
    /// continues where it left off instead of being skipped while a fresh
    /// job is started. A job has resumable state when either
    /// <see cref="JobInfo.SessionName"/> or any non-recovery-marker entry
    /// in <see cref="JobInfo.SessionChain"/> is non-empty.
    /// </summary>
    /// <remarks>
    /// Retained because <see cref="AutoPickupCascadeTests"/> pins the
    /// <see cref="HasResumableSession"/> classifier as a public-shape
    /// invariant. The pickup tick itself no longer routes through this
    /// method; <see cref="TryPickProgressJobOrDeadLetter"/> picks ANY
    /// 3-progress folder regardless of session state (the "no log" case
    /// is the most-restartable, not the most-skippable).
    /// </remarks>
    private JobInfo? GetNextResumableProgressJob()
    {
        return _scanner.ScanAllJobs()
            .Where(j => j.ProjectName == ProjectName
                        && j.State == JobStates.Progress
                        && HasResumableSession(j))
            .OrderBy(j => j.CreatedAt)
            .FirstOrDefault();
    }

    internal static bool HasResumableSession(JobInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.SessionName)) return true;
        if (info.SessionChain == null || info.SessionChain.Count == 0) return false;
        return info.SessionChain.Any(id => !string.IsNullOrWhiteSpace(id));
    }

    // Strict-iteration progress-first pickup (deliverables of the
    // pickup-loop-progress-first-strict-iteration task).
    //
    // Production observation: a 2-ready job had been picked up while a
    // 3-progress folder for the same project still existed, because the
    // older "GetNextResumableProgressJob" filter required a captured
    // session id. The folder in question lost its cli-output.log to a
    // race during a backend restart, so it carried no session id and was
    // skipped. The fix walks EVERY 3-progress folder oldest-first by
    // mtime before considering 2-ready, and dead-letters any folder that
    // has exhausted the retry budget without producing CLI output.

    /// <summary>Default retry budget before a 3-progress folder is dead-lettered.</summary>
    internal const int PickupFailureThreshold = 3;
    /// <summary>
    /// Per-attempt deadline (seconds) within which the spawned CLI must
    /// produce at least one streamed output line for the attempt to count
    /// as healthy. Today the runner observes this passively at run-finish:
    /// a run that finishes with zero captured output lines is treated as
    /// a silent attempt regardless of duration. The constant is recorded
    /// in the dead-letter row so operators can correlate the verdict with
    /// the active configuration.
    /// </summary>
    internal const int PickupOutputDeadlineSeconds = 60;

    // Per-slug consecutive-silent-attempt counter. In-memory only - a
    // backend restart resets the counter, which matches the wider runner
    // pattern (a restart is itself a recovery boundary). Bounded by
    // <see cref="PickupFailureThreshold"/>; the same dictionary is read
    // when picking and written when the failed run finishes.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PickupAttemptState> _pickupAttempts = new();

    private sealed class PickupAttemptState
    {
        public int Count;
        public readonly Queue<PickupAttemptDiagnostic> History = new();
    }

    /// <summary>Test seam: lets a unit test prime the per-slug attempt counter
    /// without driving a real failed run.</summary>
    internal void SetPickupAttemptsForTest(string slug, int count)
    {
        var state = _pickupAttempts.GetOrAdd(slug, _ => new PickupAttemptState());
        state.Count = count;
    }

    /// <summary>Test seam: read the per-slug attempt counter.</summary>
    internal int GetPickupAttempts(string slug)
        => _pickupAttempts.TryGetValue(slug, out var s) ? s.Count : 0;

    /// <summary>Test seam: read the consecutive auto-failure counter so
    /// regression tests can prove the counter actually resets after a
    /// successful auto-pickup, instead of inferring from "mode stayed
    /// auto" (which would still pass if the counter silently leaked).</summary>
    internal int GetConsecutiveAutoFailureCountForTest() => _consecutiveAutoFailureCount;

    /// <summary>Test seam: read the per-job consecutive capture-fail
    /// counter (and the job it is attributed to). Same motivation as
    /// <see cref="GetConsecutiveAutoFailureCountForTest"/>.</summary>
    internal (int Count, string? JobId) GetConsecutiveCaptureFailStateForTest()
        => (_consecutiveCaptureFailCount, _consecutiveCaptureFailJobId);

    /// <summary>Test seam: prime the consecutive auto-failure counter
    /// so regression tests can drive the reset path without first
    /// running three failed auto-pickups end-to-end.</summary>
    internal void SetConsecutiveAutoFailureCountForTest(int count, string? jobId = null)
    {
        _consecutiveAutoFailureCount = count;
        _recentAutoFailureJobIds.Clear();
        if (!string.IsNullOrWhiteSpace(jobId))
        {
            for (var i = 0; i < count; i++) _recentAutoFailureJobIds.Enqueue(jobId);
        }
    }

    /// <summary>Test seam: prime the per-job consecutive capture-fail
    /// counter directly (same motivation as the auto-failure seam).</summary>
    internal void SetConsecutiveCaptureFailStateForTest(int count, string? jobId)
    {
        _consecutiveCaptureFailCount = count;
        _consecutiveCaptureFailJobId = jobId;
    }

    /// <summary>
    /// Strict-iteration progress-first picker. Walks every 3-progress folder
    /// for this project oldest-first by mtime, dead-letters folders past the
    /// retry budget, and returns the first remaining folder. Returns null
    /// only when 3-progress contains no folders (or all of them were
    /// dead-lettered in this call).
    /// </summary>
    private JobInfo? TryPickProgressJobOrDeadLetter()
    {
        var folders = ListProgressFoldersOldestFirst();
        foreach (var candidate in folders)
        {
            var slug = Path.GetFileName(candidate.FolderPath);
            if (candidate.Info == null)
            {
                MoveProgressOrphanToFailedPickup(candidate, slug);
                continue;
            }

            if (!AgentTypes.IsAutoPickupEligible(candidate.Info.Agent))
                continue;

            var attempts = GetPickupAttempts(slug);
            if (attempts >= PickupFailureThreshold)
            {
                var trippedDuringThisDeadLetter = DeadLetterUnrecoverableFolder(candidate, slug, attempts);
                // Cross-slug infra circuit breaker (loop-inventory:
                // pickup.cross-slug-infra-circuit-breaker). If THIS
                // dead-letter just tripped the breaker, halt the
                // iteration so the remaining 3-progress folders are NOT
                // dead-lettered too. The mode flip also short-circuits
                // the next TickAsync via the "manual" gate at the head
                // of the tick, so this guard is mid-iteration only.
                if (trippedDuringThisDeadLetter) return null;
                continue;
            }
            return candidate.Info;
        }
        return null;
    }

    private void MoveProgressOrphanToFailedPickup(ProgressPickupCandidate candidate, string slug)
    {
        // Distinguish a genuine pickup orphan from post-move cleanup debris.
        //
        // Cleanup debris happens because the Windows + ASP.NET combination
        // sometimes leaves a skeleton 3-progress folder behind after the
        // job has already moved on. The race: while ProjectRunner finishes
        // a job and JobStateMachine.MoveJob renames 3-progress/<slug> ->
        // <lane>/<slug>, another in-process writer (CliOutputLogStore on
        // cli-output.log, JobSessionLog on session-events.jsonl) may still
        // hold a Read/Write file handle on a file inside that folder.
        // Those handles are opened with FileShare.ReadWrite but NOT
        // FileShare.Delete, which is exactly the share-flag that blocks
        // the Win32 directory-rename operation from completing for the
        // locked sub-file. Directory.Move() succeeds for the rest of the
        // tree (and returns success) but a stub folder containing just
        // the still-locked file or its parent <c>logs/</c> sub-folder is
        // left behind in 3-progress. The job.json is gone (it moved with
        // the rest), so the next pickup tick walks into this method.
        //
        // From the user's point of view, calling that "failed pickup" is
        // wrong: there was no CLI spawn, no missing prompt, no broken
        // config. The job moved on cleanly; only the empty shell remained.
        // Surfacing it as a pickup failure pollutes the 3a-failed-pickup
        // lane with entries that are not actionable and obscures genuine
        // CLI spawn failures.
        //
        // Decision rule: if any post-progress lane contains a folder with
        // this slug, treat the leftover 3-progress folder as cleanup debris
        // and best-effort delete it. The job is provably elsewhere; the
        // skeleton has no claim on the kanban. If the delete fails because
        // the locking handle is still open, leave the folder and retry
        // next tick — the slug-in-post-lane check stays true, so the next
        // tick will not mis-classify it either. No orphan entry is ever
        // written for cleanup debris.
        //
        // The original genuine-orphan path (no post-progress twin) is
        // preserved verbatim below: a user who manually creates an empty
        // 3-progress/<slug>/ folder, or a hard backend crash that loses
        // job.json without moving the job on, still produces a loud
        // failed-pickup entry the operator can investigate.
        if (TryFindSlugInPostProgressLane(slug, out var locatedLane))
        {
            // ADR-0024: skeleton delete routes through the typed layer.
            // Conflict (file lock) lets the next tick retry; Rejected
            // (access denied) is logged once for the operator.
            var deleteResult = _taskAccess.DeleteLaneFolder(Entry.Path, JobStates.Progress, slug);
            if (deleteResult.Status == OrchestratorApi.Services.TaskAccess.TaskMutationStatus.Applied)
            {
                _logger.LogInformation(
                    "[taskboard] cleaned up post-move skeleton for {Slug} on {Project} (real job lives in {Lane})",
                    slug, ProjectName, locatedLane);
            }
            else if (deleteResult.Status == OrchestratorApi.Services.TaskAccess.TaskMutationStatus.Conflict)
            {
                _logger.LogDebug(
                    "[taskboard] post-move skeleton {Folder} for {Slug} still locked; will retry next tick ({Msg})",
                    candidate.FolderPath, slug, deleteResult.Message);
            }
            else if (deleteResult.Status == OrchestratorApi.Services.TaskAccess.TaskMutationStatus.Rejected)
            {
                _logger.LogWarning(
                    "[taskboard] post-move skeleton {Folder} for {Slug} cannot be deleted (access denied); manual cleanup required ({Msg})",
                    candidate.FolderPath, slug, deleteResult.Message);
            }
            _pickupAttempts.TryRemove(slug, out _);
            return;
        }

        var now = DateTime.UtcNow;
        var destinationSlug = BuildProgressOrphanSlug(slug, now,
            existsInDestination: name => _taskAccess.SlugExistsInLane(Entry.Path, JobStates.FailedPickup, name));

        var reason =
            "# Stale progress orphan\n\n" +
            $"Original folder slug: `{slug}`\n\n" +
            "The auto-pickup loop found this folder under `3-progress`, but it did not contain `job.json`. " +
            "A progress folder without application-owned metadata is not a runnable job. The folder was moved here " +
            "without counting as a CLI spawn failure, so the runner can continue with the next real job.\n";
        var moveResult = _taskAccess.MoveOrphanToFailedPickup(
            Entry.Path, JobStates.Progress, slug, destinationSlug, reason);
        if (moveResult.Status != OrchestratorApi.Services.TaskAccess.TaskMutationStatus.Applied)
        {
            _logger.LogWarning(
                "Progress orphan move refused for {Slug} on {Project}: {Status} {Message}",
                slug, ProjectName, moveResult.Status, moveResult.Message);
            return;
        }

        _pickupAttempts.TryRemove(slug, out _);
        _logger.LogWarning(
            "[taskboard] moved stale 3-progress orphan {Slug} on {Project} to {Destination}; auto-pickup will continue",
            slug, ProjectName, destinationSlug);
    }

    /// <summary>
    /// Returns true when a folder with the given slug exists in any of the
    /// lanes a job can move into after <c>3-progress</c>. Used to distinguish
    /// post-move cleanup debris from a genuine pickup orphan: if the job's
    /// real folder lives downstream, the empty shell that remained in
    /// <c>3-progress</c> is just a Windows file-handle race, not a failure
    /// the operator needs to see.
    /// </summary>
    internal bool TryFindSlugInPostProgressLane(string slug, out string lane)
    {
        foreach (var laneName in PostProgressLanes)
        {
            // ADR-0024: slug existence check goes through the typed
            // layer instead of building the lane path.
            if (_taskAccess.SlugExistsInLane(Entry.Path, laneName, slug))
            {
                lane = laneName;
                return true;
            }
        }
        lane = string.Empty;
        return false;
    }

    /// <summary>
    /// Every lane a job can land in after leaving <c>3-progress</c>. Used by
    /// <see cref="TryFindSlugInPostProgressLane"/> to decide whether a
    /// skeleton folder in <c>3-progress</c> represents post-move cleanup
    /// debris (real job is downstream) or a genuine orphan (no downstream
    /// twin). The intake / pre-progress lanes are deliberately excluded:
    /// they cannot be the move target of an in-flight job, so a slug
    /// match there does not indicate cleanup debris.
    /// </summary>
    internal static readonly string[] PostProgressLanes =
    [
        JobStates.AutoReview,
        JobStates.HumanReview,
        JobStates.Completed,
        JobStates.Archive,
    ];

    internal static string BuildProgressOrphanSlug(string slug, DateTime utcNow, Func<string, bool> existsInDestination)
    {
        var baseSlug = $"orphan-{slug}-{utcNow:yyyy-MM-dd}";
        if (!existsInDestination(baseSlug)) return baseSlug;
        var i = 2;
        while (existsInDestination($"{baseSlug}-{i}")) i++;
        return $"{baseSlug}-{i}";
    }

    /// <summary>
    /// Lists every folder under this project's <c>3-progress</c> lane,
    /// ordered oldest-first by mtime. mtime uses the same shape as
    /// <see cref="StaleProgressArchiver"/>: <c>logs/cli-output.log</c>
    /// when present, falling back to <c>job.json</c>, falling back to
    /// the directory itself; an empty folder lands at epoch 0 so it
    /// sorts to the head of the iteration.
    /// </summary>
    internal List<ProgressPickupCandidate> ListProgressFoldersOldestFirst()
    {
        // ADR-0024: enumerate 3-progress through the typed layer.
        // ListLaneFolders returns orphan folders (no job.json) too,
        // which is exactly the case the pickup loop is built around.
        var byId = _scanner.ScanAllJobs()
            .Where(j => j.ProjectName == ProjectName && j.State == JobStates.Progress)
            .ToDictionary(j => j.Id, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<ProgressPickupCandidate>();
        foreach (var laneFolder in _taskAccess.ListLaneFolders(Entry.Path, JobStates.Progress))
        {
            byId.TryGetValue(laneFolder.Slug, out var info);
            candidates.Add(new ProgressPickupCandidate(
                FolderPath: laneFolder.FolderPath,
                Slug: laneFolder.Slug,
                Info: info,
                Mtime: MeasureProgressFolderMtime(laneFolder.FolderPath)));
        }

        return OrderProgressByMtime(candidates);
    }

    /// <summary>
    /// Pure helper: orders progress-folder candidates oldest-first by mtime.
    /// Ties are broken by slug for determinism (so test fixtures with mtime
    /// pinned to the same instant still sort predictably).
    /// </summary>
    internal static List<ProgressPickupCandidate> OrderProgressByMtime(IEnumerable<ProgressPickupCandidate> candidates)
        => candidates.OrderBy(c => c.Mtime).ThenBy(c => c.Slug, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// mtime measurement matching <see cref="StaleProgressArchiver.MeasureFolder"/>:
    /// max mtime across <c>job.json</c> and every file under <c>logs/</c>
    /// (<c>cli-output.log</c>, <c>tool-calls.jsonl</c>,
    /// <c>session-events.jsonl</c>, future log types). Falls back to the
    /// directory mtime when no files exist. Folders with nothing return
    /// <see cref="DateTime.MinValue"/> so they sort to the head of the
    /// oldest-first iteration. Reading any single file misses sessions that
    /// emit primarily tool-use events while <c>cli-output.log</c> stays quiet.
    /// </summary>
    internal static DateTime MeasureProgressFolderMtime(string folder)
    {
        try
        {
            var maxStamp = DateTime.MinValue.ToUniversalTime();
            var hasAny = false;

            var logsDir = Path.Combine(folder, "logs");
            if (Directory.Exists(logsDir))
            {
                foreach (var file in Directory.EnumerateFiles(logsDir))
                {
                    try
                    {
                        var stamp = File.GetLastWriteTimeUtc(file);
                        if (stamp > maxStamp) maxStamp = stamp;
                        hasAny = true;
                    }
                    catch { /* skip unreadable files */ }
                }
            }

            var jobJson = Path.Combine(folder, "job.json");
            if (File.Exists(jobJson))
            {
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(jobJson);
                    if (stamp > maxStamp) maxStamp = stamp;
                    hasAny = true;
                }
                catch { /* skip */ }
            }

            if (hasAny) return maxStamp;
            if (Directory.Exists(folder)) return Directory.GetLastWriteTimeUtc(folder);
        }
        catch { /* best-effort: an unreadable folder sorts to the head */ }
        return DateTime.MinValue.ToUniversalTime();
    }

    /// <summary>
    /// Dead-letter a 3-progress folder whose autopickup attempts have
    /// exhausted the retry budget. ADR-0028: the destination is the visible
    /// <see cref="JobStates.FailedPickup"/> lane, not <c>7-archive</c>; pickup
    /// failures are loud, never silent. Single-state-machine authority
    /// (per-task constraint): the move goes through
    /// <see cref="JobStateMachine.MoveFolderToFailedPickup"/>, not direct file
    /// IO. Writes a <see cref="PickupFailureRecord"/> row to
    /// <c>&lt;workspace&gt;/logs/pickup-failures.jsonl</c> and clears the
    /// per-slug attempt counter.
    /// </summary>
    /// <summary>
    /// Dead-letter one over-budget 3-progress folder. Returns <c>true</c>
    /// when this dead-letter just tripped the cross-slug infra circuit
    /// breaker (caller halts the surrounding iteration); <c>false</c>
    /// otherwise (caller continues to the next candidate).
    /// </summary>
    private bool DeadLetterUnrecoverableFolder(ProgressPickupCandidate candidate, string slug, int attempts)
    {
        var now = DateTime.UtcNow;
        // ADR-0024: dead-letter slug collision check goes through the
        // typed layer; the move itself uses MoveOrphanToFailedPickup so
        // the architecture test doesn't see a Path.Combine + lane
        // construction here.
        var destinationSlug = PickupFailureLog.BuildArchiveSlug(slug, now,
            existsInDestination: name => _taskAccess.SlugExistsInLane(Entry.Path, JobStates.FailedPickup, name));

        var jobIdBeforeMove = candidate.Info?.Id ?? slug;
        var cliTypeBeforeMove = candidate.Info?.CliType;
        var historySnapshot = _pickupAttempts.TryGetValue(slug, out var state)
            ? state.History.ToList()
            : new List<PickupAttemptDiagnostic>();

        var moveResult = _taskAccess.MoveOrphanToFailedPickup(
            Entry.Path, JobStates.Progress, slug, destinationSlug, reasonMarkdown: null);
        if (moveResult.Status != OrchestratorApi.Services.TaskAccess.TaskMutationStatus.Applied)
        {
            _logger.LogWarning(
                "PickupFailureLog: failed-pickup move refused for {Slug} on {Project}: {Status} {Message}",
                slug, ProjectName, moveResult.Status, moveResult.Message);
            // Reset the counter so we don't loop on the same failed move every
            // tick. The folder stays in 3-progress; the operator can intervene.
            _pickupAttempts.TryRemove(slug, out _);
            return false;
        }

        var record = new PickupFailureRecord
        {
            At = now,
            Kind = PickupFailureKinds.PickupFailed,
            ProjectName = ProjectName,
            Slug = slug,
            JobId = jobIdBeforeMove,
            DestinationSlug = destinationSlug,
            Attempts = attempts,
            Threshold = PickupFailureThreshold,
            OutputDeadlineSeconds = PickupOutputDeadlineSeconds,
            AttemptHistory = historySnapshot.Count == 0 ? null : historySnapshot,
            Reason = $"Auto-pickup exhausted retry budget: {attempts} consecutive runs finished without producing a CLI output line within {PickupOutputDeadlineSeconds}s. Folder dead-lettered to {JobStates.FailedPickup}/{destinationSlug}."
        };
        _pickupFailures.Append(record);
        _logger.LogWarning(
            "[taskboard] dead-lettered 3-progress folder {Slug} on {Project} after {Attempts} silent autopickup attempts (-> {Destination})",
            slug, ProjectName, attempts, destinationSlug);

        // Drop a chat-log note on the moved folder so the protocol pane
        // surfaces why the lane returned to one-task-per-project. Best-effort.
        try
        {
            var moved = _scanner.FindJob(jobIdBeforeMove, Entry.Path);
            if (moved != null)
            {
                _chatLog.AppendSupervisor(moved, "pickup-failed",
                    $"Auto-pickup gave up after {attempts} consecutive silent runs (no CLI output within {PickupOutputDeadlineSeconds}s). " +
                    $"Folder surfaced in {JobStates.FailedPickup}/{destinationSlug}; the runner now considers the next 3-progress folder, then 2-ready.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PickupFailureLog: chat-log append failed for {Slug}", slug);
        }

        _pickupAttempts.TryRemove(slug, out _);

        // Cross-slug infra circuit breaker. The per-slug breaker just
        // dead-lettered ONE folder; if the same CLI now has N distinct
        // slugs dead-lettered within the rolling window, the failures are
        // infra-shaped (broken CLI binary), not task-shaped, and pickup
        // must halt across the project rather than continuing to drain
        // 2-ready one slug at a time. Single-state-machine principle:
        // the breaker raises a TripOutcome here; this method does the
        // mode flip via the same SetMode path the API uses (so existing
        // audit/event paths fire).
        return TripInfraBreakerIfNeeded(cliTypeBeforeMove, slug, jobIdBeforeMove, now);
    }

    /// <summary>
    /// Feed one spawn-failed dead-letter into
    /// <see cref="CrossSlugInfraCircuitBreaker"/> and apply the trip
    /// side-effects on the call that crosses the threshold: SetMode("manual")
    /// + plain-text supervisor chat note on the moved job folder. Returns
    /// <c>true</c> when this call just tripped the breaker.
    /// </summary>
    private bool TripInfraBreakerIfNeeded(string? cliType, string slug, string jobIdBeforeMove, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return false;
        TripOutcome? trip;
        try
        {
            trip = _infraBreaker.RecordSpawnFailedDeadLetter(ProjectName, cliType, slug, now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrossSlugInfraCircuitBreaker: record-trip failed for {Project}/{Slug}", ProjectName, slug);
            return false;
        }
        if (trip == null) return false;

        _logger.LogWarning(
            "[taskboard] cross-slug infra breaker tripped for {Project} on {Cli} ({Slugs}); switching mode to manual",
            ProjectName, cliType, string.Join(", ", trip.Slugs));

        // Plain-text supervisor chat note on the freshly-moved dead-letter so
        // the project chat surface shows one banner-shaped entry (not per-tick
        // spam). The supervisor stream tag is the same channel ChatNoteHosted
        // and the per-slug breaker use, so the activity-log renders it as a
        // separate participant.
        try
        {
            var moved = _scanner.FindJob(jobIdBeforeMove, Entry.Path);
            if (moved != null)
            {
                _chatLog.AppendSupervisor(moved, "infra-halt", trip.BuildSupervisorChatMessage());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrossSlugInfraCircuitBreaker: chat-log append failed for {Slug}", slug);
        }

        // Mode flip last so any throw above does not leave us paused
        // without the banner. SetMode routes through OnModePersist so the
        // backend-restart-safe persistence in ProjectSettingsService fires.
        //
        // Halt-iteration signal: only true when we actually transitioned
        // out of an auto mode. If the runner was already manual or paused
        // (e.g. test reflection invoking the picker against a paused
        // runner), the breaker still records its row and notes the chat,
        // but does not block the caller's iteration - the auto-pickup
        // cascade we're protecting against can only happen when auto
        // mode is what's driving the picker.
        if (IsAutoMode(_mode))
        {
            SetMode("manual",
                $"cross-slug infra circuit-breaker on {cliType}: {string.Join(", ", trip.Slugs)}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Records a per-attempt diagnostic against the per-slug attempt counter
    /// for an autopickup against a 3-progress folder. A "silent" attempt
    /// (zero output lines) increments the counter; a productive attempt
    /// (any output streamed) resets the counter to zero so a flaky single
    /// run does not wedge a productive folder. Called from
    /// <see cref="OnCliFinishedAsync"/>.
    /// </summary>
    private void RecordPickupAttemptResult(string slug, int outputLines, double durationSeconds, string? executionStatus)
    {
        if (outputLines > 0)
        {
            // Productive attempt: drop the slug from the counter so the next
            // failure starts a fresh streak instead of inheriting an old one.
            _pickupAttempts.TryRemove(slug, out _);
            // Cross-slug infra breaker reset: ≥ 1 streamed CLI output line
            // means the infra is healthy again for this CLI. Clear the
            // distinct-slug counter so a future single bad job does not
            // ride on top of a stale cascade.
            try { _infraBreaker.OnProductivePickup(ProjectName, _activeCliType); }
            catch (Exception ex) { _logger.LogDebug(ex, "CrossSlugInfraCircuitBreaker reset failed for {Project}", ProjectName); }
            return;
        }

        var state = _pickupAttempts.GetOrAdd(slug, _ => new PickupAttemptState());
        state.Count++;
        state.History.Enqueue(new PickupAttemptDiagnostic
        {
            At = DateTime.UtcNow,
            DurationSeconds = durationSeconds,
            OutputLines = outputLines,
            ExecutionStatus = executionStatus
        });
        // Bound the history at the threshold so the JSONL row stays compact
        // and the in-memory state does not grow unboundedly when a move keeps
        // refusing.
        while (state.History.Count > PickupFailureThreshold) state.History.Dequeue();
    }

    /// <summary>
    /// Pure decision: should the just-finished run trigger a session-chain
    /// recovery marker? True when the run was a <c>--resume</c> attempt
    /// (planner produced a resume plan with a real session id) AND the
    /// CLI did not capture a usable session id back. Pulled out as a
    /// helper so the field-snapshot pattern protecting it from
    /// concurrency races (the prior bug class that drove the 31-run
    /// arhciv loop) is directly testable.
    /// </summary>
    internal static bool ShouldMarkSessionChainRecovery(RunPlan? planSnapshot) =>
        planSnapshot?.ResumeFlag == true
        && !string.IsNullOrWhiteSpace(planSnapshot.SessionToResume);

    private List<string> GetQueuedJobIds()
    {
        return _scanner.ScanAllJobs()
            .Where(j => j.ProjectName == ProjectName && j.State == JobStates.Ready)
            .OrderBy(j => j.Order)
            .Select(j => j.Id)
            .ToList();
    }

    private void NotifyStatus()
    {
        // Defense-in-depth: NotifyStatus is called from many points inside
        // the pickup tick. A throwing subscriber would escape the tick,
        // exit ExecuteAsync, and stop the host. The TaskRunnerService
        // wrapper around the subscriber chain is the primary guard; this
        // catch is the second line so any future direct subscriber added
        // to ProjectRunner.OnStatusChanged stays contained.
        try { OnStatusChanged?.Invoke(GetStatus()); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnStatusChanged subscriber threw for {Project}", ProjectName); }
    }

    /// <summary>
    /// HEAD-SHA capture wrapper. Swallows git failures - missing repo,
    /// missing tool, transient errors - so a flaky environment can't
    /// take down a run. The persisted SHA stays null on failure and the
    /// commits endpoint falls back to the wall-clock window.
    /// </summary>
    private string? SafeGetHeadSha(string jobId)
    {
        try { return _git.GetHeadSha(jobId, Entry.Path); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HEAD SHA capture failed for {JobId}", jobId);
            return null;
        }
    }

    /// <summary>
    /// Per-tick continuous decision review. Scans the active job's live
    /// CLI output buffer for the latest unresolved interruptive sentinel
    /// (<c>[[TASK_NEEDS_INPUT]]</c>, <c>[[TASK_BLOCKED]]</c>) and updates
    /// <see cref="_activePendingDecision"/>. Cheap: one regex pass over
    /// the buffer's tail. Same-state ticks are silent. See ADR-0027.
    /// </summary>
    private void TickPendingDecision()
    {
        var jobId = _activeJobId;
        var cliType = _activeCliType;
        if (jobId == null || cliType == null)
        {
            ClearPendingDecisionIfPresent();
            return;
        }

        ICliExecutionService cli;
        try { cli = _router.Get(cliType); }
        catch
        {
            ClearPendingDecisionIfPresent();
            return;
        }

        var jobKey = JobIdentity.CreateKey(Entry.Path, jobId);
        List<CliOutputLine> output;
        try { output = cli.GetOutput(jobKey); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pending-decision scan: GetOutput failed for {JobId}", jobId);
            return;
        }

        var hit = PendingDecisionScanner.Scan(output);
        lock (_pendingDecisionLock)
        {
            if (hit == null)
            {
                if (_activePendingDecision != null)
                {
                    _logger.LogInformation(
                        "[taskboard] pending decision cleared for {JobId} on {Project}",
                        jobId, ProjectName);
                }
                _activePendingDecision = null;
                return;
            }

            // Same job, same line -> already known, nothing to log.
            if (_activePendingDecision != null
                && _activePendingDecision.JobId == jobId
                && _activePendingDecision.Decision.LineIndex == hit.LineIndex
                && _activePendingDecision.Decision.Kind == hit.Kind)
            {
                return;
            }

            string? title = null;
            try { title = _scanner.FindJob(jobId, Entry.Path)?.Title; } catch { /* best-effort */ }
            _activePendingDecision = new PendingDecisionEntry(jobId, title ?? jobId, hit);
            _logger.LogInformation(
                "[taskboard] pending decision detected for {JobId} on {Project}: kind={Kind} reason={Reason}",
                jobId, ProjectName, hit.Kind, hit.Reason ?? "<none>");
        }
    }

    private void ClearPendingDecisionIfPresent()
    {
        lock (_pendingDecisionLock)
        {
            if (_activePendingDecision == null) return;
            _activePendingDecision = null;
        }
    }

    /// <summary>
    /// Returns the active unresolved decision sentinel(s) for this project.
    /// At most one entry today (only one job runs in 3-progress per project,
    /// per ADR-0001), but the surface is shaped as a list so a future
    /// orchestrator advisory can join the same banner without an API break.
    /// </summary>
    public IReadOnlyList<PendingDecisionEntry> GetPendingDecisions()
    {
        lock (_pendingDecisionLock)
        {
            return _activePendingDecision == null
                ? Array.Empty<PendingDecisionEntry>()
                : new[] { _activePendingDecision };
        }
    }
}

/// <summary>
/// One pending decision currently surfaced on a project. The job-level
/// metadata (id, title) is captured at detection time so the read API can
/// shape a banner without a follow-up scanner call.
/// </summary>
public sealed record PendingDecisionEntry(
    string JobId,
    string Title,
    PendingDecision Decision);
