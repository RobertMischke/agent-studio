using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Folder-level state transitions for jobs: moving a job between
/// state folders (<c>2-ready</c>, <c>3-progress</c>, …), deleting a
/// job folder entirely, copying a job to a different watched
/// workspace, the one-shot startup migration that creates the state
/// directories and rehomes legacy flat folders, and the bulk
/// reorder-within-state operation.
///
/// All operations on disk; no callers should write to the state
/// folders directly. Reads still go through <see cref="JobScannerService"/>.
/// </summary>
public class JobStateMachine
{
    private readonly JobScannerService _scanner;
    private readonly ILogger<JobStateMachine> _logger;

    public JobStateMachine(JobScannerService scanner, ILogger<JobStateMachine> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    public MoveJobOutcome MoveJob(string jobId, string targetState, string? watchPath = null)
    {
        if (!JobStates.All.Contains(targetState))
            return new MoveJobOutcome(MoveJobStatus.Failure, $"Invalid state: {targetState}");

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);
        if (info.State == targetState) return new MoveJobOutcome(MoveJobStatus.Success);

        var jobFolderName = Path.GetFileName(info.FolderPath);
        var targetDir = Path.Combine(info.WatchPath, targetState, jobFolderName);

        // A pre-existing target folder almost always means a stale duplicate of the same
        // slug was left behind in another state — Directory.Move would throw a generic
        // IOException and the user would see a 404. Detect it up front and surface a
        // clear message so they know what to clean up.
        if (Directory.Exists(targetDir))
        {
            _logger.LogWarning(
                "Cannot move {JobId} to {State}: target folder already exists at {Target}",
                jobId, targetState, targetDir);
            return new MoveJobOutcome(
                MoveJobStatus.TargetFolderExists,
                $"A job folder named '{jobFolderName}' already exists in {targetState}. " +
                "This usually means a stale duplicate was left behind; remove or rename one of the folders and retry.");
        }

        try
        {
            Directory.Move(info.FolderPath, targetDir);
            JobJsonFile.UpdateField(targetDir, "state", targetState, _logger);
            return new MoveJobOutcome(MoveJobStatus.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move job {JobId} to {State}", jobId, targetState);
            return new MoveJobOutcome(MoveJobStatus.Failure, ex.Message);
        }
    }

    /// <summary>
    /// Move a job to <c>2-ready</c> and reorder it to position 1 (next pickup).
    /// Used by the busy-project queue path: when a follow-up arrives for a
    /// non-active job, this promotes the target so the auto-pickup loop will
    /// run it next.
    ///
    /// <para>Other queued jobs that already carry a <see cref="JobInfo.PendingIntent"/>
    /// keep their relative order in front of this one, so the user's earlier
    /// queued intents are not overtaken. Plain queued jobs (no pending
    /// intent) shuffle down by one.</para>
    /// </summary>
    /// <returns>The 1-based position of the target in the new <c>2-ready</c> ordering, or 0 on failure.</returns>
    public int PromoteToReadyTop(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return 0;

        if (info.State != JobStates.Ready)
        {
            var moved = MoveJob(jobId, JobStates.Ready, watchPath);
            if (moved.Status != MoveJobStatus.Success) return 0;
        }

        // Recompute order across all 2-ready jobs in the same project, with
        // the rule above. We only need to bump the moved job; everyone else
        // keeps relative order.
        var ready = _scanner.ScanAllJobs()
            .Where(j => j.WatchPath == info.WatchPath && j.State == JobStates.Ready)
            .OrderBy(j => j.Order)
            .ToList();

        // Build the new ordering: existing pending-intent jobs first (keep
        // their relative order), then the promoted job, then the rest.
        var pendingHead = ready.Where(j => j.Id != jobId && j.PendingIntent != null).ToList();
        var rest = ready.Where(j => j.Id != jobId && j.PendingIntent == null).ToList();
        var target = ready.FirstOrDefault(j => j.Id == jobId)
                     ?? _scanner.FindJob(jobId, watchPath); // post-move re-fetch
        if (target == null) return 0;

        var ordered = new List<JobInfo>();
        ordered.AddRange(pendingHead);
        ordered.Add(target);
        ordered.AddRange(rest);

        var step = 10;
        for (int i = 0; i < ordered.Count; i++)
        {
            JobJsonFile.UpdateOrder(ordered[i].FolderPath, (i + 1) * step, _logger);
        }

        return ordered.FindIndex(j => j.Id == jobId) + 1;
    }

    public bool DeleteJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        try
        {
            Directory.Delete(info.FolderPath, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete job {JobId}", jobId);
            return false;
        }
    }

    public bool ChangeProject(string jobId, string targetWatchPath, string? watchPath = null)
    {
        var entries = _scanner.GetWatchPaths();
        var targetEntry = entries.FirstOrDefault(e => e.Path == targetWatchPath);
        if (targetEntry == null) return false;

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        if (info.WatchPath == targetWatchPath) return true;

        var jobFolderName = Path.GetFileName(info.FolderPath);
        var targetDir = Path.Combine(targetWatchPath, info.State, jobFolderName);

        if (Directory.Exists(targetDir)) return false;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            CopyDirectory(info.FolderPath, targetDir);
            Directory.Delete(info.FolderPath, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change project for job {JobId} to {Path}", jobId, targetWatchPath);
            return false;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    public void EnsureStateFoldersAndMigrate()
    {
        foreach (var entry in _scanner.GetWatchPaths())
        {
            var watchPath = entry.Path;
            if (!Directory.Exists(watchPath))
            {
                Directory.CreateDirectory(watchPath);
            }

            // Rename old unnumbered state folders to numbered ones
            foreach (var (oldName, newName) in JobStates.LegacyFolderMap)
            {
                var oldDir = Path.Combine(watchPath, oldName);
                var newDir = Path.Combine(watchPath, newName);
                if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
                {
                    Directory.Move(oldDir, newDir);
                    _logger.LogInformation("Renamed state folder {Old} → {New}", oldName, newName);
                }
            }

            // Create state folders
            foreach (var state in JobStates.All)
            {
                Directory.CreateDirectory(Path.Combine(watchPath, state));
            }

            // Migrate existing flat job folders into state subfolders
            foreach (var jobDir in Directory.GetDirectories(watchPath))
            {
                var dirName = Path.GetFileName(jobDir);
                if (JobStates.All.Contains(dirName)) continue; // skip state folders themselves
                if (JobStates.LegacyFolderMap.ContainsKey(dirName)) continue; // skip old state folders
                if (dirName.StartsWith('_')) continue;

                var jobJsonPath = Path.Combine(jobDir, "job.json");
                if (!File.Exists(jobJsonPath)) continue;

                try
                {
                    var json = File.ReadAllText(jobJsonPath);
                    var raw = JsonSerializer.Deserialize<JsonElement>(json, JobJsonFile.ReadOpts);
                    var oldState = raw.TryGetProperty("state", out var s) ? s.GetString() ?? "draft" : "draft";
                    var newState = JobStates.MapLegacyState(oldState);

                    var targetDir = Path.Combine(watchPath, newState, dirName);
                    Directory.Move(jobDir, targetDir);
                    JobJsonFile.UpdateField(targetDir, "state", newState, _logger);
                    _logger.LogInformation("Migrated job {Job} from {Old} to {New}", dirName, oldState, newState);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to migrate job folder {Dir}", dirName);
                }
            }
        }
    }

    public bool ReorderJobs(List<JobOrderItem> jobs)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var info = _scanner.FindJob(job.JobId, job.WatchPath);
            if (info == null) continue;
            JobJsonFile.UpdateOrder(info.FolderPath, i + 1, _logger);
        }
        return true;
    }
}
