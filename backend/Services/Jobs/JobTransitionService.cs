using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Application-owned job state transitions that need side effects around the
/// raw folder move. Manual API moves and automatic runner completion both use
/// this service so lifecycle policy stays in code, not in agent prompts.
/// </summary>
public sealed class JobTransitionService
{
    private readonly JobScannerService _scanner;
    private readonly JobStateMachine _states;
    private readonly JobMutationService _mutations;
    private readonly GitService _git;
    private readonly ProjectSettingsService _settings;
    private readonly ILogger<JobTransitionService> _logger;

    /// <summary>
    /// Fires after a successful folder move with the resolved project name,
    /// the job id, the source state (the lane the job was in before the move),
    /// and the target state. Subscribers must be cheap and side-effect-only;
    /// the move itself is already on disk by the time this fires. The
    /// load-bearing subscriber is the runner-active-state clearer wired in
    /// <c>Program.cs</c>: when the moved job was the active one for that
    /// project, the runner's in-memory <c>_activeJobId</c> is reconciled
    /// atomically so the next pickup tick is unblocked.
    /// </summary>
    public event Action<string, string, string, string>? OnJobMoved;

    public JobTransitionService(
        JobScannerService scanner,
        JobStateMachine states,
        JobMutationService mutations,
        GitService git,
        ProjectSettingsService settings,
        ILogger<JobTransitionService> logger)
    {
        _scanner = scanner;
        _states = states;
        _mutations = mutations;
        _git = git;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Moves a job between states. When auto-commit is enabled and the
    /// transition is <c>3-progress -> 4-auto-review</c>, commits the target
    /// working tree first and stamps the produced SHA onto the moved job.
    /// (Post-ADR-0025 the lane is 4-auto-review; the orchestrator's
    /// review-decision pass then decides whether to promote to
    /// 5-human-review.)
    /// </summary>
    public async Task<MoveJobOutcome> MoveAsync(
        string jobId,
        string targetState,
        string? watchPath,
        CancellationToken ct = default)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);

        var shouldAutoCommit =
            info.State == JobStates.Progress &&
            targetState == JobStates.AutoReview &&
            _settings.Get(info.ProjectName).AutoCommit;

        JobCommitInfo? commitToStamp = null;
        if (shouldAutoCommit)
        {
            commitToStamp = await TryAutoCommitAsync(jobId, watchPath, ct);
        }

        var fromState = info.State;
        var projectName = info.ProjectName;

        var outcome = _states.MoveJob(jobId, targetState, watchPath);
        if (outcome.Status == MoveJobStatus.Success && commitToStamp != null)
        {
            var moved = _scanner.FindJob(jobId, watchPath);
            if (moved != null)
            {
                _mutations.SetJobCommitOnFolder(moved.FolderPath, commitToStamp);
            }
        }

        if (outcome.Status == MoveJobStatus.Success && fromState != targetState)
        {
            try
            {
                OnJobMoved?.Invoke(projectName, jobId, fromState, targetState);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OnJobMoved subscriber threw for {JobId} ({From} -> {To})", jobId, fromState, targetState);
            }
        }

        return outcome;
    }

    private async Task<JobCommitInfo?> TryAutoCommitAsync(string jobId, string? watchPath, CancellationToken ct)
    {
        try
        {
            var (result, message) = await _git.AutoCommitAsync(jobId, watchPath, ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Sha))
            {
                _logger.LogInformation("Auto-commit skipped for {JobId}: {Error}", jobId, result.Error);
                return null;
            }

            var files = _git.GetCommitFiles(jobId, watchPath, result.Sha);
            return new JobCommitInfo
            {
                Sha = result.Sha,
                ShortSha = result.Sha.Length > 7 ? result.Sha[..7] : result.Sha,
                Message = message,
                FilesChanged = files.Count,
                Files = files.Select(f => f.Path).ToList(),
                At = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-commit threw for {JobId}. Moving without a recorded SHA.", jobId);
            return null;
        }
    }
}
