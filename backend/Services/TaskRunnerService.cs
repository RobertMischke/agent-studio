using System.Collections.Concurrent;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
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
    private readonly JobScannerService _scanner;
    private readonly JobStateMachine _states;
    private readonly JobMutationService _mutations;
    private readonly JobSessionLog _sessions;
    private readonly CopilotCliService _cli;
    private readonly CliRouter _router;
    private readonly ContextUsageParser _contextUsageParser;
    private readonly SummaryGenerationService _summaryService;
    private readonly RuntimePromptService _prompts;
    private readonly JobTransitionService _transitions;
    private readonly ProjectSettingsService _projectSettings;
    private readonly OrchestratorChatLog _chatLog;
    private readonly OrchestratorLog _orchestratorLog;
    private readonly OrchestratorRunner _orchestratorRunner;
    private readonly OrchestratorSessionStore _orchestratorSessions;
    private readonly ConcurrentDictionary<string, ProjectRunner> _runners = new();

    public event Action<string, ProjectRunnerStatus>? OnRunnerStatusChanged;

    public TaskRunnerService(
        IConfiguration config,
        ILogger<TaskRunnerService> logger,
        JobScannerService scanner,
        JobStateMachine states,
        JobMutationService mutations,
        JobSessionLog sessions,
        CopilotCliService cli,
        CliRouter router,
        ContextUsageParser contextUsageParser,
        SummaryGenerationService summaryService,
        RuntimePromptService prompts,
        JobTransitionService transitions,
        ProjectSettingsService projectSettings,
        OrchestratorChatLog chatLog,
        OrchestratorLog orchestratorLog,
        OrchestratorRunner orchestratorRunner,
        OrchestratorSessionStore orchestratorSessions)
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
        _chatLog = chatLog;
        _orchestratorLog = orchestratorLog;
        _orchestratorRunner = orchestratorRunner;
        _orchestratorSessions = orchestratorSessions;
    }

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

            var runner = new ProjectRunner(entry.Name, entry, _logger, _scanner, _states, _sessions, _router, _summaryService, _prompts, _transitions, _chatLog, _mutations, _orchestratorLog, _orchestratorRunner, _orchestratorSessions, _projectSettings);
            runner.ConfigureWatchdog(LoadWatchdogConfig(_config));
            _stuckLoopBudget = LoadStuckLoopBudget(_config);
            runner.ConfigureStuckLoopBudget(_stuckLoopBudget);
            runner.OnStatusChanged += status => OnRunnerStatusChanged?.Invoke(entry.Name, status);
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

        // Run the loop - poll every 5 seconds for auto-mode runners
        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var runner in _runners.Values)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await runner.TickAsync(stoppingToken);
            }

            await Task.Delay(5000, stoppingToken);
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

    public bool SetMode(string projectName, string mode)
    {
        if (!_runners.TryGetValue(projectName, out var runner)) return false;
        var validModes = new[] { "manual", "auto-single", "auto-continuous", "paused" };
        if (!validModes.Contains(mode)) return false;
        runner.SetMode(mode);
        return true;
    }

    public async Task<ContinueJobResponse> StartJobAsync(string jobId, string? watchPath = null, string? modelOverride = null, string? cliTypeOverride = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) throw new JobOperationException("Job not found", 404);

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) throw new JobOperationException($"No runner configured for project '{info.ProjectName}' - check RootPath in WatchPaths config", 400);

        // Persist CLI type override before validity check so the next iteration picks it up.
        if (!string.IsNullOrWhiteSpace(cliTypeOverride) && cliTypeOverride != info.CliType)
        {
            _mutations.SetJobCliType(jobId, CliTypes.Normalize(cliTypeOverride), watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) throw new JobOperationException($"{cli.CliType} CLI is not installed or not on PATH", 400);

        // Persist override on the job so subsequent runs reuse it
        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
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
    public async Task<ContinueJobResponse> ContinueJobAsync(string jobId, string followupPrompt, string? watchPath = null, string? modelOverride = null, string? mode = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) throw new JobOperationException("Job not found", 404);

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) throw new JobOperationException($"No runner configured for project '{info.ProjectName}'", 400);
        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) throw new JobOperationException($"{cli.CliType} CLI is not installed or not on PATH", 400);

        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
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
    /// <see cref="JobOperationException"/>.
    /// </summary>
    private ContinueJobResponse ShapeOutcome(
        RunOutcome outcome,
        JobInfo info,
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

        if (rej.Reason == RunRejectReason.JobNotFound)
            throw new JobOperationException(rej.Message ?? "Job not found", 404);

        // CliUnavailable, None, or anything else: 400 with the message.
        throw new JobOperationException(rej.Message ?? "Cannot start job", 400);
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
    /// flight). Looks up the project runner for the job's watch path and
    /// asks it for the live counter. Used by the jobs endpoint so the UI
    /// can render the loop badge ("auto-loop 2/5") on the active job card.
    /// </summary>
    public StuckLoopState? GetStuckLoopStateForJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return null;
        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        return runner?.GetStuckLoopState(jobId);
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
    private void AppendUserPromptToCliLog(JobInfo info, string prompt)
    {
        try
        {
            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));
            var logPath = JobPaths.CliOutputLog(info.FolderPath);
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

    public bool StopJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        return info != null && _router.Get(info.CliType).Stop(info.JobKey);
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
        var execution = _router.Get(info.CliType).GetExecution(info.JobKey);
        return execution?.Status == "running";
    }

    public List<CliOutputLine> GetJobOutput(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return [];

        var logPath = JobPaths.CliOutputLog(info.FolderPath);
        var liveOutput = _router.Get(info.CliType).GetOutput(info.JobKey);

        if (liveOutput.Count > 0)
        {
            // Prepend the accumulated historical log so continuations show the full conversation.
            var historical = CliOutputLogParser.ParseFile(logPath);
            return historical.Count > 0 ? [.. historical, .. liveOutput] : liveOutput;
        }

        return CliOutputLogParser.ParseFile(logPath);
    }

    /// <summary>Looks up the CliExecution for a job from the right CLI backend.</summary>
    public CliExecution? GetExecutionForJob(JobInfo info)
        => _router.Get(info.CliType).GetExecution(info.JobKey);

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

        var execution = _cli.GetExecution(info.JobKey);
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

    public bool StartRunner(string projectName)
    {
        if (!_runners.TryGetValue(projectName, out var runner)) return false;
        runner.SetMode("auto-single");
        return true;
    }

    public bool StopRunner(string projectName)
    {
        if (!_runners.TryGetValue(projectName, out var runner)) return false;
        runner.SetMode("paused");
        return true;
    }
}
