using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Supervisor;

/// <summary>
/// Read-only observation surface for the per-project supervisor. Returns a
/// <see cref="SupervisorObservation"/> snapshot for one project; calling this
/// cannot mutate runner state, job state, or any file. The hard health checks
/// and the soft reasoner consume the same observation each tick.
/// </summary>
/// <remarks>
/// Quota is intentionally null in this first cut. Wiring it up means depending
/// on <c>QuotaService</c>'s background refresh and adds load to a path that
/// runs every 10 s; we wait until the cadence numbers settle before adding it.
/// </remarks>
public sealed class ProjectObservationService
{
    private readonly TaskRunnerService _taskRunner;
    private readonly JobScannerService _jobScanner;
    private readonly ILogger<ProjectObservationService> _logger;
    private readonly TimeProvider _time;

    public ProjectObservationService(
        TaskRunnerService taskRunner,
        JobScannerService jobScanner,
        ILogger<ProjectObservationService> logger,
        TimeProvider? time = null)
    {
        _taskRunner = taskRunner;
        _jobScanner = jobScanner;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public Task<SupervisorObservation> ObserveAsync(string project, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ct.ThrowIfCancellationRequested();

        var status = _taskRunner.GetStatus();
        var capturedAt = _time.GetUtcNow().UtcDateTime;

        if (status.Projects == null || !status.Projects.TryGetValue(project, out var projectStatus) || projectStatus == null)
        {
            return Task.FromResult(IdleObservation(project, capturedAt));
        }

        var activeJobId = projectStatus.ActiveJobId;
        var runnerStatus = (projectStatus.Mode ?? "unknown") + (activeJobId != null ? " (active)" : string.Empty);

        if (string.IsNullOrEmpty(activeJobId))
        {
            return Task.FromResult(new SupervisorObservation(
                CapturedAt: capturedAt,
                Project: project,
                RunnerStatus: runnerStatus,
                CurrentJobId: null,
                CurrentRunState: null,
                LastProgressAt: null,
                Quota: null,
                RecentDecisions: Array.Empty<SupervisorRecentDecision>(),
                RecentAgentSamples: Array.Empty<string>(),
                ErrorCounts: new SupervisorErrorCounts(0, 0, 0)));
        }

        var info = SafeFindJob(activeJobId);
        var logPath = info != null ? JobPaths.CliOutputLog(info.FolderPath) : null;

        IReadOnlyList<CliOutputLine> lines = Array.Empty<CliOutputLine>();
        if (logPath != null && File.Exists(logPath))
        {
            try
            {
                lines = CliOutputLogParser.ParseFile(logPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ProjectObservationService failed to parse {LogPath} for {Project}", logPath, project);
            }
        }

        var lastProgressAt = ObservationParsing.LatestTimestamp(lines);
        if (lastProgressAt == null && logPath != null && File.Exists(logPath))
        {
            try { lastProgressAt = File.GetLastWriteTimeUtc(logPath); }
            catch { /* swallow; observation is best-effort */ }
        }

        var recentDecisions = ObservationParsing.ExtractRecentDecisions(lines);
        var recentSamples = ObservationParsing.ExtractRecentAgentSamples(lines);
        var errorCounts = ObservationParsing.CountErrors(lines, capturedAt, TimeSpan.FromHours(1));

        return Task.FromResult(new SupervisorObservation(
            CapturedAt: capturedAt,
            Project: project,
            RunnerStatus: runnerStatus,
            CurrentJobId: activeJobId,
            CurrentRunState: info?.State,
            LastProgressAt: lastProgressAt,
            Quota: null,
            RecentDecisions: recentDecisions,
            RecentAgentSamples: recentSamples,
            ErrorCounts: errorCounts));
    }

    private JobInfo? SafeFindJob(string jobId)
    {
        try { return _jobScanner.FindJob(jobId); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ProjectObservationService FindJob failed for {JobId}", jobId);
            return null;
        }
    }

    private static SupervisorObservation IdleObservation(string project, DateTime capturedAt) => new(
        CapturedAt: capturedAt,
        Project: project,
        RunnerStatus: "unknown",
        CurrentJobId: null,
        CurrentRunState: null,
        LastProgressAt: null,
        Quota: null,
        RecentDecisions: Array.Empty<SupervisorRecentDecision>(),
        RecentAgentSamples: Array.Empty<string>(),
        ErrorCounts: new SupervisorErrorCounts(0, 0, 0));
}
