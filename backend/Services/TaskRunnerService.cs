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
        OrchestratorChatLog chatLog)
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

            var runner = new ProjectRunner(entry.Name, entry, _logger, _scanner, _states, _sessions, _router, _summaryService, _prompts, _transitions, _chatLog);
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

    public async Task<(CliExecution? Execution, string? Error)> StartJobAsync(string jobId, string? watchPath = null, string? modelOverride = null, string? cliTypeOverride = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) return (null, $"No runner configured for project '{info.ProjectName}' - check RootPath in WatchPaths config");

        // Persist CLI type override before validity check so the next iteration picks it up.
        if (!string.IsNullOrWhiteSpace(cliTypeOverride) && cliTypeOverride != info.CliType)
        {
            _mutations.SetJobCliType(jobId, CliTypes.Normalize(cliTypeOverride), watchPath);
            info = _scanner.FindJob(jobId, watchPath) ?? info;
        }

        var cli = _router.Get(info.CliType);
        if (!cli.IsAvailable()) return (null, $"{cli.CliType} CLI is not installed or not on PATH");

        // Persist override on the job so subsequent runs reuse it
        if (!string.IsNullOrWhiteSpace(modelOverride) && modelOverride != info.Model)
        {
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
        }

        return await runner.StartJobManualAsync(jobId, ct);
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
            _mutations.SetJobModel(jobId, modelOverride, watchPath);
        }

        _mutations.AppendContinuationNote(jobId, followupPrompt, watchPath);
        AppendUserPromptToCliLog(info, followupPrompt);

        return await runner.ContinueJobAsync(jobId, followupPrompt, ct);
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
