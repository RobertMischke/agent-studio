using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;

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
        if (info.State == targetState) return new MoveJobOutcome(MoveJobStatus.Success, NewFolderPath: info.FolderPath);

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
            // Cycle 2: invalidate the cache synchronously so a POST-then-GET
            // sequence (e.g. drag a card, frontend re-polls) never sees the
            // pre-move snapshot. The 250 ms FileSystemWatcher debounce alone
            // is too slow for that round-trip.
            _scanner.InvalidateCache();
            // Hand the post-move path back to the caller so chat-log writes
            // and follow-up files cannot land in the now-vanished source
            // folder via a stale FindJob result (see MoveJobOutcome docs).
            return new MoveJobOutcome(MoveJobStatus.Success, NewFolderPath: targetDir);
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
        _scanner.InvalidateCache();

        return ordered.FindIndex(j => j.Id == jobId) + 1;
    }

    /// <summary>
    /// Archive a job folder by absolute source path under <c>7-archive/</c>
    /// with a new folder slug. Used by the boot-time stale-progress sweep
    /// (ADR-0020 follow-up) for the residual case where a folder is genuinely
    /// nothing but a directory entry (no <c>job.json</c>, no
    /// <c>cli-output.log</c>): see <see cref="MoveFolderToFailedPickup"/> for
    /// the loud path that handles real orphans.
    /// </summary>
    /// <remarks>
    /// Takes a folder path rather than a jobId because empty stale folders have
    /// no job.json and therefore are not visible to <see cref="JobScannerService"/>.
    /// Still routes the move + state-field update through this state machine so
    /// callers never write to the state folders directly.
    /// </remarks>
    public MoveJobOutcome ArchiveFolder(string sourceFolder, string newSlug)
        => MoveFolderToState(sourceFolder, newSlug, JobStates.Archive);

    /// <summary>
    /// Move a stale <c>3-progress</c> folder into <c>3a-failed-pickup</c> with
    /// a new folder slug. ADR-0028: pickup failures are loud, not silent;
    /// orphan and empty folders that the boot sweep used to hide in
    /// <c>7-archive</c> now land in the visible failed-pickup lane so the
    /// user sees what the runner could not finish.
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="ArchiveFolder"/> but targets
    /// <see cref="JobStates.FailedPickup"/>. Empty stale folders may not have
    /// a <c>job.json</c>; in that case a placeholder is written so the lane
    /// can render a card and the state-field invariant holds.
    /// </remarks>
    public MoveJobOutcome MoveFolderToFailedPickup(string sourceFolder, string newSlug)
        => MoveFolderToState(sourceFolder, newSlug, JobStates.FailedPickup, writePlaceholderJobJson: true);

    /// <summary>
    /// Inverse of <see cref="MoveFolderToFailedPickup"/>: lift a folder
    /// out of <c>3a-failed-pickup</c> back into a live lane (default
    /// <c>2-ready</c>) and rename it back to its pre-dead-letter slug.
    /// Surfaced as <c>POST /api/jobs/{id}/restore-from-failed-pickup</c>
    /// to close the gap that previously forced an operator to fall back
    /// to <c>mv</c> + manual rename - exactly the bypass the
    /// <see cref="OrchestratorApi.Tests.Architecture.JobFolderAccessIsolationTest"/>
    /// and the AGENTS.md "API first" rule are meant to stop.
    ///
    /// <para>Single-state-machine principle: the move + the slug rewrite
    /// both flow through <see cref="MoveFolderToState"/>, the same helper
    /// the dead-letter path uses. No new <see cref="Directory.Move"/>
    /// call site, so the architecture test stays green.</para>
    ///
    /// <para>Idempotency: if the slug is not in
    /// <see cref="JobStates.FailedPickup"/> the call returns
    /// <see cref="RestoreFromFailedPickupStatus.NoOp"/> when the original
    /// slug already exists in <paramref name="targetState"/> (the
    /// expected "already restored" case) and
    /// <see cref="RestoreFromFailedPickupStatus.NotFound"/> when the
    /// slug is genuinely unknown.</para>
    ///
    /// <para>The pickup-attempt counter is held in
    /// <c>ProjectRunner._pickupAttempts</c> and was already cleared at
    /// dead-letter time (see <c>DeadLetterUnrecoverableFolder</c>), so
    /// the next pickup attempt on the restored slug starts at 0 without
    /// any extra reset here. If a future schema adds a persisted counter
    /// to <c>job.json</c>, reset it inside this method.</para>
    /// </summary>
    /// <param name="jobId">The dead-letter slug under
    /// <c>3a-failed-pickup</c>, e.g. <c>foo-pickup-failed-2026-05-08</c>.</param>
    /// <param name="watchPath">Workspace root that disambiguates the slug
    /// when the same id appears in two projects.</param>
    /// <param name="keepDeadLetterSlug">When <c>true</c>, keep the
    /// <c>-pickup-failed-&lt;utc&gt;</c> suffix on the restored folder.
    /// Useful when the operator wants the trail visible in the slug
    /// itself.</param>
    /// <param name="targetState">Target lane. Defaults to
    /// <see cref="JobStates.Ready"/>.</param>
    public RestoreFromFailedPickupOutcome RestoreFromFailedPickup(
        string jobId,
        string? watchPath,
        bool keepDeadLetterSlug,
        string? targetState = null)
    {
        var resolvedTargetState = string.IsNullOrWhiteSpace(targetState) ? JobStates.Ready : targetState!;
        if (!JobStates.All.Contains(resolvedTargetState))
        {
            return new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.Failure,
                Message: $"Invalid target state: {resolvedTargetState}");
        }

        // Parse the original slug back out of the dead-letter name. When
        // the input does not match the dead-letter shape the call may
        // still be a legitimate restore (operator renamed manually before
        // calling this); fall back to the input slug as both source and
        // original so the move can still proceed.
        var hasDeadLetterShape = PickupFailureLog.TryParseFailedPickupSlug(jobId, out var parsedOriginal);
        var originalSlug = hasDeadLetterShape ? parsedOriginal : jobId;
        var restoredSlug = keepDeadLetterSlug ? jobId : originalSlug;

        var info = _scanner.FindJob(jobId, watchPath);

        // Idempotency: nothing to restore in 3a-failed-pickup; check
        // whether the original slug is already in the target lane.
        if (info == null || info.State != JobStates.FailedPickup)
        {
            var existing = _scanner.FindJob(restoredSlug, watchPath);
            if (existing != null && existing.State == resolvedTargetState)
            {
                return new RestoreFromFailedPickupOutcome(
                    RestoreFromFailedPickupStatus.NoOp,
                    RestoredSlug: restoredSlug,
                    OriginalSlug: originalSlug,
                    SourceSlug: jobId,
                    Message: $"Folder is already in {resolvedTargetState} under '{restoredSlug}'.");
            }

            return new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.NotFound,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: $"No folder found in {JobStates.FailedPickup} with slug '{jobId}'.");
        }

        if (!hasDeadLetterShape && !keepDeadLetterSlug)
        {
            // The folder is in 3a-failed-pickup but its slug does not
            // match the auto-generated dead-letter pattern. Refuse to
            // guess at a rename; surface the slug-shape problem to the
            // caller, who can retry with keepDeadLetterSlug=true.
            return new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.InvalidSlug,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: $"Slug '{jobId}' does not match the dead-letter shape '<slug>-pickup-failed-<yyyy-mm-dd>'. " +
                         "Retry with keepDeadLetterSlug=true to restore without a rename.");
        }

        var outcome = MoveFolderToState(info.FolderPath, restoredSlug, resolvedTargetState);
        return outcome.Status switch
        {
            MoveJobStatus.Success => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.Success,
                RestoredSlug: restoredSlug,
                OriginalSlug: originalSlug,
                SourceSlug: jobId),
            MoveJobStatus.NotFound => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.NotFound,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: outcome.Message),
            MoveJobStatus.TargetFolderExists => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.TargetFolderExists,
                RestoredSlug: restoredSlug,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: outcome.Message),
            _ => new RestoreFromFailedPickupOutcome(
                RestoreFromFailedPickupStatus.Failure,
                OriginalSlug: originalSlug,
                SourceSlug: jobId,
                Message: outcome.Message)
        };
    }

    private MoveJobOutcome MoveFolderToState(string sourceFolder, string newSlug, string targetState, bool writePlaceholderJobJson = false)
    {
        if (string.IsNullOrWhiteSpace(newSlug))
            return new MoveJobOutcome(MoveJobStatus.Failure, "Slug must not be empty");
        if (!JobStates.All.Contains(targetState))
            return new MoveJobOutcome(MoveJobStatus.Failure, $"Invalid state: {targetState}");
        if (!Directory.Exists(sourceFolder))
            return new MoveJobOutcome(MoveJobStatus.NotFound);

        var stateDir = Path.GetDirectoryName(sourceFolder);
        var watchPath = stateDir != null ? Path.GetDirectoryName(stateDir) : null;
        if (string.IsNullOrEmpty(watchPath))
            return new MoveJobOutcome(MoveJobStatus.Failure, "Source folder is not under a state directory");

        var targetDir = Path.Combine(watchPath, targetState, newSlug);
        if (Directory.Exists(targetDir))
        {
            _logger.LogWarning(
                "Cannot move {Source} to {State}/{Slug}: target folder already exists at {Target}",
                sourceFolder, targetState, newSlug, targetDir);
            return new MoveJobOutcome(
                MoveJobStatus.TargetFolderExists,
                $"A folder named '{newSlug}' already exists in {targetState}.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDir)!);
            Directory.Move(sourceFolder, targetDir);
            var jobJsonPath = Path.Combine(targetDir, "job.json");
            if (File.Exists(jobJsonPath))
            {
                JobJsonFile.UpdateField(targetDir, "state", targetState, _logger);
            }
            else if (writePlaceholderJobJson)
            {
                // The empty-stale path lacks any metadata. Synthesize a minimal
                // job.json so the scanner sees the card and the state-field
                // invariant ("every job folder has a state field on disk") holds.
                var placeholder = $"{{\"id\":\"{newSlug}\",\"title\":\"{newSlug}\",\"state\":\"{targetState}\",\"order\":1,\"agent\":\"unknown\"}}";
                File.WriteAllText(jobJsonPath, placeholder);
            }
            _scanner.InvalidateCache();
            return new MoveJobOutcome(MoveJobStatus.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move {Source} to {State}/{Slug}", sourceFolder, targetState, newSlug);
            return new MoveJobOutcome(MoveJobStatus.Failure, ex.Message);
        }
    }

    public bool DeleteJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        try
        {
            Directory.Delete(info.FolderPath, true);
            _scanner.InvalidateCache();
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
            _scanner.InvalidateCache();
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

    /// <summary>
    /// One-shot migration step for ADR-0025: if <paramref name="oldName"/>
    /// exists in the workspace, move every job folder under it to
    /// <paramref name="newName"/>, rewrite each job.json's <c>state</c>
    /// field, and remove the now-empty old folder. Idempotent: on the
    /// next boot the old folder no longer exists and the call is a no-op.
    /// </summary>
    private void MigrateNumberedLane(string watchPath, string oldName, string newName)
    {
        var oldDir = Path.Combine(watchPath, oldName);
        if (!Directory.Exists(oldDir)) return;

        var newDir = Path.Combine(watchPath, newName);
        Directory.CreateDirectory(newDir);

        var jobFolders = Directory.GetDirectories(oldDir);
        var movedJobs = 0;
        foreach (var jobFolder in jobFolders)
        {
            var folderName = Path.GetFileName(jobFolder);
            var targetFolder = Path.Combine(newDir, folderName);
            if (Directory.Exists(targetFolder))
            {
                _logger.LogWarning(
                    "ADR-0025 migration: target {Target} already exists; leaving source {Source} in place for manual reconciliation",
                    targetFolder, jobFolder);
                continue;
            }
            try
            {
                Directory.Move(jobFolder, targetFolder);
                if (File.Exists(Path.Combine(targetFolder, "job.json")))
                {
                    JobJsonFile.UpdateField(targetFolder, "state", newName, _logger);
                }
                movedJobs++;
                LastNumberedLaneMigrationCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ADR-0025 migration: failed to move {Source} to {Target}",
                    jobFolder, targetFolder);
            }
        }

        // Try to remove the old (now-empty) lane folder so the next boot has
        // nothing to do. Leftover non-job items (loose files, hidden state)
        // keep it around; that's fine, the migration is still idempotent
        // because we only act on directories with a job.json shape.
        try
        {
            if (!Directory.EnumerateFileSystemEntries(oldDir).Any())
            {
                Directory.Delete(oldDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ADR-0025 migration: could not remove emptied lane {OldDir}",
                oldDir);
        }

        _logger.LogInformation(
            "ADR-0025 migration: moved {Count} job(s) from {Old} to {New} in {Workspace}",
            movedJobs, oldName, newName, watchPath);
    }

    /// <summary>
    /// Total number of jobs whose <c>state</c> field was rewritten by the
    /// most recent ADR-0025 numbered-lane migration sweep
    /// (<c>4-review → 4-auto-review</c>, <c>5-completed → 6-completed</c>,
    /// <c>6-archive → 7-archive</c>). Useful for reporting back on the
    /// migration without mining the logs. Reset at the start of every
    /// <see cref="EnsureStateFoldersAndMigrate"/> call so the value
    /// reflects this boot's work and not cumulative startups.
    /// </summary>
    public int LastNumberedLaneMigrationCount { get; private set; }

    public void EnsureStateFoldersAndMigrate()
    {
        LastNumberedLaneMigrationCount = 0;

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

            // ADR-0025: rename pre-three-stage-review numbered lanes to the
            // new layout. Order matters: free 6- before reusing it for
            // completed, free 5- before completed could overwrite a stray
            // 5-human-review someone tried to seed by hand. The check is
            // idempotent - on the second boot the old names no longer
            // exist so each branch is a no-op.
            MigrateNumberedLane(watchPath, "6-archive", JobStates.Archive);
            MigrateNumberedLane(watchPath, "5-completed", JobStates.Completed);
            MigrateNumberedLane(watchPath, "4-review", JobStates.AutoReview);

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

    /// <summary>
    /// Place <paramref name="jobId"/> at slot <paramref name="targetIndex"/>
    /// in its current lane (within the same project / watch path) and
    /// rewrite every job's <c>order</c> field to a dense 1..N sequence so
    /// the resulting order is stable. Used by the move endpoint when a
    /// drag-and-drop cross-lane drop carries a desired insertion slot:
    /// without this the moved folder keeps its source-lane <c>order</c>
    /// value and snaps to a position the user did not choose.
    /// </summary>
    /// <returns><c>true</c> when the job was found and the lane was
    /// rewritten; <c>false</c> when the job cannot be located.</returns>
    public bool SetOrderInLane(string jobId, string? watchPath, int targetIndex)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        var laneJobs = _scanner.ScanAllJobs()
            .Where(j => j.WatchPath == info.WatchPath && j.State == info.State)
            .OrderBy(j => j.Order)
            .ThenBy(j => j.Id, StringComparer.Ordinal)
            .ToList();

        var moved = laneJobs.FirstOrDefault(j => j.Id == jobId);
        if (moved == null) return false;

        var others = laneJobs.Where(j => j.Id != jobId).ToList();
        var slot = Math.Clamp(targetIndex, 0, others.Count);
        var ordered = new List<JobInfo>(others.Count + 1);
        ordered.AddRange(others.Take(slot));
        ordered.Add(moved);
        ordered.AddRange(others.Skip(slot));

        for (int i = 0; i < ordered.Count; i++)
        {
            JobJsonFile.UpdateOrder(ordered[i].FolderPath, i + 1, _logger);
        }
        _scanner.InvalidateCache();
        return true;
    }

    public bool ReorderJobs(List<JobOrderItem> jobs)
    {
        if (jobs.Count == 0) return true;

        // One scan, then dict lookup per item. The previous shape called
        // _scanner.FindJob(jobId, watchPath) inside the loop, and FindJob
        // re-runs ScanAllJobs (full disk walk) every time — O(N x M) for an
        // N-card reorder on an M-job board, which made consecutive drags
        // and the click-after-drop interaction lag visibly. Lookup key is
        // (watchPath, jobId) so the same id in two workspaces still resolves
        // to the right folder. Case-insensitive watchPath match mirrors
        // FindJob's own comparison.
        var byKey = new Dictionary<(string watchPath, string jobId), string>();
        foreach (var info in _scanner.ScanAllJobs())
        {
            byKey[(info.WatchPath.ToLowerInvariant(), info.Id)] = info.FolderPath;
        }

        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var watchKey = (job.WatchPath ?? string.Empty).ToLowerInvariant();
            string? folder;
            if (!byKey.TryGetValue((watchKey, job.JobId), out folder))
            {
                // Fall back to id-only lookup when the caller omits watchPath
                // (legacy clients) — pick the first match in a deterministic
                // order so the operation is still useful.
                folder = byKey
                    .Where(kv => kv.Key.jobId == job.JobId)
                    .Select(kv => kv.Value)
                    .FirstOrDefault();
                if (folder == null) continue;
            }
            JobJsonFile.UpdateOrder(folder, i + 1, _logger);
        }
        _scanner.InvalidateCache();
        return true;
    }
}
