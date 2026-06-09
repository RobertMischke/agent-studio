using System.Collections.Concurrent;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Quota;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Services;

/// <summary>
/// DI-singleton background service that owns one <see cref="ProjectRunner"/>
/// per watched workspace, ticks them on a 5 s loop for auto-pickup, and
/// fans out the public start / stop / continue / status API used by the
/// HTTP endpoints. The per-project lifecycle and the per-run decision tree
/// live in <see cref="OrchestratorApi.Services.Runner"/>; this class is
/// intentionally kept to routing + cross-project orchestration so the three
/// concerns can be read independently.
/// </summary>
public class TaskRunnerService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TaskRunnerService> _logger;
    private readonly TaskScannerService _scanner;
    private readonly TaskStateMachine _states;
    private readonly TaskMutationService _mutations;
    private readonly TaskSessionLog _sessions;
    private readonly CopilotCliService _cli;
    private readonly CliRouter _router;
    private readonly ContextUsageParser _contextUsageParser;
    private readonly SummaryGenerationService _summaryService;
    private readonly RuntimePromptService _prompts;
    private readonly TaskTransitionService _transitions;
    private readonly ProjectSettingsService _projectSettings;
    private readonly QuotaService _quotaService;
    private readonly CliQuotaCapsService _quotaCaps;
    private readonly OrchestratorChatLog _chatLog;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly OrchestratorRunner _orchestratorRunner;
    private readonly OrchestratorSessionStore _orchestratorSessions;
    private readonly GlobalOrchestratorBootstrap _globalOrchestrator;
    private readonly PickupFailureLog _pickupFailures;
    private readonly CrossSlugInfraCircuitBreaker _infraBreaker;
    // Forwarded to each ProjectRunner. DI injects the registered singleton even
    // into this optional slot; null only when a test fixture builds the service
    // directly, in which case ProjectRunner builds its own workspace-less fallback.
    private readonly HumanReviewEscalation? _humanReviewEscalation;
    private readonly AgentMessageBusBridge? _bus;
    private readonly OrchestratorApi.Services.TaskAccess.ITaskAccess _taskAccess;
    private readonly PickupLockFile? _pickupLock;
    private readonly IntegrationLeaseService? _integrationLeases;
    private readonly TimelineLog? _timeline;
    private readonly OrchestratorApi.Services.Pipeline.PipelineExecutionLog? _pipelineLog;
    // Forwarded to each ProjectRunner. DI injects the registered singleton; the
    // step is default-OFF per project, so a wired-but-disabled step changes
    // nothing. Null only when a test fixture builds the service directly.
    private readonly OrchestratorApi.Services.Runner.PostAbortReviewStepService? _postAbortReview;
    // Forwarded to each ProjectRunner for post-hoc Claude token reconstruction
    // from session transcripts. DI injects the registered singleton; null only
    // when a test fixture builds the service directly.
    private readonly OrchestratorApi.Services.Cli.ClaudeSessionInspector? _sessionInspector;
    private readonly ConcurrentDictionary<string, ProjectRunner> _runners = new();

    /// <summary>
    /// Process-wide pickup role (ADR-0044). Resolved once at construction
    /// from <c>Runner:Role</c> with a fallback to <c>Environment:IsDev</c>.
    /// Surfaced so endpoints can echo it back in the runner status.
    /// </summary>
    public RunnerRole Role { get; }

    /// <summary>
    /// Logical name of this backend instance for cross-process lock attribution.
    /// Resolved from <c>Runner:BackendName</c> with a fallback derived from
    /// <c>Environment:IsDev</c> ("dev" vs "stable") so the two checkouts under
    /// <c>agent-taskboard-devspace/</c> produce distinct lock owners even when
    /// the operator forgets to set the explicit key.
    /// </summary>
    public string BackendName { get; }

    public event Action<string, ProjectRunnerStatus>? OnRunnerStatusChanged;

    public TaskRunnerService(
        IConfiguration config,
        ILogger<TaskRunnerService> logger,
        TaskScannerService scanner,
        TaskStateMachine states,
        TaskMutationService mutations,
        TaskSessionLog sessions,
        CopilotCliService cli,
        CliRouter router,
        ContextUsageParser contextUsageParser,
        SummaryGenerationService summaryService,
        RuntimePromptService prompts,
        TaskTransitionService transitions,
        ProjectSettingsService projectSettings,
        QuotaService quotaService,
        CliQuotaCapsService quotaCaps,
        OrchestratorChatLog chatLog,
        OrchestratorLog orchestratorLog,
        OrchestratorRunner orchestratorRunner,
        OrchestratorSessionStore orchestratorSessions,
        GlobalOrchestratorBootstrap globalOrchestrator,
        GitService git,
        PickupFailureLog pickupFailures,
        CrossSlugInfraCircuitBreaker infraBreaker,
        OrchestratorApi.Services.TaskAccess.ITaskAccess taskAccess,
        AgentMessageBusBridge? bus = null,
        PickupLockFile? pickupLock = null,
        IntegrationLeaseService? integrationLeases = null,
        TimelineLog? timeline = null,
        OrchestratorApi.Services.Pipeline.PipelineExecutionLog? pipelineLog = null,
        HumanReviewEscalation? humanReviewEscalation = null,
        OrchestratorApi.Services.Runner.PostAbortReviewStepService? postAbortReview = null,
        OrchestratorApi.Services.Cli.ClaudeSessionInspector? sessionInspector = null)
    {
        _config = config;
        _logger = logger;
        _scanner = scanner;
        _states = states;
        _mutations = mutations;
        _sessions = sessions;
        _cli = cli;
        _router = router;
        _contextUsageParser = contextUsageParser;
        _summaryService = summaryService;
        _prompts = prompts;
        _transitions = transitions;
        _projectSettings = projectSettings;
        _quotaService = quotaService;
        _quotaCaps = quotaCaps;
        _chatLog = chatLog;
        _orchestratorLog = orchestratorLog;
        _orchestratorRunner = orchestratorRunner;
        _orchestratorSessions = orchestratorSessions;
        _globalOrchestrator = globalOrchestrator;
        _git = git;
        _pickupFailures = pickupFailures;
        _infraBreaker = infraBreaker;
        _humanReviewEscalation = humanReviewEscalation;
        _taskAccess = taskAccess;
        _bus = bus;
        _pickupLock = pickupLock;
        _integrationLeases = integrationLeases;
        _timeline = timeline;
        _pipelineLog = pipelineLog;
        _postAbortReview = postAbortReview;
        _sessionInspector = sessionInspector;

        Role = RunnerRoles.ResolveFromConfig(_config);
        BackendName = ResolveBackendName(_config);
        _logger.LogInformation(
            "TaskRunnerService booting with role={Role} backend={Backend} (Runner:Role={Configured}, Environment:IsDev={IsDev})",
            RunnerRoles.Format(Role),
            BackendName,
            _config["Runner:Role"] ?? "<unset>",
            _config.GetValue<bool>("Environment:IsDev"));
        if (Role == RunnerRole.TestSubject)
        {
            _logger.LogWarning(
                "[runner-role] Auto-pickup is structurally DISABLED for this backend (role=test-subject). " +
                "Explicit /api/tasks/{{id}}/start calls still execute (Playwright fixtures, manual debugging). " +
                "See ADR-0044 and AGENTS.md 'Dev backend lifecycle'.");
        }
    }

    /// <summary>
    /// Picks the backend identity stamped onto pickup-lock files. Explicit
    /// <c>Runner:BackendName</c> wins; when unset, dev/stable is inferred from
    /// <c>Environment:IsDev</c> so the two checkouts produce distinct owners.
    /// </summary>
    private static string ResolveBackendName(IConfiguration cfg)
    {
        var explicitName = cfg["Runner:BackendName"];
        if (!string.IsNullOrWhiteSpace(explicitName)) return explicitName.Trim();
        var isDev = cfg.GetValue<bool>("Environment:IsDev");
        return isDev ? "dev" : "stable";
    }

    /// <summary>
    /// Stamps a fresh <see cref="PickupLockOwner"/> for the given project,
    /// carrying pid, hostname, role, and backend name so a foreign lock writer
    /// can be identified later. The owner is per-project but the rest of the
    /// fields are process-wide.
    /// </summary>
    private PickupLockOwner BuildPickupLockOwner(string projectName) => new()
    {
        Pid = System.Environment.ProcessId,
        Hostname = System.Environment.MachineName,
        Role = RunnerRoles.Format(Role),
        BackendName = BackendName,
        BackendPort = ResolveBackendPort(_config),
        ProjectName = projectName
    };

    private static int ResolveBackendPort(IConfiguration cfg)
    {
        var raw = cfg["Urls"] ?? cfg["ASPNETCORE_URLS"] ?? System.Environment.GetEnvironmentVariable("PORT");
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        foreach (var token in raw.Split([';', ' '], System.StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, out var p)) return p;
            var colon = token.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(token[(colon + 1)..].TrimEnd('/'), out var p2)) return p2;
        }
        return 0;
    }

    private readonly GitService _git;

    public SummaryGenerationService SummaryService => _summaryService;

    public CliRouter Router => _router;

    /// <summary>
    /// The active <see cref="StuckLoopBudget"/> read from configuration at
    /// startup. Surfaced so endpoints can label the auto-loop snapshot
    /// they return with the actual ceilings the runner is enforcing.
    /// </summary>
    public StuckLoopBudget StuckLoopBudget => _stuckLoopBudget;
    private StuckLoopBudget _stuckLoopBudget = StuckLoopBudget.Default;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initialize runners for each watch path
        var entries = _scanner.GetWatchPaths();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.RootPath))
            {
                _logger.LogWarning("WatchPath '{Name}' has no RootPath configured, skipping runner", entry.Name);
                continue;
            }

            if (!Directory.Exists(entry.RootPath))
            {
                _logger.LogWarning("RootPath '{RootPath}' for '{Name}' does not exist, skipping runner", entry.RootPath, entry.Name);
                continue;
            }

            var runner = new ProjectRunner(
                entry.Name, entry, _logger, _scanner, _states, _sessions, _router, _summaryService,
                _prompts, _transitions, _chatLog, _mutations, _orchestratorLog, _orchestratorRunner,
                _orchestratorSessions, _projectSettings, _quotaService, _quotaCaps, _git,
                _pickupFailures, _infraBreaker, _taskAccess, _bus,
                role: Role,
                pickupLock: _pickupLock,
                pickupLockOwner: BuildPickupLockOwner(entry.Name),
                integrationLeases: _integrationLeases,
                timeline: _timeline,
                pipelineLog: _pipelineLog,
                humanReviewEscalation: _humanReviewEscalation,
                postAbortReview: _postAbortReview,
                sessionInspector: _sessionInspector);
            runner.ConfigureWatchdog(LoadWatchdogConfig(_config), PhaseBudgetTable.FromConfig(_config));
            runner.ConfigureCircuitBreaker(RunnerCircuitBreakerOptions.FromConfig(_config));
            _stuckLoopBudget = LoadStuckLoopBudget(_config);
            runner.ConfigureStuckLoopBudget(_stuckLoopBudget);
            runner.OnStatusChanged += status =>
            {
                // A throwing subscriber here (typically the SignalR fan-out in
                // Program.cs raising synchronously when the hub system is in
                // a transitional state) used to bubble back up through
                // ProjectRunner.NotifyStatus into the pickup tick, escape
                // ExecuteAsync, and stop the host (BackgroundServiceExceptionBehavior
                // defaults to StopHost). Catch and log instead.
                try { OnRunnerStatusChanged?.Invoke(entry.Name, status); }
                catch (Exception ex) { _logger.LogWarning(ex, "OnRunnerStatusChanged subscriber threw for {Project}", entry.Name); }
            };
            // Persist every mode change so the auto-pickup toggle survives
            // backend restarts. Includes implicit transitions like "auto-single
            // → manual" after a job completes.
            runner.OnModePersist += mode => _projectSettings.SetRunnerMode(entry.Name, mode);
            _runners[entry.Name] = runner;

            // Restore last saved mode (if any) after wiring the persist hook so
            // the restore itself is idempotent and doesn't double-write.
            var savedMode = _projectSettings.Get(entry.Name).RunnerMode;
            if (!string.IsNullOrWhiteSpace(savedMode) && savedMode != "manual")
            {
                runner.RestoreMode(savedMode!);
                _logger.LogInformation("Restored runner mode '{Mode}' for project '{Name}'", savedMode, entry.Name);
            }
            _logger.LogInformation("Initialized runner for project '{Name}' (Root: {RootPath})", entry.Name, entry.RootPath);
        }

        // Check CLI availability
        if (!_cli.IsAvailable())
        {
            _logger.LogWarning("Copilot CLI not available - runners will be in manual/board-only mode");
        }

        // Boot the orchestrator's long-lived Claude session per project so
        // it has warm context (project README, recent activity, lane
        // counts) ready for auto-mode decisions. Cheap when a session is
        // already on disk - we only re-boot if the persisted file is
        // missing. Fire-and-forget per project so a slow boot doesn't
        // block app startup; a project whose boot fails is logged and
        // the orchestrator falls back to one-shot calls on first use.
        foreach (var runner in _runners.Values)
        {
            var snapshot = runner;
            _ = Task.Run(async () =>
            {
                try { await snapshot.BootOrchestratorSessionAsync(stoppingToken); }
                catch (Exception ex) { _logger.LogWarning(ex, "Orchestrator boot failed for {Project}", snapshot.ProjectName); }
            }, stoppingToken);
        }

        // Boot the global orchestrator. Sits above the per-project ones; one
        // session for cross-project decisions, persisted under TaskRepository
        // so it survives restarts. Fire-and-forget for the same reason as
        // the per-project boots: a slow boot must not block app startup.
        _ = Task.Run(async () =>
        {
            try { await _globalOrchestrator.BootAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Global orchestrator boot failed"); }
        }, stoppingToken);

        // Run the loop - poll every 5 seconds for auto-mode runners.
        // Per-tick try/catch is the load-bearing safety net: an unhandled
        // exception escaping ExecuteAsync stops the host
        // (BackgroundServiceExceptionBehavior defaults to StopHost). One bad
        // tick must not take down the API for every other project.
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var runner in _runners.Values)
            {
                if (stoppingToken.IsCancellationRequested) break;
                try
                {
                    // Quota cap watchdog: if the active job's CLI has gone past
                    // the configured cap since it started (e.g. usage rolled
                    // over after a probe refresh), terminate it. We do this
                    // before the pickup tick so the slot frees up immediately.
                    var capStop = runner.EnforceQuotaCapsOnActiveJob(RunStopReason.QuotaCapExceeded);
                    if (capStop.Blocked)
                    {
                        _logger.LogInformation(
                            "[taskboard] cap watchdog stopped active job for {Project}: {Reason}",
                            runner.ProjectName, capStop.DescribeReason());
                    }

                    await runner.TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Pickup tick failed for project {Project}; continuing", runner.ProjectName);
                }
            }

            try { await Task.Delay(5000, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public RunnerStatus GetStatus()
    {
        var projects = new Dictionary<string, ProjectRunnerStatus>();
        foreach (var (name, runner) in _runners)
        {
            projects[name] = runner.GetStatus();
        }
        return new RunnerStatus { Projects = projects };
    }

    public bool SetMode(string projectName, string mode, string? reason = null)
    {
        var result = RequestModeChange(projectName, mode, reason);
        return result != null && result.Outcome != ModeChangeOutcome.Invalid;
    }

    /// <summary>
    /// Typed mode-change entry point (ADR-0044). Returns the structured
    /// outcome so <c>PUT /api/runner/{project}/mode</c> can answer with
    /// <c>{applied|deferred, mode, pendingMode, willApplyAfter}</c> instead
    /// of an opaque 200/400. Null means "unknown project" (the endpoint
    /// returns 404 in that case). <see cref="ModeChangeOutcome.Invalid"/>
    /// means the mode string was not one of the four allowed values; the
    /// endpoint converts that to 400.
    /// </summary>
    /// <remarks>
    /// The deferred branch (manual / paused requested while a job is
    /// running) is the load-bearing change from the original
    /// <c>SetMode</c>: previously the call returned 200 unconditionally and
    /// the operator had no way to tell that an active job was still going
    /// to run to completion before the mode flip "took". The deferred slot
    /// surfaces via <see cref="ProjectRunnerStatus.PendingMode"/> and the
    /// API response so the lane pill can render "MANUAL (after current)"
    /// while the runner finishes the job in flight.
    /// </remarks>
    public ModeChangeResult? RequestModeChange(string projectName, string mode, string? reason = null)
    {
        if (!_runners.TryGetValue(projectName, out var runner)) return null;
        var validModes = new[] { "manual", "auto-single", "auto-continuous", "paused" };
        if (!validModes.Contains(mode))
            return new ModeChangeResult(ModeChangeOutcome.Invalid, runner.GetStatus().Mode, null, null);
        // Cross-slug infra breaker reset: when the operator flips back to
        // an auto mode, treat that as "infra is fixed, run again" and clear
        // the distinct-slug counter across all CLIs for this project. The
        // breaker drives the auto -> manual transition itself, but it never
        // sets an auto-* mode, so this hook reliably distinguishes operator
        // intent from runner-internal mode flips.
        if (mode is "auto-single" or "auto-continuous")
        {
            try { _infraBreaker.OnOperatorResumeAuto(projectName); }
            catch (Exception ex) { _logger.LogWarning(ex, "CrossSlugInfraCircuitBreaker reset failed for {Project}", projectName); }
        }
        var effectiveReason = string.IsNullOrWhiteSpace(reason) ? "api: PUT /api/runner/{project}/mode" : reason;
        return runner.RequestModeChange(mode, effectiveReason);
    }

    /// <summary>
    /// Continuous-decision read surface (ADR-0027): returns the live
    /// unresolved interruptive sentinel(s) the named project's running job
    /// has emitted, or null when the project is not configured. Empty list
    /// means "no decision is currently pending".
    /// </summary>
    public IReadOnlyList<PendingDecisionEntry>? GetPendingDecisions(string projectName)
    {
        if (!_runners.TryGetValue(projectName, out var runner)) return null;
        return runner.GetPendingDecisions();
    }

    public async Task<ContinueJobResponse> StartJobAsync(string jobId, string? watchPath = null, string? modelOverride = null, string? cliTypeOverride = null, string? thinkingLevelOverride = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) throw new TaskOperationException("Job not found", 404);

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) throw new TaskOperationException($"No runner configured for project '{info.ProjectName}' - check RootPath in WatchPaths config", 400);

        // Persist CLI type override before validity check so the next iteration picks it up.
        if (!string.IsNullOrWhiteSpace(cliTypeOverride) && cliTypeOverride != info.CliType)
        {
            _mutations.SetJobCliType(jobId, CliTypes.Normalize(cliTypeOverride), watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) throw new TaskOperationException($"{cli.CliType} CLI is not installed or not on PATH", 400);

        // Persist override on the job so subsequent runs reuse it
        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        if (thinkingLevelOverride is not null && thinkingLevelOverride != info.ThinkingLevel)
        {
            _mutations.SetJobThinkingLevel(jobId, thinkingLevelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var outcome = await runner.StartJobManualAsync(jobId, ct);
        return ShapeOutcome(outcome, info, jobId, watchPath, mode: ContinueModes.Continue, prompt: string.Empty);
    }

    /// <summary>
    /// Resumes a job's CLI session and feeds it a follow-up prompt. Recovery
    /// fallback: when no session is recorded (or the recorded id is incompatible
    /// with the current CLI), <see cref="ProjectRunner.ContinueJobAsync"/>
    /// switches to a fresh-session run with a recovery prompt that instructs
    /// the agent to reconstruct context from the job folder. The user gets the
    /// continuation they asked for instead of a 400 - at the cost of conversation
    /// memory that wasn't already on disk.
    /// </summary>
    public async Task<ContinueJobResponse> ContinueJobAsync(string jobId, string followupPrompt, string? watchPath = null, string? modelOverride = null, string? cliTypeOverride = null, string? thinkingLevelOverride = null, string? mode = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) throw new TaskOperationException("Job not found", 404);

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) throw new TaskOperationException($"No runner configured for project '{info.ProjectName}'", 400);

        if (!string.IsNullOrWhiteSpace(cliTypeOverride) && cliTypeOverride != info.CliType)
        {
            _mutations.SetJobCliType(jobId, CliTypes.Normalize(cliTypeOverride), watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) throw new TaskOperationException($"{cli.CliType} CLI is not installed or not on PATH", 400);

        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        if (thinkingLevelOverride is not null && thinkingLevelOverride != info.ThinkingLevel)
        {
            _mutations.SetJobThinkingLevel(jobId, thinkingLevelOverride, watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var normalizedMode = ContinueModes.Normalize(mode);

        // Extend mode: persist the new prompt as its own prompt-N.md so the
        // task description grows blog-style. The agent still receives just
        // the new prompt (the runner handles framing); the historical files
        // are evidence and a renderable timeline for the UI.
        if (normalizedMode == ContinueModes.Extend)
        {
            try
            {
                var nextIndex = NextPromptHistoryIndex(info.FolderPath);
                var promptFile = Path.Combine(info.FolderPath, $"prompt-{nextIndex}.md");
                await File.WriteAllTextAsync(promptFile, followupPrompt.TrimEnd() + Environment.NewLine, System.Text.Encoding.UTF8, ct);
                _logger.LogInformation("Extend mode: wrote {File} for job {JobId}", Path.GetFileName(promptFile), jobId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write extend prompt history for {JobId}", jobId);
            }
        }

        _mutations.AppendContinuationNote(jobId, followupPrompt, watchPath);
        AppendUserPromptToCliLog(info, followupPrompt);

        // Mirror the user follow-up onto the bus. cli-output.log remains the
        // canonical transcript (the activity-log parser reads it); the bus
        // event is the typed projection that downstream messages chain onto
        // via correlationId.
        try { _ = _bus?.EmitUserPromptAsync(info, followupPrompt, normalizedMode); }
        catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of user prompt failed for {JobId}", jobId); }

        var outcome = await runner.ContinueJobAsync(jobId, followupPrompt, normalizedMode, ct);
        return ShapeOutcome(outcome, info, jobId, watchPath, normalizedMode, followupPrompt);
    }

    /// <summary>
    /// Translates a <see cref="RunOutcome"/> from <see cref="ProjectRunner"/>
    /// into a <see cref="ContinueJobResponse"/> for the HTTP layer. The
    /// busy-project case is the load-bearing branch: the user's intent gets
    /// saved as a draft on the target job, the job is promoted to the top of
    /// <c>2-ready</c>, the chat receives an orchestrator <c>[queued]</c>
    /// meta line, and the response is shaped as <c>status: "queued"</c>
    /// (the endpoint returns 202). Other rejection reasons surface as
    /// <see cref="TaskOperationException"/>.
    /// </summary>
    private ContinueJobResponse ShapeOutcome(
        RunOutcome outcome,
        TaskInfo info,
        string jobId,
        string? watchPath,
        string mode,
        string prompt)
    {
        if (outcome.Execution != null)
        {
            return new ContinueJobResponse { Status = "started", Execution = outcome.Execution };
        }

        var rej = outcome.Rejection ?? new RunRejection(RunRejectReason.None, "unknown");

        if (rej.Reason == RunRejectReason.ProjectBusy)
        {
            // Save user intent + promote the target job to top of 2-ready,
            // then post the orchestrator meta line into the chat. From the
            // user's seat, the modal disappears and the chat reflects the
            // queued state.
            var savedIntent = _mutations.SavePendingIntent(
                jobId, mode, prompt,
                reason: "project-busy",
                activeJobId: rej.BusyJobId,
                watchPath: watchPath);

            var fromState = info.State;
            var position = _states.PromoteToReadyTop(jobId, watchPath);

            try
            {
                var refreshed = _scanner.FindJob(jobId, watchPath) ?? info;
                var summary = $"Saved follow-up for \"{refreshed.Title}\"; project busy with \"{rej.BusyJobTitle ?? rej.BusyJobId ?? "another task"}\". Promoted from {fromState} to 2-ready (position {position}).";
                _chatLog.Append(refreshed, OrchestratorMessageKind.Decision,
                    $"[queued] Saved your follow-up. Project busy with \"{rej.BusyJobTitle ?? rej.BusyJobId ?? "another task"}\"; this task moved from {fromState} to 2-ready (position {position}). Will run on next auto-pickup.");

                _orchestratorLog.Append(refreshed.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Decision,
                    Topic = OrchestratorLogTopics.TaskQueued,
                    JobId = jobId,
                    Summary = summary,
                    Reasoning = $"User sent a {mode} follow-up while the project was running another job. " +
                                $"Saved the prompt as pending-intent.json on the target task and promoted the task to top of 2-ready " +
                                $"so the auto-pickup loop runs it on the next tick. Active job at the time: {rej.BusyJobId}."
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write [queued] meta for {JobId}", jobId);
            }

            return new ContinueJobResponse
            {
                Status = "queued",
                Queued = new ContinueJobQueuedInfo
                {
                    Reason = "project-busy",
                    ActiveJobId = rej.BusyJobId,
                    ActiveJobTitle = rej.BusyJobTitle,
                    Position = position > 0 ? position : 1,
                    PromotedFromState = fromState
                }
            };
        }

        if (rej.Reason == RunRejectReason.TaskNotFound)
            throw new TaskOperationException(rej.Message ?? "Job not found", 404);

        if (rej.Reason == RunRejectReason.QuotaCapExceeded)
            throw new TaskOperationException(rej.Message ?? "Quota cap exceeded", 429);

        // CliUnavailable, None, or anything else: 400 with the message.
        throw new TaskOperationException(rej.Message ?? "Cannot start job", 400);
    }

    /// <summary>
    /// Loads watchdog thresholds from <c>Watchdog:*</c> in configuration.
    /// Falls back to <see cref="WatchdogConfig.Default"/> when nothing is
    /// set. Per-CLI overrides are not yet read here; we apply one config
    /// per project runner today.
    /// </summary>
    /// <summary>
    /// Loads <see cref="StuckLoopBudget"/> from <c>StuckLoop:*</c> configuration.
    /// Falls back to <see cref="StuckLoopBudget.Default"/> when nothing is set.
    /// Defaults are deliberately generous - the user can tighten via
    /// appsettings if their CLI quota is small.
    /// </summary>
    private static StuckLoopBudget LoadStuckLoopBudget(IConfiguration cfg)
    {
        var section = cfg.GetSection("StuckLoop");
        var d = StuckLoopBudget.Default;
        if (!section.Exists()) return d;
        return new StuckLoopBudget(
            MaxIterations:          section.GetValue("MaxIterations", d.MaxIterations),
            MaxOrchestratorTokens:  section.GetValue<long>("MaxOrchestratorTokens", d.MaxOrchestratorTokens));
    }

    /// <summary>
    /// Snapshot of the auto-loop state for one job (null when no loop is in
    /// flight). The caller passes the project name directly so the lookup
    /// is O(1) - the previous variant accepted only <c>watchPath</c> and
    /// resolved the project via <see cref="TaskScannerService.FindJob"/>,
    /// which performs a full disk rescan per call. With the runtime
    /// overlay applied to every TaskInfo, that turned <c>/api/tasks</c> and
    /// <c>/api/tasks/grouped</c> into O(N^2) disk reads (~7-15 s on a
    /// 150-job board) and froze the polling UI. Locked by
    /// <c>JobsEndpointPerfTests.WithRuntime_Over200Jobs_FinishesWellUnderOneSecond</c>.
    /// </summary>
    public StuckLoopState? GetStuckLoopStateForJob(string jobId, string projectName)
    {
        if (string.IsNullOrEmpty(projectName)) return null;
        return _runners.TryGetValue(projectName, out var runner)
            ? runner.GetStuckLoopState(jobId)
            : null;
    }

    private static WatchdogConfig LoadWatchdogConfig(IConfiguration cfg)
    {
        var section = cfg.GetSection("Watchdog");
        if (!section.Exists()) return WatchdogConfig.Default;
        var d = WatchdogConfig.Default;
        return new WatchdogConfig(
            Enabled:              section.GetValue("Enabled", d.Enabled),
            WarmUpGraceSeconds:   section.GetValue("WarmUpGraceSeconds", d.WarmUpGraceSeconds),
            QuietSeconds:         section.GetValue("QuietSeconds", d.QuietSeconds),
            SuspiciousSeconds:    section.GetValue("SuspiciousSeconds", d.SuspiciousSeconds),
            HungSeconds:          section.GetValue("HungSeconds", d.HungSeconds),
            TickIntervalSeconds:  section.GetValue("TickIntervalSeconds", d.TickIntervalSeconds));
    }

    /// <summary>
    /// Counts existing <c>prompt-N.md</c> files in the job folder and returns
    /// the next free index (1-based). The original task lives in
    /// <c>prompt.md</c>; extensions land as <c>prompt-1.md</c>,
    /// <c>prompt-2.md</c>, ... so the timeline is obvious from a directory
    /// listing alone.
    /// </summary>
    private static int NextPromptHistoryIndex(string jobFolder)
    {
        if (!Directory.Exists(jobFolder)) return 1;
        var max = 0;
        foreach (var path in Directory.EnumerateFiles(jobFolder, "prompt-*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            // name = "prompt-3"
            var dash = name.IndexOf('-');
            if (dash < 0 || dash >= name.Length - 1) continue;
            if (int.TryParse(name[(dash + 1)..], out var n) && n > max) max = n;
        }
        return max + 1;
    }

    /// <summary>
    /// Persist the user's follow-up as a <c>[user]</c>-stream line in
    /// <c>logs/cli-output.log</c>. The activity log polls this file, so writing
    /// the line synchronously before the CLI starts means the user sees their
    /// own message in the conversation immediately - no silent gap between
    /// click and the agent's first reply.
    /// </summary>
    private void AppendUserPromptToCliLog(TaskInfo info, string prompt)
    {
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));
            var logPath = TaskPaths.CliOutputLog(info.FolderPath);
            var ts = DateTime.UtcNow.ToString("HH:mm:ss.fff");
            var oneLine = prompt.Replace("\r", " ").Replace("\n", " ").TrimEnd();
            var line = $"[{ts}] [user] {oneLine}";
            var prefix = File.Exists(logPath) && new FileInfo(logPath).Length > 0
                ? Environment.NewLine
                : string.Empty;
            File.AppendAllText(logPath, prefix + line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record user follow-up in CLI log for {JobId}", info.Id);
        }
    }

    public bool StopJob(string jobId, string? watchPath = null, RunStopReason reason = RunStopReason.UserStop)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var stopped = _router.Get(info.CliType).Stop(info.TaskKey, reason);
        if (stopped)
        {
            // Lifecycle event: stop requested. The matching RunFinished message
            // is emitted by ProjectRunner.OnCliFinishedAsync after the process
            // exits; this records the trigger separately so the timeline shows
            // both the request and the actual termination.
            try { _ = _bus?.EmitRunStopRequestedAsync(info, reason, source: "taskboard"); }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus mirror of stop-requested failed for {JobId}", jobId); }
        }
        return stopped;
    }

    /// <summary>
    /// True only when a CLI process is currently executing this job.
    /// The "3-progress" folder alone is not enough - a job can sit there
    /// after a stop / crash / restart without a live process.
    /// </summary>
    public bool IsJobLive(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var execution = _router.Get(info.CliType).GetExecution(info.TaskKey);
        return execution?.Status == "running";
    }

    public List<CliOutputLine> GetJobOutput(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return [];

        var logPath = TaskPaths.CliOutputLog(info.FolderPath);
        var liveOutput = _router.Get(info.CliType).GetOutput(info.TaskKey);

        if (liveOutput.Count > 0)
        {
            // Prepend the accumulated historical log so continuations show the full conversation.
            var historical = CliOutputLogParser.ParseFile(logPath);
            return historical.Count > 0 ? [.. historical, .. liveOutput] : liveOutput;
        }

        return CliOutputLogParser.ParseFile(logPath);
    }

    /// <summary>Looks up the CliExecution for a job from the right CLI backend.</summary>
    public CliExecution? GetExecutionForJob(TaskInfo info)
        => _router.Get(info.CliType).GetExecution(info.TaskKey);

    public async Task<(ContextUsageSnapshot? Snapshot, string? Error)> RefreshContextUsageAsync(string jobId, string? watchPath = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");
        // /context usage is Copilot-specific; if the job runs another CLI, no-op.
        if (CliTypes.Normalize(info.CliType) != CliTypes.Copilot)
            return (null, $"{CliTypes.Normalize(info.CliType)} CLI does not support /context usage refresh.");
        if (!_cli.IsAvailable()) return (null, "Copilot CLI is not installed or not on PATH");

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) return (null, $"No runner configured for project '{info.ProjectName}'");

        var execution = _cli.GetExecution(info.TaskKey);
        var canResumeSession = !string.IsNullOrWhiteSpace(info.SessionName) && execution?.Status != "running";
        var promptResult = await _cli.RunPromptOnceAsync(
            "/context usage",
            runner.Entry.RootPath,
            canResumeSession ? info.SessionName : null,
            resumeSession: canResumeSession,
            ct: ct);

        var snapshot = _contextUsageParser.Parse(promptResult.Stdout, promptResult.Stderr, promptResult.ExitCode);
        if (promptResult.TimedOut)
        {
            snapshot = snapshot with
            {
                Status = "error",
                Error = "The /context usage command timed out.",
                Notes = [.. snapshot.Notes, "The context usage query exceeded the time limit."]
            };
        }

        _mutations.UpdateContextUsage(jobId, snapshot, watchPath);
        return (snapshot, null);
    }

    /// <summary>
    /// Releases the in-memory active-job latch on the matching project's
    /// runner when an external mutation (the move endpoint, the boot-time
    /// stuck-folder sweep, a manual folder rearrangement) takes the active
    /// job out of <c>3-progress</c>. Wired off
    /// <see cref="TaskTransitionService.OnJobMoved"/> in <c>Program.cs</c> so
    /// the clear is atomic with the move from the API caller's perspective:
    /// a successful <c>POST /api/tasks/{id}/move</c> guarantees the next
    /// pickup tick sees <c>active=null</c>.
    /// </summary>
    public bool ClearActiveJobForProject(string projectName, string jobId, string reason)
    {
        if (string.IsNullOrEmpty(projectName) || string.IsNullOrEmpty(jobId)) return false;
        if (!_runners.TryGetValue(projectName, out var runner)) return false;
        return runner.ClearActiveJobIfMatches(jobId, reason);
    }

    /// <summary>
    /// Sweeps every project runner's defensive
    /// <see cref="ProjectRunner.ReconcileActiveJobAgainstDisk"/>. Cheap when
    /// no project has an active-job latch held; one disk scan per project
    /// otherwise. Wired off <see cref="TaskWatcherService.OnJobChanged"/> so
    /// non-API folder changes (external scripts, hand edits, boot-time
    /// stuck-folder sweep) get reconciled within the watcher's debounce
    /// interval rather than waiting for the next 5 s pickup tick.
    /// </summary>
    public void ReconcileAllRunners()
    {
        foreach (var runner in _runners.Values)
        {
            try { runner.ReconcileActiveJobAgainstDisk(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Reconcile failed for runner {Project}", runner.ProjectName); }
        }
    }

    public bool StartRunner(string projectName)
    {
        if (!_runners.TryGetValue(projectName, out var runner)) return false;
        // Starting is always immediate: auto-* never defers.
        runner.SetMode("auto-single", "api: POST /api/runner/{project}/start");
        return true;
    }

    public bool StopRunner(string projectName)
    {
        // Stop routes through the deferred-aware request so a Stop fired
        // while a job is running queues the pause behind the active job
        // (the runner does not kill the in-flight CLI). Matches the
        // semantics applied by PUT /mode (ADR-0044).
        var result = RequestModeChange(projectName, "paused", "api: POST /api/runner/{project}/stop");
        return result != null && result.Outcome != ModeChangeOutcome.Invalid;
    }
}
