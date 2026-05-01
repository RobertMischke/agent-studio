using System.Collections.Concurrent;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Services;

/// <summary>
/// Why an upcoming CLI run was triggered. The planner uses this single
/// dimension to decide between the start-shaped and continue-shaped
/// branches; everything else (session state, job state, CLI compatibility)
/// is plain data. Keeping the trigger reason explicit in one enum is what
/// lets <see cref="ProjectRunner.PlanRun"/> own the whole decision tree
/// instead of having the start and continue endpoints reinvent it
/// independently.
/// </summary>
public enum RunIntent
{
    /// <summary>User clicked Play / hit /jobs/{id}/start.</summary>
    ManualStart,
    /// <summary>Auto-pickup tick chose this job from the ready queue.</summary>
    AutoPickup,
    /// <summary>User typed in the chat / hit /jobs/{id}/continue with a follow-up.</summary>
    UserContinue
}

/// <summary>
/// Pure description of what a single CLI invocation should do. Produced by
/// <see cref="ProjectRunner.PlanRun"/> from inputs (intent, job state,
/// session state, CLI capabilities) and consumed by the runner which then
/// applies side-effects (state moves, scanner writes, log writes, event
/// append, CLI start). Splitting plan from apply keeps the decision tree
/// fully unit-testable without mocking the scanner or CLI.
/// </summary>
public sealed record RunPlan(
    string Prompt,
    string? SessionToResume,
    bool ResumeFlag,
    string EventKind,
    string? EventReason,
    string? EventInputSessionId,
    bool MoveJobToProgress,
    bool MarkSessionChainRecovery,
    bool WriteCutMarker,
    string? CutMarkerReason,
    string? PersistSessionName,
    bool ClearStaleSessionName);

public class TaskRunnerService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TaskRunnerService> _logger;
    private readonly JobScannerService _scanner;
    private readonly CopilotCliService _cli;
    private readonly CliRouter _router;
    private readonly ContextUsageParser _contextUsageParser;
    private readonly SummaryGenerationService _summaryService;
    private readonly ProjectSettingsService _projectSettings;
    private readonly ConcurrentDictionary<string, ProjectRunner> _runners = new();

    public event Action<string, ProjectRunnerStatus>? OnRunnerStatusChanged;

    public TaskRunnerService(
        IConfiguration config,
        ILogger<TaskRunnerService> logger,
        JobScannerService scanner,
        CopilotCliService cli,
        CliRouter router,
        ContextUsageParser contextUsageParser,
        SummaryGenerationService summaryService,
        ProjectSettingsService projectSettings)
    {
        _config = config;
        _logger = logger;
        _scanner = scanner;
        _cli = cli;
        _router = router;
        _contextUsageParser = contextUsageParser;
        _summaryService = summaryService;
        _projectSettings = projectSettings;
    }

    public SummaryGenerationService SummaryService => _summaryService;

    public CliRouter Router => _router;

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

            var runner = new ProjectRunner(entry.Name, entry, _logger, _scanner, _router, _summaryService);
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
            _logger.LogWarning("Copilot CLI not available — runners will be in manual/board-only mode");
        }

        // Run the loop — poll every 5 seconds for auto-mode runners
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

    public async Task<(CliExecution? Execution, string? Error)> StartJobAsync(string jobId, string? watchPath = null, string? modelOverride = null, string? cliTypeOverride = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) return (null, $"No runner configured for project '{info.ProjectName}' — check RootPath in WatchPaths config");

        // Persist CLI type override before validity check so the next iteration picks it up.
        if (!string.IsNullOrWhiteSpace(cliTypeOverride) && cliTypeOverride != info.CliType)
        {
            _scanner.SetJobCliType(jobId, CliTypes.Normalize(cliTypeOverride), watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) return (null, $"{cli.CliType} CLI is not installed or not on PATH");

        // Persist override on the job so subsequent runs reuse it
        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _scanner.SetJobModel(jobId, modelOverride, watchPath);
        }

        return await runner.StartJobManualAsync(jobId, ct);
    }

    /// <summary>
    /// Resumes a job's CLI session and feeds it a follow-up prompt. Recovery
    /// fallback: when no session is recorded (or the recorded id is incompatible
    /// with the current CLI), <see cref="ProjectRunner.ContinueJobAsync"/>
    /// switches to a fresh-session run with a recovery prompt that instructs
    /// the agent to reconstruct context from the job folder. The user gets the
    /// continuation they asked for instead of a 400 — at the cost of conversation
    /// memory that wasn't already on disk.
    /// </summary>
    public async Task<(CliExecution? Execution, string? Error)> ContinueJobAsync(string jobId, string followupPrompt, string? watchPath = null, string? modelOverride = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) return (null, $"No runner configured for project '{info.ProjectName}'");
        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) return (null, $"{cli.CliType} CLI is not installed or not on PATH");

        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _scanner.SetJobModel(jobId, modelOverride, watchPath);
        }

        _scanner.AppendContinuationNote(jobId, followupPrompt, watchPath);
        AppendUserPromptToCliLog(info, followupPrompt);

        return await runner.ContinueJobAsync(jobId, followupPrompt, ct);
    }

    /// <summary>
    /// Persist the user's follow-up as a <c>[user]</c>-stream line in
    /// <c>logs/cli-output.log</c>. The activity log polls this file, so writing
    /// the line synchronously before the CLI starts means the user sees their
    /// own message in the conversation immediately — no silent gap between
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
    /// The "3-progress" folder alone is not enough — a job can sit there
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

        _scanner.UpdateContextUsage(jobId, snapshot, watchPath);
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

public class ProjectRunner
{
    private readonly ILogger _logger;
    private readonly JobScannerService _scanner;
    private readonly CliRouter _router;
    private readonly SummaryGenerationService _summaryService;
    private string _mode = "manual";
    private string? _activeJobId;
    private string? _activeCliType;
    private bool _processing;

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

    public ProjectRunner(string projectName, WatchPathEntry entry, ILogger logger, JobScannerService scanner, CliRouter router, SummaryGenerationService summaryService)
    {
        ProjectName = projectName;
        Entry = entry;
        _logger = logger;
        _scanner = scanner;
        _router = router;
        _summaryService = summaryService;

        // Listen across all CLI backends for completion of the active job.
        _router.OnFinished += (cliType, jobKey, exec) => OnCliFinished(cliType, jobKey, exec);
    }

    private ICliExecutionService GetCliFor(JobInfo info) => _router.Get(info.CliType);

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
        if (_mode is "manual" or "paused") return;
        if (_processing || _activeJobId != null) return;

        // Check if there's a running process for this project on any CLI
        if (_router.All.Any(c => c.IsRunningForProject(Entry.RootPath))) return;

        var nextJob = GetNextReadyJob();
        if (nextJob == null)
        {
            if (_mode == "auto-single")
            {
                // Route through SetMode so the revert is persisted — otherwise a
                // backend restart right after this would resurrect "auto-single"
                // and immediately pick up another job.
                SetMode("manual");
            }
            return;
        }

        await RunCliAsync(nextJob.Id, RunIntent.AutoPickup, followupPrompt: null, ct);
    }

    public Task<(CliExecution? Execution, string? Error)> StartJobManualAsync(string jobId, CancellationToken ct)
        => RunCliAsync(jobId, RunIntent.ManualStart, followupPrompt: null, ct);

    /// <summary>
    /// Sends a follow-up prompt into the CLI session that was originally created
    /// for this job (via <c>--resume</c>). When no compatible session is on
    /// record, the planner falls back to <b>recovery mode</b>: a fresh CLI run
    /// instructed to reconstruct context from the job folder. Moves the job back
    /// to <c>3-progress</c> if it sits in <c>4-review</c> or <c>5-completed</c>.
    /// </summary>
    public Task<(CliExecution? Execution, string? Error)> ContinueJobAsync(string jobId, string followupPrompt, CancellationToken ct)
        => RunCliAsync(jobId, RunIntent.UserContinue, followupPrompt, ct);

    /// <summary>
    /// Single entry point for spawning the CLI for a job. <see cref="PlanRun"/>
    /// owns the full decision tree (resume vs recovery vs fresh, prompt choice,
    /// session-event shape, state moves); this method only applies the side
    /// effects the plan describes. Both the start endpoints and the continue
    /// endpoint route through here so a fix in one path can never miss its
    /// sibling — that divergence is the bug class this refactor exists to
    /// prevent.
    /// </summary>
    private async Task<(CliExecution? Execution, string? Error)> RunCliAsync(
        string jobId, RunIntent intent, string? followupPrompt, CancellationToken ct)
    {
        if (_activeJobId != null)
        {
            if (intent == RunIntent.ManualStart)
                _logger.LogWarning("Runner '{Project}' already has active job {JobId}", ProjectName, _activeJobId);
            return (null, $"Runner '{ProjectName}' is already executing job '{_activeJobId}'");
        }

        _processing = true;
        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info == null) return (null, "Job not found");

            var cli = GetCliFor(info);
            var initialState = info.State;
            var promptPath = Path.Combine(info.FolderPath, "prompt.md");
            var jobFolder = info.FolderPath;

            var plan = PlanRun(
                intent,
                initialState,
                info.SessionName,
                cli.CliType,
                cli.IsCompatibleSessionName,
                jobId,
                promptPath,
                jobFolder,
                followupPrompt);

            if (plan.MoveJobToProgress && info.State != JobStates.Progress)
            {
                _scanner.MoveJob(jobId, JobStates.Progress, Entry.Path);
                info = _scanner.FindJob(jobId, Entry.Path) ?? info;
            }

            _activeJobId = jobId;
            NotifyStatus();

            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));

            // Diagnostic logs — surface the planner's decision in one place so
            // operators reading the log can tell which branch fired without
            // grepping for old per-method messages.
            _logger.LogInformation(
                "[taskboard] {Intent} for job {JobId} on {Cli}: kind={Kind} resume={Resume} session={Session} reason={Reason}",
                intent, jobId, cli.CliType, plan.EventKind, plan.ResumeFlag,
                plan.SessionToResume ?? "<none>", plan.EventReason ?? "<none>");
            _logger.LogInformation("[taskboard] using working directory {Path}", Entry.RootPath);

            if (plan.ClearStaleSessionName)
                _scanner.SetJobSessionName(jobId, null, Entry.Path);
            if (plan.PersistSessionName != null)
                _scanner.SetJobSessionName(jobId, plan.PersistSessionName, Entry.Path);
            if (plan.MarkSessionChainRecovery)
                _scanner.MarkSessionChainRecovery(jobId, Entry.Path);
            if (plan.WriteCutMarker)
                AppendSessionCutMarkerToCliLog(info, plan.CutMarkerReason ?? "session lost");

            _scanner.AppendSessionEvent(jobId, new SessionEvent
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
                jobId, GetJobKey(jobId), plan.Prompt, Entry.RootPath,
                plan.SessionToResume, plan.ResumeFlag, info.Model, ct);

            if (execution == null)
            {
                _activeJobId = null;
                _activeCliType = null;
                NotifyStatus();
                return (null, cliError ?? $"Failed to start {cli.CliType} CLI process");
            }

            return (execution, null);
        }
        finally
        {
            _processing = false;
        }
    }

    /// <summary>
    /// Pure decision function: maps (intent, job state, session state, CLI
    /// capabilities) to a <see cref="RunPlan"/>. No I/O, no field access — fully
    /// unit-testable across the (intent × state × session) matrix in
    /// <c>TaskRunnerPlanTests</c>. Whenever you change a branch here, add or
    /// adjust the matrix row that locks it in; that is what stops the
    /// start/continue divergence the previous design kept producing.
    /// </summary>
    public static RunPlan PlanRun(
        RunIntent intent,
        string initialState,
        string? sessionName,
        string cliType,
        Func<string?, bool> isCompatibleSessionName,
        string jobId,
        string promptPath,
        string jobFolder,
        string? followupPrompt)
    {
        if (intent == RunIntent.UserContinue)
        {
            var hasSession = !string.IsNullOrWhiteSpace(sessionName);
            var compatible = hasSession && isCompatibleSessionName(sessionName);
            var placeholder = IsPlaceholderSessionSlug(sessionName);
            var canResume = hasSession && compatible && !placeholder;
            string? reason =
                !hasSession ? "no session recorded"
                : !compatible ? $"recorded id is not a valid {cliType} session"
                : placeholder ? "recorded id is a legacy placeholder slug"
                : null;
            var moveToProgress = initialState is JobStates.Review or JobStates.Completed or JobStates.Ready;

            if (canResume)
            {
                return new RunPlan(
                    Prompt: followupPrompt ?? string.Empty,
                    SessionToResume: sessionName,
                    ResumeFlag: true,
                    EventKind: "continue",
                    EventReason: null,
                    EventInputSessionId: sessionName,
                    MoveJobToProgress: moveToProgress,
                    MarkSessionChainRecovery: false,
                    WriteCutMarker: false,
                    CutMarkerReason: null,
                    PersistSessionName: null,
                    ClearStaleSessionName: false);
            }

            return new RunPlan(
                Prompt: BuildRecoveryContinuationPrompt(jobFolder, followupPrompt ?? string.Empty),
                SessionToResume: null,
                ResumeFlag: false,
                EventKind: "recovery",
                EventReason: reason,
                EventInputSessionId: null,
                MoveJobToProgress: moveToProgress,
                MarkSessionChainRecovery: true,
                WriteCutMarker: true,
                CutMarkerReason: reason ?? "session lost",
                PersistSessionName: null,
                ClearStaleSessionName: false);
        }

        // ManualStart / AutoPickup share the same plan shape — only the trigger
        // differs, and that is logged at the call site, not branched here.
        var moveStartToProgress = initialState == JobStates.Ready;
        var startSession = sessionName;
        var sessionDropped = false;
        var clearStale = false;
        var markRecovery = false;
        if (!string.IsNullOrWhiteSpace(startSession) && !isCompatibleSessionName(startSession))
        {
            var isLegacyPlaceholder = IsPlaceholderSessionSlug(startSession);
            startSession = null;
            if (isLegacyPlaceholder)
            {
                clearStale = true;
            }
            else
            {
                markRecovery = true;
                sessionDropped = true;
            }
        }
        var resume = !string.IsNullOrWhiteSpace(startSession);
        string? persistSessionName = null;
        if (!resume && cliType == CliTypes.Copilot)
        {
            // Copilot uses the persisted name as the resume handle — pre-generate
            // a slug now so the next run can find it. Other CLIs capture a real
            // UUID during streaming and leave SessionName null until then.
            startSession = BuildSessionName(jobId);
            persistSessionName = startSession;
        }

        var useResumePrompt = ShouldUseResumePrompt(initialState, resume, sessionDropped);
        var prompt = useResumePrompt
            ? BuildResumeContinuationPrompt(jobFolder)
            : BuildFreshStartPrompt(promptPath, jobFolder);

        string evtKind = sessionDropped ? "recovery" : (resume ? "continue" : "start");
        string? evtReason = sessionDropped ? "previous session was for another CLI — files reconstructed" : null;

        return new RunPlan(
            Prompt: prompt,
            SessionToResume: startSession,
            ResumeFlag: resume,
            EventKind: evtKind,
            EventReason: evtReason,
            EventInputSessionId: resume ? startSession : null,
            MoveJobToProgress: moveStartToProgress,
            MarkSessionChainRecovery: markRecovery,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: persistSessionName,
            ClearStaleSessionName: clearStale);
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
            var line = $"[{ts}] [system] ─── Session lost ({reason}) — recovering from job folder ───";
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

    private static string BuildSessionName(string jobId)
    {
        // Copilot uses the name as a stable handle for --resume; keep it short, deterministic and unique.
        var slug = new string(jobId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (slug.Length > 40) slug = slug[..40];
        return $"taskboard-{slug}-{DateTime.UtcNow:yyyyMMddHHmm}";
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderSessionSlugRegex =
        new(@"^taskboard-[A-Za-z0-9_-]+-\d{12}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// True for slugs we generated via <see cref="BuildSessionName"/> on an earlier
    /// run. These were never real sessions on the agent side — recognising them
    /// lets the cross-CLI guard drop them silently instead of treating the
    /// next start as a recovery from an interrupted run.
    /// </summary>
    public static bool IsPlaceholderSessionSlug(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName)
           && PlaceholderSessionSlugRegex.IsMatch(sessionName!);

    /// <summary>
    /// Decides whether the start should inject the resume-continuation prompt
    /// instead of the fresh-start prompt.
    /// <list type="bullet">
    /// <item>Sending it when no real session exists and nothing was dropped just
    /// gets a "I don't see an interrupted task" reply and an exit — so a job that
    /// happens to be in 3-progress without a captured UUID is treated as a fresh
    /// start.</item>
    /// <item><c>sessionDropped</c> means the persisted session id was for another
    /// CLI; the agent that wrote the job folder did real work so reconstruction
    /// from files is worth attempting regardless of the current state.</item>
    /// </list>
    /// </summary>
    public static bool ShouldUseResumePrompt(string initialState, bool resume, bool sessionDropped)
    {
        if (sessionDropped) return true;
        if (initialState == JobStates.Progress && resume) return true;
        return false;
    }

    /// <summary>
    /// Initial-run prompt: instructs the agent to read the task prompt and
    /// execute it. Used only for fresh starts (job came from 2-ready).
    /// </summary>
    public static string BuildFreshStartPrompt(string promptPath, string jobFolder)
        => $"Lies @\"{promptPath}\" und führe den Task aus. Der Job-Ordner ist \"{jobFolder}\".";

    /// <summary>
    /// Resume prompt: forces the agent to rebuild context from the job folder
    /// before doing anything else. Sent when the previous run was interrupted
    /// (job already in 3-progress) or when the persisted session id had to be
    /// dropped as incompatible — in both cases the model would otherwise treat
    /// the new turn as a fresh request and ask the user what to continue with.
    /// English on purpose: works across Claude / Codex / Gemini / Copilot
    /// regardless of the user's chat language.
    /// </summary>
    public static string BuildResumeContinuationPrompt(string jobFolder)
        => "Resume the interrupted task.\n\n"
         + "Task folder:\n"
         + $"{jobFolder}\n\n"
         + "Read job.json, prompt.md, status.md and existing logs first.\n"
         + "Reconstruct progress from files and continue implementation.\n"
         + "Do not ask what to do unless required files are missing.";

    /// <summary>
    /// Recovery prompt for the continue endpoint when the previous CLI session
    /// is lost or incompatible. Tells the agent to rebuild context from the
    /// job folder + git, then act on the user's follow-up that's appended at
    /// the bottom. Bounded ("last ~200 lines") so we don't ask the agent to
    /// drag a massive log into context. English on purpose — works across all
    /// supported CLIs.
    /// </summary>
    public static string BuildRecoveryContinuationPrompt(string jobFolder, string userFollowup)
        => "Continue this task — the previous CLI session was lost and cannot be resumed.\n\n"
         + "Task folder:\n"
         + $"{jobFolder}\n\n"
         + "Reconstruct context before doing anything else:\n"
         + "1. Read job.json, prompt.md, status.md.\n"
         + "2. Read the last ~200 lines of logs/cli-output.log to see what the previous run was doing.\n"
         + "3. Run `git status` and `git diff` in the working directory to see the current change set.\n\n"
         + "Then continue with the user's follow-up below. Treat this as a continuation, not a new task — keep the existing changes and protocol, append rather than restart.\n\n"
         + "User follow-up:\n"
         + (userFollowup?.TrimEnd() ?? string.Empty);

    private void OnCliFinished(string cliType, string jobKey, CliExecution execution)
    {
        if (GetActiveJobKey() != jobKey || _activeJobId == null) return;
        if (_activeCliType != null && !string.Equals(cliType, _activeCliType, StringComparison.OrdinalIgnoreCase)) return;

        _logger.LogInformation("Job {JobId} finished in project '{Project}' on {Cli} with status {Status}",
            _activeJobId, ProjectName, cliType, execution.Status);

        var cli = _router.Get(cliType);

        // Persist last token/usage summary (best-effort)
        var usage = cli.GetLastUsage(jobKey);
        if (usage != null)
        {
            _scanner.UpdateLastUsage(_activeJobId, usage, Entry.Path);
        }

        // Persist the captured session UUID so follow-ups can resume.
        // Claude / Codex / Gemini all auto-create a UUID on first run and
        // surface it in their JSON output; we capture it during streaming
        // and write it back here. Without this, Continue always loses
        // context because info.SessionName never advances past the slug.
        var capturedSessionId = cli switch
        {
            Cli.ClaudeCliService claude => claude.GetCapturedSessionId(jobKey),
            Cli.CodexCliService codex   => codex.GetCapturedSessionId(jobKey),
            Cli.GeminiCliService gemini => gemini.GetCapturedSessionId(jobKey),
            _ => null
        };
        if (!string.IsNullOrWhiteSpace(capturedSessionId))
        {
            // Append to the chain (and update sessionName in lockstep). Forking
            // CLIs emit a new id on every --resume; preserving the chain lets the
            // user see how often the session has been continued.
            _scanner.AppendSessionToChain(_activeJobId, capturedSessionId!, Entry.Path);
            _scanner.BackfillLatestSessionEventCapturedId(_activeJobId, capturedSessionId!, Entry.Path);
        }

        // Write CLI output to log file. The runtime JSONL is the durable
        // backup that lets us recover the Activity Log after a backend
        // restart; once the consolidated cli-output.log has it, the JSONL
        // can go so the disk-fallback path in GetOutput doesn't replay the
        // same lines after the in-memory buffer is evicted.
        var activeInfo = _scanner.FindJob(_activeJobId, Entry.Path);
        if (activeInfo != null && WriteCliLog(activeInfo, cli))
        {
            cli.DiscardPersistedOutput(jobKey);
        }

        var finishedJobId = _activeJobId;
        // Move job to 4-review
        _scanner.MoveJob(_activeJobId, JobStates.Review, Entry.Path);

        // Fire-and-forget Haiku summary on successful completion. Skipped for
        // failed/cancelled runs because partial logs rarely yield useful
        // protocols and the user can re-run if needed.
        if (string.Equals(execution.Status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var info = _scanner.FindJob(finishedJobId, Entry.Path);
                    if (info != null) await _summaryService.GenerateAsync(info);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Summary generation crashed for {JobId}", finishedJobId);
                }
            });
        }

        _activeJobId = null;
        _activeCliType = null;
        NotifyStatus();
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
                // to flush — don't truncate the existing log.
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

    private string GetJobKey(string jobId) => JobIdentity.CreateKey(Entry.Path, jobId);

    private string? GetActiveJobKey() => _activeJobId != null ? GetJobKey(_activeJobId) : null;

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
