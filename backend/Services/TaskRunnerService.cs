using System.Collections.Concurrent;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Cli;

namespace OrchestratorApi.Services;

public class TaskRunnerService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TaskRunnerService> _logger;
    private readonly JobScannerService _scanner;
    private readonly CopilotCliService _cli;
    private readonly CliRouter _router;
    private readonly ContextUsageParser _contextUsageParser;
    private readonly SummaryGenerationService _summaryService;
    private readonly ConcurrentDictionary<string, ProjectRunner> _runners = new();

    public event Action<string, ProjectRunnerStatus>? OnRunnerStatusChanged;

    public TaskRunnerService(
        IConfiguration config,
        ILogger<TaskRunnerService> logger,
        JobScannerService scanner,
        CopilotCliService cli,
        CliRouter router,
        ContextUsageParser contextUsageParser,
        SummaryGenerationService summaryService)
    {
        _config = config;
        _logger = logger;
        _scanner = scanner;
        _cli = cli;
        _router = router;
        _contextUsageParser = contextUsageParser;
        _summaryService = summaryService;
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
            _runners[entry.Name] = runner;
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
    /// Resumes the Copilot session bound to a job and feeds it a follow-up prompt.
    /// The job must already have a sessionName recorded in <c>job.json</c> (i.e. it was started before).
    /// </summary>
    public async Task<(CliExecution? Execution, string? Error)> ContinueJobAsync(string jobId, string followupPrompt, string? watchPath = null, string? modelOverride = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");
        if (string.IsNullOrWhiteSpace(info.SessionName))
            return (null, "This job has no session yet — start it once before continuing.");

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) return (null, $"No runner configured for project '{info.ProjectName}'");
        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) return (null, $"{cli.CliType} CLI is not installed or not on PATH");

        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _scanner.SetJobModel(jobId, modelOverride, watchPath);
        }

        _scanner.AppendContinuationNote(jobId, followupPrompt, watchPath);

        return await runner.ContinueJobAsync(jobId, followupPrompt, ct);
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
                _mode = "manual";
                NotifyStatus();
            }
            return;
        }

        await StartJobInternalAsync(nextJob.Id, ct);
    }

    public async Task<(CliExecution? Execution, string? Error)> StartJobManualAsync(string jobId, CancellationToken ct)
    {
        if (_activeJobId != null)
        {
            _logger.LogWarning("Runner '{Project}' already has active job {JobId}", ProjectName, _activeJobId);
            return (null, $"Runner '{ProjectName}' is already executing job '{_activeJobId}'");
        }

        return await StartJobInternalAsync(jobId, ct);
    }

    private async Task<(CliExecution? Execution, string? Error)> StartJobInternalAsync(string jobId, CancellationToken ct)
    {
        _processing = true;
        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info == null) return (null, "Job not found");

            // Capture state BEFORE moving to 3-progress so we can tell a fresh
            // start (job sits in 2-ready) apart from a resume after the previous
            // run was interrupted (job already in 3-progress because it was
            // started before but never reached 4-review). Same callsite, same
            // CLI args — only the injected prompt and the structured logging
            // differ between the two cases.
            var initialState = info.State;

            if (info.State == JobStates.Ready)
            {
                _scanner.MoveJob(jobId, JobStates.Progress, Entry.Path);
                info = _scanner.FindJob(jobId, Entry.Path) ?? info;
            }

            _activeJobId = jobId;
            NotifyStatus();

            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));

            // Resolve / persist a stable session name so follow-ups can use --resume.
            // Only Copilot uses a pre-generated slug as its resume handle. Claude /
            // Codex / Gemini emit a real session UUID in their first stream-json
            // frame and we capture it in OnCliFinished — until then SessionName
            // stays null. Pre-generating a slug for them used to backfire: the
            // slug failed IsCompatibleSessionName on the next run, the cross-CLI
            // guard treated it as a dropped foreign session, and the resume
            // prompt was injected into what was actually a fresh start.
            var cli = GetCliFor(info);
            var sessionName = info.SessionName;
            // Cross-CLI guard: a session name recorded under another CLI (e.g.
            // Copilot's slug) is meaningless to Claude/Codex and used to make
            // them hang on `-r`. Drop it and let the fresh-session branch pick
            // up. Legacy placeholder slugs we generated ourselves (pattern
            // `taskboard-…-yyyyMMddHHmm`) are recognised and dropped quietly:
            // they never represented a real session, so there's nothing to
            // reconstruct via the resume prompt.
            var sessionDropped = false;
            if (!string.IsNullOrWhiteSpace(sessionName) && !cli.IsCompatibleSessionName(sessionName))
            {
                var isLegacyPlaceholder = IsPlaceholderSessionSlug(sessionName);
                _logger.LogInformation(
                    "Dropping {Kind} sessionName '{Session}' for {Cli} job {JobId}",
                    isLegacyPlaceholder ? "legacy placeholder" : "incompatible",
                    sessionName, cli.CliType, jobId);
                sessionName = null;
                _scanner.SetJobSessionName(jobId, null, Entry.Path);
                sessionDropped = !isLegacyPlaceholder;
            }
            var resume = !string.IsNullOrWhiteSpace(sessionName);
            if (!resume && cli.CliType == CliTypes.Copilot)
            {
                sessionName = BuildSessionName(jobId);
                _scanner.SetJobSessionName(jobId, sessionName, Entry.Path);
            }

            var promptPath = Path.Combine(info.FolderPath, "prompt.md");
            var jobFolderPath = info.FolderPath;

            // Resume mode kicks in when the job was already in 3-progress at the
            // moment we were asked to start it — i.e. a previous CLI run crashed,
            // timed out, was killed, or hit an API error before reaching review.
            // Without an explicit resume prompt the agent only sees a fresh
            // "Lies prompt.md und führe den Task aus" message and treats the new
            // turn as a fresh request: it loses the in-flight state, asks "what
            // should I continue with?", and falls back to generic repo context.
            //
            // Two prerequisites for the resume prompt to actually pay off:
            //   (a) we have a real session to load via `-r` (resume == true), OR
            //   (b) we've just dropped a foreign-CLI session — the previous agent
            //       wrote files we can reconstruct from.
            // If neither holds (state is 3-progress but no UUID was ever captured
            // and no foreign session was dropped), the "interrupted" run never
            // produced anything to continue from — sending the resume prompt just
            // makes the agent reply "I don't see an interrupted task" and quit.
            // Treat that case as a fresh start.
            var useResumePrompt = ShouldUseResumePrompt(initialState, resume, sessionDropped);

            string prompt;
            if (useResumePrompt)
            {
                _logger.LogInformation("[taskboard] resume detected for job {JobId} (initialState={State}, sessionDropped={Dropped})",
                    jobId, initialState, sessionDropped);
                if (resume)
                    _logger.LogInformation("[taskboard] restoring session {SessionId}", sessionName);
                else
                    _logger.LogInformation("[taskboard] no compatible session to restore — falling back to fresh session with continuation prompt");
                _logger.LogInformation("[taskboard] restoring job {JobId}", jobId);
                _logger.LogInformation("[taskboard] using working directory {Path}", Entry.RootPath);
                _logger.LogInformation("[taskboard] injecting continuation prompt (jobFolder={JobFolder})", jobFolderPath);
                prompt = BuildResumeContinuationPrompt(jobFolderPath);
            }
            else
            {
                prompt = BuildFreshStartPrompt(promptPath, jobFolderPath);
            }

            _activeCliType = cli.CliType;
            var (execution, cliError) = await cli.StartAsync(jobId, GetJobKey(jobId), prompt, Entry.RootPath, sessionName, resume, info.Model, ct);

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
    /// Sends a follow-up prompt into the Copilot session that was originally created for this job
    /// (via <c>--resume=&lt;sessionName&gt;</c>). The job must already have a recorded sessionName.
    /// Moves the job back to <c>3-progress</c> if it sits in <c>4-review</c> or <c>5-completed</c>.
    /// </summary>
    public async Task<(CliExecution? Execution, string? Error)> ContinueJobAsync(string jobId, string followupPrompt, CancellationToken ct)
    {
        if (_activeJobId != null)
        {
            return (null, $"Runner '{ProjectName}' is already executing job '{_activeJobId}'");
        }

        _processing = true;
        try
        {
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info == null) return (null, "Job not found");
            if (string.IsNullOrWhiteSpace(info.SessionName))
                return (null, "Job has no session to resume — start it once first.");

            var cli = GetCliFor(info);
            if (!cli.IsCompatibleSessionName(info.SessionName))
                return (null, $"Recorded session id '{info.SessionName}' is not a valid {cli.CliType} session — restart the job once so the CLI can record a fresh session UUID.");

            // Bring the job back into 3-progress so the runner workflow stays consistent
            if (info.State is JobStates.Review or JobStates.Completed or JobStates.Ready)
            {
                _scanner.MoveJob(jobId, JobStates.Progress, Entry.Path);
            }

            _activeJobId = jobId;
            NotifyStatus();

            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));

            _logger.LogInformation("[taskboard] continue requested for job {JobId} (user follow-up)", jobId);
            _logger.LogInformation("[taskboard] restoring session {SessionId}", info.SessionName);
            _logger.LogInformation("[taskboard] using working directory {Path}", Entry.RootPath);

            _activeCliType = cli.CliType;
            var (execution, cliError) = await cli.StartAsync(jobId, GetJobKey(jobId), followupPrompt, Entry.RootPath, info.SessionName, true, info.Model, ct);

            if (execution == null)
            {
                _activeJobId = null;
                _activeCliType = null;
                NotifyStatus();
                return (null, cliError ?? $"Failed to resume {cli.CliType} CLI session");
            }

            return (execution, null);
        }
        finally
        {
            _processing = false;
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
            _scanner.SetJobSessionName(_activeJobId, capturedSessionId, Entry.Path);
        }

        // Write CLI output to log file
        var activeInfo = _scanner.FindJob(_activeJobId, Entry.Path);
        if (activeInfo != null) WriteCliLog(activeInfo, cli);

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

    private void WriteCliLog(JobInfo info, ICliExecutionService cli)
    {
        try
        {
            Directory.CreateDirectory(JobPaths.LogsDir(info.FolderPath));
            var logPath = JobPaths.CliOutputLog(info.FolderPath);

            var output = cli.GetOutput(info.JobKey);
            var logContent = string.Join(Environment.NewLine,
                output.Select(l => $"[{l.Timestamp:HH:mm:ss.fff}] [{l.Stream}] {l.Text}"));

            // Append so that continuation sessions accumulate rather than overwrite.
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
                File.AppendAllText(logPath, Environment.NewLine + logContent, System.Text.Encoding.UTF8);
            else
                File.WriteAllText(logPath, logContent, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write CLI log for job {JobId}", info.Id);
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
