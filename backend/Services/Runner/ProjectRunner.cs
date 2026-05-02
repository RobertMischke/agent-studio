using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;

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
    private string _mode = "manual";
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
        ProjectSettingsService projectSettings)
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

        // Listen across all CLI backends for completion of the active job.
        _router.OnFinished += (cliType, jobKey, exec) => OnCliFinished(cliType, jobKey, exec);
    }

    public void SetMode(string mode)
    {
        _mode = mode;
        _logger.LogInformation("Runner '{Project}' mode set to '{Mode}'", ProjectName, mode);
        try { OnModePersist?.Invoke(mode); }
        catch (Exception ex) { _logger.LogWarning(ex, "OnModePersist subscriber threw for {Project}", ProjectName); }
        NotifyStatus();
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
            QueuedJobIds = queued
        };
    }

    public async Task TickAsync(CancellationToken ct)
    {
        // Watchdog ticks regardless of runner mode: even when auto-pickup is
        // disabled, an active CLI on this project still needs to be watched
        // for hangs. Cheap (one timestamp arithmetic per active job).
        TickWatchdog();

        if (_mode is "manual" or "paused") return;
        if (_processing || _activeJobId != null) return;

        // Check if there's a running process for this project on any CLI
        if (_router.All.Any(c => c.IsRunningForProject(Entry.RootPath))) return;

        var nextJob = GetNextReadyJob();
        if (nextJob == null)
        {
            if (_mode == "auto-single")
            {
                // Route through SetMode so the revert is persisted - otherwise a
                // backend restart right after this would resurrect "auto-single"
                // and immediately pick up another job.
                SetMode("manual");
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

            _sessions.AppendSessionEvent(jobId, new SessionEvent
            {
                Ts = DateTime.UtcNow,
                Kind = plan.EventKind,
                Cli = cli.CliType,
                InputSessionId = plan.EventInputSessionId,
                CapturedSessionId = null,
                Resumed = plan.ResumeFlag,
                Reason = plan.EventReason
            }, Entry.Path);

            _activeCliType = cli.CliType;
            var (execution, cliError) = await cli.StartAsync(
                jobId, GetJobKey(jobId), prompt, Entry.RootPath,
                plan.SessionToResume, plan.ResumeFlag, info.Model, ct);

            if (execution == null)
            {
                _activeJobId = null;
                _activeCliType = null;
                NotifyStatus();
                // Roll back the consumed pending-intent on spawn failure so
                // the next auto-pickup retries instead of losing the user's
                // input.
                _mutations.RollbackStashedPendingIntent(info.FolderPath);
                return RunOutcome.Reject(new RunRejection(
                    Reason: RunRejectReason.CliUnavailable,
                    Message: cliError ?? $"Failed to start {cli.CliType} CLI process"));
            }

            // Spawn succeeded; drop the stashed intent (we've consumed it).
            _mutations.DiscardStashedPendingIntent(info.FolderPath);
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
        var silence = (now - lastStreamed).TotalSeconds;
        var age     = (now - exec.StartedAt).TotalSeconds;

        var prev = cli.GetWatchdogState(jobKey);
        var next = Watchdog.DecideState(silence, age, _watchdogConfig);
        if (!Watchdog.ShouldAnnounce(prev, next)) return;

        cli.SetWatchdogState(jobKey, next);

        var info = _scanner.FindJob(jobId, Entry.Path);
        if (info == null) return;

        switch (next)
        {
            case WatchdogState.Quiet:
                // Yellow, informational only. Single-line chat note so the
                // user sees that the watchdog is paying attention.
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[watchdog] Agent has been quiet {silence:F0}s. Watching; will warn at {_watchdogConfig.SuspiciousSeconds:F0}s.");
                break;
            case WatchdogState.Suspicious:
                _chatLog.Append(info, OrchestratorMessageKind.Decision,
                    $"[watchdog] Still silent at {silence:F0}s. Will kill at {_watchdogConfig.HungSeconds:F0}s if no signal arrives.");
                break;
            case WatchdogState.Hung:
                _chatLog.Append(info, OrchestratorMessageKind.GiveUp,
                    $"[watchdog] Killed after {silence:F0}s of silence. Process tree terminated; the run will finalize as failed.");
                _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
                {
                    Kind = OrchestratorLogKinds.Action,
                    Topic = OrchestratorLogTopics.Watchdog,
                    JobId = jobId,
                    Summary = $"Watchdog killed \"{info.Title}\" after {silence:F0}s of silence.",
                    Reasoning = $"No streamed output from the CLI for {silence:F0}s (run age {age:F0}s). Threshold: {_watchdogConfig.HungSeconds:F0}s. Process tree terminated; the run finalizes as failed."
                });
                try { cli.Stop(jobKey); }
                catch (Exception ex) { _logger.LogWarning(ex, "Watchdog kill failed for {JobId}", jobId); }
                break;
            case WatchdogState.Healthy:
                if (prev != WatchdogState.Healthy)
                {
                    _chatLog.Append(info, OrchestratorMessageKind.Decision,
                        "[watchdog] Agent resumed streaming. Back to healthy.");
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

    /// <summary>Set by <see cref="TaskRunnerService"/> on construction.</summary>
    public void ConfigureWatchdog(WatchdogConfig config) => _watchdogConfig = config;

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
        sb.AppendLine($"You are the orchestrator for the project \"{ProjectName}\" running in Agent Task Processor.");
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
        sb.AppendLine("- When the runner sends you a NEEDS_INPUT decision request, reply with the user-style follow-up to send back to the agent.");
        sb.AppendLine("- When the runner sends you a status query, summarize concisely.");
        sb.AppendLine("- The conversation history accumulated in this session is your memory across decisions; you do not need to be re-briefed each turn.");
        sb.AppendLine("- If you genuinely cannot decide without user knowledge that you do not have, reply with exactly: BLOCK");
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

            var orchestratorPrompt = BuildOrchestratorPrompt(info, promptText, lastAgentText);
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
                var resumePrompt = BuildOrchestratorResumePrompt(info, lastAgentText);
                result = await _orchestratorRunner.ResumeAsync(
                    session.SessionId, resumePrompt, modelToUse,
                    Entry.RootPath, CancellationToken.None);

                // If the session id was rejected (Anthropic rotated /
                // retention expired), fall back to a fresh one-shot so
                // the user's auto-mode is not stuck. Drop the stale
                // session-id; the next boot tick will re-create one.
                if (!result.Success && result.ErrorMessage != null
                    && (result.ErrorMessage.Contains("session", StringComparison.OrdinalIgnoreCase)
                        || result.ErrorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("[orchestrator] resume rejected; clearing session and falling back to one-shot. error={Err}", result.ErrorMessage);
                    _orchestratorSessions.Clear(Entry.Path);
                    result = await _orchestratorRunner.DecideAsync(
                        orchestratorPrompt, modelToUse, Entry.RootPath, CancellationToken.None);
                }
                else if (result.Success)
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

            if (!result.Success || string.IsNullOrWhiteSpace(result.ReplyText)
                || result.ReplyText.Trim().Equals("BLOCK", StringComparison.OrdinalIgnoreCase))
            {
                // Orchestrator declined or errored. Surface the question to
                // the user the same way the manual path does, plus a meta
                // line explaining why the orchestrator could not decide.
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

            var reply = result.ReplyText.Trim();
            _chatLog.Append(info, OrchestratorMessageKind.Decision,
                $"[orchestrator] Auto-mode decision: {Truncate(reply, 200)}");
            _orchestratorLog.Append(info.WatchPath, new OrchestratorLogEntry
            {
                Kind = OrchestratorLogKinds.Decision,
                Topic = "agent-needs-input",
                JobId = jobId,
                Summary = $"Auto-decided for \"{info.Title}\": {Truncate(reply, 140)}",
                Reasoning = $"Project mode is {_mode}; the active agent emitted NEEDS_INPUT and the orchestrator was invoked to reply on the user's behalf. " +
                            $"The reply will be sent as a Continue follow-up. Model: {result.Model}.",
                TokenUsage = result.TokenUsage
            });

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
    /// </summary>
    private static string BuildOrchestratorResumePrompt(JobInfo info, string lastAgentText)
    {
        return
            $"NEEDS_INPUT decision request for task \"{info.Title}\" (id: {info.Id}).\n\n" +
            "The agent's last message you need to answer:\n" +
            lastAgentText +
            "\n\nReply with the user-style follow-up to send back to the agent. " +
            "Reply with exactly BLOCK if you cannot decide without user knowledge you do not have. " +
            "Plain text, no markdown headings.";
    }

    /// <summary>
    /// Build the prompt the orchestrator's one-shot Claude call sees. Kept
    /// here instead of in a runtime template file because the framing is
    /// load-bearing for the decision contract: the orchestrator must know
    /// it can return BLOCK to defer, and must reply in the user's voice
    /// not the orchestrator's.
    /// </summary>
    private static string BuildOrchestratorPrompt(JobInfo info, string promptText, string lastAgentText)
    {
        return
            "You are the project orchestrator for Agent Task Processor. " +
            "The user has set this project to auto mode and stepped away. " +
            "The active task agent just asked for input and is waiting. " +
            "Your job: decide what the user would have replied, in one short paragraph, in the user's voice. " +
            "The reply will be sent back to the agent as a Continue follow-up.\n\n" +
            $"Project: {info.ProjectName}\n" +
            $"Task: {info.Title}\n\n" +
            "Original task description:\n" +
            (string.IsNullOrWhiteSpace(promptText) ? "(empty)" : promptText) +
            "\n\nThe agent's last message you need to answer:\n" +
            lastAgentText +
            "\n\nReasoning style:\n" +
            "- If the agent's question has an obvious right answer in context, give it directly.\n" +
            "- If the question is ambiguous and multiple paths are reasonable, pick the simpler path and say why in one short sentence.\n" +
            "- If you genuinely cannot decide without user knowledge that you do not have, reply with exactly: BLOCK\n\n" +
            "Reply now with the user-style follow-up directly. Do not preface with \"I would say\" or similar. Plain text, no markdown headings.";
    }

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
            if (!string.IsNullOrWhiteSpace(capturedSessionId))
            {
                // Append to the chain (and update sessionName in lockstep). Forking
                // CLIs emit a new id on every --resume; preserving the chain lets the
                // user see how often the session has been continued.
                _sessions.AppendSessionToChain(jobId, capturedSessionId!, Entry.Path);
                _sessions.BackfillLatestSessionEventCapturedId(jobId, capturedSessionId!, Entry.Path);
            }
            else if (cli is ClaudeCliService
                  || cli is CodexCliService
                  || cli is GeminiCliService)
            {
                // The CLI normally emits a session UUID on every run; missing
                // it means the next follow-up will fall back to Recovery. Tell
                // the user explicitly so the loop is not silent.
                var captureFailInfo = _scanner.FindJob(jobId, Entry.Path);
                if (captureFailInfo != null)
                {
                    _chatLog.Append(captureFailInfo, OrchestratorMessageKind.Decision,
                        $"[capture-fail] No {cli.CliType} session id from this run; next follow-up will rebuild from disk.");
                }
            }

            // Snapshot the live output before we flush it to disk. The
            // outcome analyzer needs the buffer to classify the run, and the
            // post-run policy may re-issue another run on top before we let
            // the regular review/summary pipeline proceed.
            var liveOutputSnapshot = cli.GetOutput(jobKey);

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

            // Apply the orchestrator's post-run policy. The policy is pure;
            // we apply its decision here. The activeInfo lookup may fail
            // (job folder moved between completion and lookup), in which
            // case we skip the meta channel and fall through to the
            // existing accept-path - the policy is a refinement, not a gate.
            var capturedIntent = _activeIntent;
            var capturedFollowup = _activeFollowup;
            var capturedPlan = _activePlan;
            var capturedAttempt = _activeReissueAttempt;
            var outcome = AgentOutcomeAnalyzer.Analyze(
                liveOutputSnapshot,
                execution.Status ?? "completed",
                execution.DurationSeconds ?? 0.0);
            OutcomeAction? action = capturedPlan != null
                ? RunOutcomePolicy.Decide(capturedIntent, capturedPlan, outcome, capturedFollowup, capturedAttempt)
                : null;
            if (action != null && activeInfo != null)
            {
                // Build a short signature so we can suppress the second
                // identical heuristic message in a Recovery cascade. Two
                // back-to-back "needsinput" warnings on a stuck loop do not
                // help the user; one is enough.
                var signature = $"{action.Kind}|{action.IsHeuristicFallback}|{outcome.Kind}|{string.Equals(capturedPlan!.EventKind, "recovery", StringComparison.OrdinalIgnoreCase)}";
                var suppress = action.Kind == OutcomeActionKind.NotifyUserAndAccept
                            && action.IsHeuristicFallback
                            && string.Equals(_lastMetaSignature, signature, StringComparison.Ordinal);

                if (!suppress)
                {
                    if (action.IsHeuristicFallback && !string.IsNullOrWhiteSpace(action.MetaMessage))
                    {
                        _chatLog.Append(activeInfo, OrchestratorMessageKind.HeuristicFallback,
                            $"{action.MetaMessage} (run summary: {outcome.Summary ?? "n/a"})");
                    }
                    else if (!string.IsNullOrWhiteSpace(action.MetaMessage))
                    {
                        var kind = action.Kind switch
                        {
                            OutcomeActionKind.ReissueWithStrongerFraming => OrchestratorMessageKind.Reissue,
                            OutcomeActionKind.NotifyUserAndStop          => OrchestratorMessageKind.GiveUp,
                            _                                            => OrchestratorMessageKind.Decision
                        };
                        _chatLog.Append(activeInfo, kind, action.MetaMessage);
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
                    var retryPrompt = RunOutcomePolicy.BuildReissueFollowupPrompt(action.FollowupRetryPrompt!, recoveryContext: wasRecovery);
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

            if (RunCompletionPolicy.ShouldMoveToReview(execution.Status))
            {
                var moveOutcome = await _transitions.MoveAsync(jobId, JobStates.Review, Entry.Path, CancellationToken.None);
                if (moveOutcome.Status == MoveJobStatus.Success)
                {
                    // Fire-and-forget Haiku summary on successful completion.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var info = _scanner.FindJob(jobId, Entry.Path);
                            if (info != null) await _summaryService.GenerateAsync(info);
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

    private static string BuildAttachmentsList(string jobFolder)
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

    private JobInfo? GetNextReadyJob()
    {
        return _scanner.ScanAllJobs()
            .Where(j => j.ProjectName == ProjectName && j.State == JobStates.Ready)
            .OrderBy(j => j.Order)
            .FirstOrDefault();
    }

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
        OnStatusChanged?.Invoke(GetStatus());
    }
}
