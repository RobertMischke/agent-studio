using System.Collections.Concurrent;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public class TaskRunnerService : BackgroundService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TaskRunnerService> _logger;
    private readonly JobScannerService _scanner;
    private readonly CopilotCliService _cli;
    private readonly ConcurrentDictionary<string, ProjectRunner> _runners = new();

    public event Action<string, ProjectRunnerStatus>? OnRunnerStatusChanged;

    public TaskRunnerService(
        IConfiguration config,
        ILogger<TaskRunnerService> logger,
        JobScannerService scanner,
        CopilotCliService cli)
    {
        _config = config;
        _logger = logger;
        _scanner = scanner;
        _cli = cli;
    }

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

            var runner = new ProjectRunner(entry.Name, entry, _logger, _scanner, _cli);
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

    public async Task<(CliExecution? Execution, string? Error)> StartJobAsync(string jobId, string? watchPath = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) return (null, $"No runner configured for project '{info.ProjectName}' — check RootPath in WatchPaths config");

        if (!_cli.IsAvailable()) return (null, "Copilot CLI is not installed or not on PATH");

        return await runner.StartJobManualAsync(jobId, ct);
    }

    /// <summary>
    /// Resumes the Copilot session bound to a job and feeds it a follow-up prompt.
    /// The job must already have a sessionName recorded in <c>job.json</c> (i.e. it was started before).
    /// </summary>
    public async Task<(CliExecution? Execution, string? Error)> ContinueJobAsync(string jobId, string followupPrompt, string? watchPath = null, CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");
        if (string.IsNullOrWhiteSpace(info.SessionName))
            return (null, "This job has no session yet — start it once before continuing.");

        var runner = _runners.Values.FirstOrDefault(r => r.Entry.Name == info.ProjectName);
        if (runner == null) return (null, $"No runner configured for project '{info.ProjectName}'");
        if (!_cli.IsAvailable()) return (null, "Copilot CLI is not installed or not on PATH");

        return await runner.ContinueJobAsync(jobId, followupPrompt, ct);
    }

    public bool StopJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        return info != null && _cli.Stop(info.JobKey);
    }

    public List<CliOutputLine> GetJobOutput(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        return info != null ? _cli.GetOutput(info.JobKey) : [];
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
    private readonly CopilotCliService _cli;
    private string _mode = "manual";
    private string? _activeJobId;
    private bool _processing;

    public string ProjectName { get; }
    public WatchPathEntry Entry { get; }

    public event Action<ProjectRunnerStatus>? OnStatusChanged;

    public ProjectRunner(string projectName, WatchPathEntry entry, ILogger logger, JobScannerService scanner, CopilotCliService cli)
    {
        ProjectName = projectName;
        Entry = entry;
        _logger = logger;
        _scanner = scanner;
        _cli = cli;

        // Listen for CLI completion to move jobs to review
        _cli.OnFinished += OnCliFinished;
    }

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
        return new ProjectRunnerStatus
        {
            ProjectName = ProjectName,
            Mode = _mode,
            ActiveJobId = _activeJobId,
            ActiveExecution = activeJobKey != null ? _cli.GetExecution(activeJobKey) : null,
            QueuedJobIds = queued
        };
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (_mode is "manual" or "paused") return;
        if (_processing || _activeJobId != null) return;

        // Check if there's a running process for this project
        if (_cli.IsRunningForProject(Entry.RootPath)) return;

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
            // Move job to 3-progress
            var info = _scanner.FindJob(jobId, Entry.Path);
            if (info == null) return (null, "Job not found");

            if (info.State == JobStates.Ready)
            {
                _scanner.MoveJob(jobId, JobStates.Progress, Entry.Path);
                info = _scanner.FindJob(jobId, Entry.Path) ?? info;
            }

            _activeJobId = jobId;
            NotifyStatus();

            // Ensure logs directory exists
            var jobFolder = FindJobFolder(jobId);
            if (jobFolder != null)
            {
                Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));
            }

            // Resolve / persist a stable session name so follow-ups can use --resume
            var sessionName = info.SessionName;
            var resume = !string.IsNullOrWhiteSpace(sessionName);
            if (!resume)
            {
                sessionName = BuildSessionName(jobId);
                _scanner.SetJobSessionName(jobId, sessionName, Entry.Path);
            }

            // Start CLI process
            var prompt = $"Lies @.orchestrator/jobs/3-progress/{jobId}/prompt.md und führe den Task aus.";
            var (execution, cliError) = await _cli.StartAsync(jobId, GetJobKey(jobId), prompt, Entry.RootPath, sessionName, resume, ct);

            if (execution == null)
            {
                _activeJobId = null;
                NotifyStatus();
                return (null, cliError ?? "Failed to start Copilot CLI process");
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

            // Bring the job back into 3-progress so the runner workflow stays consistent
            if (info.State is JobStates.Review or JobStates.Completed or JobStates.Ready)
            {
                _scanner.MoveJob(jobId, JobStates.Progress, Entry.Path);
            }

            _activeJobId = jobId;
            NotifyStatus();

            var jobFolder = FindJobFolder(jobId);
            if (jobFolder != null)
            {
                Directory.CreateDirectory(Path.Combine(jobFolder, "logs"));
            }

            var (execution, cliError) = await _cli.StartAsync(jobId, GetJobKey(jobId), followupPrompt, Entry.RootPath, info.SessionName, true, ct);

            if (execution == null)
            {
                _activeJobId = null;
                NotifyStatus();
                return (null, cliError ?? "Failed to resume Copilot CLI session");
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

    private void OnCliFinished(string jobKey, CliExecution execution)
    {
        if (GetActiveJobKey() != jobKey || _activeJobId == null) return;

        _logger.LogInformation("Job {JobId} finished in project '{Project}' with status {Status}",
            _activeJobId, ProjectName, execution.Status);

        // Persist last token/usage summary (best-effort)
        var usage = _cli.GetLastUsage(jobKey);
        if (usage != null)
        {
            _scanner.UpdateLastUsage(_activeJobId, usage, Entry.Path);
        }

        // Write CLI output to log file
        WriteCliLog(_activeJobId);

        // Move job to 4-review
        _scanner.MoveJob(_activeJobId, JobStates.Review, Entry.Path);

        _activeJobId = null;
        NotifyStatus();
    }

    private void WriteCliLog(string jobId)
    {
        try
        {
            var jobFolder = FindJobFolder(jobId);
            if (jobFolder == null) return;

            var logsDir = Path.Combine(jobFolder, "logs");
            Directory.CreateDirectory(logsDir);

            var output = _cli.GetOutput(GetJobKey(jobId));
            var logContent = string.Join(Environment.NewLine,
                output.Select(l => $"[{l.Timestamp:HH:mm:ss.fff}] [{l.Stream}] {l.Text}"));

            File.WriteAllText(Path.Combine(logsDir, "cli-output.log"), logContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write CLI log for job {JobId}", jobId);
        }
    }

    private string? FindJobFolder(string jobId)
    {
        foreach (var state in JobStates.All)
        {
            var folder = Path.Combine(Entry.Path, state, jobId);
            if (Directory.Exists(folder)) return folder;
        }
        return null;
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
