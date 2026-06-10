using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Diagnostics;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Persistence;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Boot-time recovery doctrine: after a backend crash mid-job, the next
/// boot must leave the system in a state where no agent evidence is lost
/// and the runner can resume cleanly. Two mechanisms cooperate (ADR-0020):
///
/// <list type="number">
///   <item><b>Completion markers.</b> The runner writes a tiny
///   <c>completion-marker.json</c> into the job folder right before the
///   <c>3-progress -&gt; 4-review</c> move. A marker that survives into
///   the next boot means the runner crashed between "decided" and
///   "moved"; we pick up where it left off via
///   <see cref="TaskTransitionService.MoveAsync"/>, then clear the
///   marker.</item>
///   <item><b>Orphan changes.</b> If the project's working tree contains
///   uncommitted changes that map to a known job folder
///   (most-recently-active job in <c>3-progress</c> by <c>lastProgressAt</c>),
///   we commit them with a fixed <c>crash-recovery</c> author tag so the
///   work isn't silently overwritten by the next agent run. Recovery
///   never pushes; that is still the user's gate.</item>
/// </list>
///
/// Every recovery decision is appended to
/// <c>logs/backend/recovery.jsonl</c> for after-the-fact inspection and
/// also surfaced through the structured backend log so Layer 3 system
/// review picks it up.
///
/// <para>
/// Runs once at boot, before the first <see cref="ProjectRunner"/> tick,
/// so the runner sees the recovered state on its first scan and a
/// second crash mid-recovery is itself recoverable on the next boot.
/// </para>
/// </summary>
public sealed class CrashRecoveryService
{
    private readonly TaskScannerService _scanner;
    private readonly TaskTransitionService _transitions;
    private readonly TaskMutationService _mutations;
    private readonly GitService _git;
    private readonly BackendFileLogSink _logSink;
    private readonly BackendFileLoggerOptions _logOptions;
    private readonly ILogger<CrashRecoveryService> _logger;
    private readonly IJsonlAppender _appender;
    private readonly PickupLockFile _pickupLock;

    public CrashRecoveryService(
        TaskScannerService scanner,
        TaskTransitionService transitions,
        TaskMutationService mutations,
        GitService git,
        BackendFileLogSink logSink,
        IOptions<BackendFileLoggerOptions> logOptions,
        ILogger<CrashRecoveryService> logger,
        IJsonlAppender? appender = null,
        PickupLockFile? pickupLock = null)
    {
        _scanner = scanner;
        _transitions = transitions;
        _mutations = mutations;
        _git = git;
        _logSink = logSink;
        _logOptions = logOptions.Value;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
        _pickupLock = pickupLock ?? new PickupLockFile(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PickupLockFile>.Instance);
    }

    /// <summary>Path of the recovery audit log. Absolute, alongside daily backend logs.</summary>
    public string RecoveryLogPath =>
        Path.Combine(Path.GetFullPath(_logOptions.LogDirectory), "recovery.jsonl");

    /// <summary>
    /// Run one full recovery sweep across all watched projects. Returns the
    /// list of recovery decisions taken so a caller (boot path, tests) can
    /// assert against the result without re-parsing the JSONL log.
    /// </summary>
    public async Task<IReadOnlyList<RecoveryDecision>> RecoverAsync(CancellationToken ct = default)
    {
        var decisions = new List<RecoveryDecision>();

        foreach (var entry in _scanner.GetWatchPaths())
        {
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            // Phase 1: complete pending transitions for any 3-progress jobs
            // whose marker survived the crash.
            await RecoverCompletionMarkersAsync(entry, decisions, ct);

            // Phase 2: rescue orphan working-tree changes onto the most
            // recently active job in 3-progress (if any). MUST run before
            // Phase 3, which requeues the interrupted job out of 3-progress;
            // the orphan-attribution here needs that job still in the lane to
            // pin the rescued commit to the right job.
            RecoverOrphanChanges(entry, decisions);

            // Phase 3: requeue runs that were interrupted mid-flight (a backend
            // restart / crash after the runner stamped the pickup lock but
            // before it released it). Such a job is stranded in 3-progress with
            // a stale .pickup-lock.json and (often) an empty logs/ dir. Clear
            // the stale lock and move it back to 2-ready so the next pickup
            // tick starts it cleanly, instead of letting a silent retry loop
            // feed the auto-failure circuit-breaker.
            await RecoverInterruptedRunsAsync(entry, decisions, ct);
        }

        // One INFO line per boot keeps the daily log scan-friendly even when
        // recovery had nothing to do.
        _logger.LogInformation(
            "CrashRecoveryService: completed boot sweep with {Count} decision(s).",
            decisions.Count);

        return decisions;
    }

    private async Task RecoverCompletionMarkersAsync(
        WatchPathEntry entry,
        List<RecoveryDecision> decisions,
        CancellationToken ct)
    {
        var progressDir = Path.Combine(entry.Path, TaskStates.Progress);
        if (!Directory.Exists(progressDir)) return;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            ct.ThrowIfCancellationRequested();
            var marker = CompletionMarker.TryRead(jobFolder, _logger);
            if (marker == null) continue;

            var jobJsonPath = Path.Combine(jobFolder, "task.json");
            if (!File.Exists(jobJsonPath))
            {
                CompletionMarker.Clear(jobFolder, _logger);
                continue;
            }

            var jobId = Path.GetFileName(jobFolder);

            // A run that wrote a completion marker but crashed before its
            // finally-block released the pickup lock leaves a stale lock that
            // would otherwise ride along into 4-auto-review on the move below.
            // Clear it here so "no stale .pickup-lock.json by any exit path"
            // holds for the completion-marker recovery path too.
            TryClearStaleLock(jobFolder, entry, jobId, decisions);

            try
            {
                // The transition's auto-commit hook will pick up any
                // uncommitted work for this project under the project's
                // configured author. Recovery doesn't tag those commits;
                // the marker existing is the signal that the agent's run
                // completed normally. The crash-recovery tag only applies
                // to orphan changes (Phase 2) where no completion marker
                // was found.
                var moveOutcome = await _transitions.MoveAsync(jobId, marker.TargetState, entry.Path, ct);
                if (moveOutcome.Status == MoveJobStatus.Success)
                {
                    var movedInfo = _scanner.FindJob(jobId, entry.Path);
                    if (movedInfo != null) CompletionMarker.Clear(movedInfo.FolderPath, _logger);

                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.TransitionCompleted,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = marker.TargetState,
                        Reason = $"completion-marker survived crash (executionStatus={marker.ExecutionStatus ?? "?"}, agentOutcome={marker.AgentOutcome ?? "?"})"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogInformation(
                        "CrashRecoveryService: completed transition {JobId} -> {Target} (project {Project})",
                        jobId, marker.TargetState, entry.Name);
                }
                else
                {
                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.TransitionFailed,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = marker.TargetState,
                        Reason = $"transition refused: {moveOutcome.Status} {moveOutcome.Message}"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogWarning(
                        "CrashRecoveryService: could not finish transition for {JobId}: {Status} {Message}",
                        jobId, moveOutcome.Status, moveOutcome.Message);
                }
            }
            catch (Exception ex)
            {
                var decision = new RecoveryDecision
                {
                    At = DateTime.UtcNow,
                    Kind = RecoveryDecisionKinds.TransitionFailed,
                    ProjectName = entry.Name,
                    JobId = jobId,
                    TargetState = marker.TargetState,
                    Reason = $"exception: {ex.Message}"
                };
                decisions.Add(decision);
                AppendRecoveryEntry(decision);
                _logger.LogError(ex, "CrashRecoveryService: transition for {JobId} threw", jobId);
            }
        }
    }

    private void RecoverOrphanChanges(WatchPathEntry entry, List<RecoveryDecision> decisions)
    {
        var repoRoot = _git.ResolveRepoRootForProject(entry.Name);
        if (string.IsNullOrWhiteSpace(repoRoot)) return;
        if (!_git.RepoHasUncommittedChanges(repoRoot)) return;

        // Attribute orphan changes to the most-recently-active job in
        // 3-progress by lastProgressAt. We deliberately read task.json
        // straight from disk: at boot time the TaskScannerService's overlay
        // has not warmed up yet, and we need a single field.
        //
        // Cross-lane lookup (drift rule `orphan-detection-checks-other-lanes`,
        // 2026-05-12 boot-race): the candidate 3-progress folder is skipped
        // when the same slug already lives in a later lane. That folder is a
        // mid-move casualty whose real twin already completed; attributing a
        // rescued commit to it would write the commit-info onto a folder that
        // is about to be cleaned up.
        var (jobId, jobFolder) = FindMostRecentlyActiveProgressJob(entry);

        // C1 (2026-05-22): if there is NO active 3-progress job to
        // attribute the changes to, the uncommitted state is much more
        // likely to be a human-driven editor session than a crashed
        // agent run. The previous behaviour swept those edits into a
        // crash-recovery commit on the next backend restart, which
        // surprised the operator (and bit me twice during the
        // F1-F11 work). Log a hint instead and let the human decide.
        if (jobId == null)
        {
            var hint = new RecoveryDecision
            {
                At = DateTime.UtcNow,
                Kind = RecoveryDecisionKinds.OrphanSkipped,
                ProjectName = entry.Name,
                JobId = null,
                Reason = "uncommitted changes present but no 3-progress job to attribute to; skipped to avoid clobbering an active editor session"
            };
            decisions.Add(hint);
            AppendRecoveryEntry(hint);
            _logger.LogInformation(
                "CrashRecoveryService: project {Project} has uncommitted changes but no active 3-progress job — leaving them for the operator. Set ATP_CRASH_RECOVERY_AGGRESSIVE=1 to re-enable the old auto-commit behaviour.",
                entry.Name);
            if (Environment.GetEnvironmentVariable("ATP_CRASH_RECOVERY_AGGRESSIVE") != "1")
            {
                return;
            }
        }

        var message = jobId == null
            ? $"chore(crash-recovery): rescue orphan changes for project {entry.Name}\n\n" +
              "Recovered uncommitted working-tree state after a backend crash. No active job\n" +
              "found in 3-progress; review and re-attribute manually if needed. (Aggressive\n" +
              "mode — ATP_CRASH_RECOVERY_AGGRESSIVE=1.)"
            : $"chore(crash-recovery): rescue orphan changes for {jobId}\n\n" +
              $"Recovered uncommitted working-tree state after a backend crash. Last active\n" +
              $"3-progress job in project {entry.Name} (by lastProgressAt) was {jobId}.";

        var scope = PlanCrashRecoveryCommitScope(entry, jobId, jobFolder, repoRoot);
        if (scope.Scope == CrashRecoveryCommitScope.None)
        {
            var skipped = new RecoveryDecision
            {
                At = DateTime.UtcNow,
                Kind = RecoveryDecisionKinds.OrphanSkipped,
                ProjectName = entry.Name,
                JobId = jobId,
                Reason = "uncommitted changes present but every dirty path predates the active job's first session event; skipped to avoid sweeping foreign work into crash recovery"
            };
            decisions.Add(skipped);
            AppendRecoveryEntry(skipped);
            _logger.LogInformation(
                "CrashRecoveryService: project {Project} has uncommitted changes for active job {JobId}, but no dirty path belongs to the session window; leaving them untouched.",
                entry.Name, jobId);
            return;
        }

        var pathspecs = scope.Scope == CrashRecoveryCommitScope.Scoped ? scope.Paths : null;
        if (pathspecs is { Count: > 0 })
        {
            _logger.LogInformation(
                "CrashRecoveryService: scoped orphan recovery for project {Project} job {JobId} to {Count} task-attributable path(s); foreign dirty changes left untouched.",
                entry.Name, jobId, pathspecs.Count);
        }

        var commit = _git.CrashRecoveryCommit(entry.Name, repoRoot, message, pathspecs);
        if (!commit.Success)
        {
            // "Nothing to commit" can race in if a concurrent process committed
            // between RepoHasUncommittedChanges and CrashRecoveryCommit; treat
            // it as a no-op rather than a failure.
            if (commit.Error != null && commit.Error.Contains("Nothing to commit", StringComparison.OrdinalIgnoreCase))
                return;

            var failed = new RecoveryDecision
            {
                At = DateTime.UtcNow,
                Kind = RecoveryDecisionKinds.OrphanCommitFailed,
                ProjectName = entry.Name,
                JobId = jobId,
                Reason = $"git commit failed: {commit.Error}"
            };
            decisions.Add(failed);
            AppendRecoveryEntry(failed);
            _logger.LogWarning(
                "CrashRecoveryService: orphan commit failed for project {Project}: {Error}",
                entry.Name, commit.Error);
            return;
        }

        var decision = new RecoveryDecision
        {
            At = DateTime.UtcNow,
            Kind = RecoveryDecisionKinds.OrphanCommitted,
            ProjectName = entry.Name,
            JobId = jobId,
            CommitSha = commit.Sha,
            Reason = jobId == null
                ? "orphan changes committed; no active 3-progress job to attribute to"
                : $"orphan changes committed and attributed to {jobId}"
        };
        decisions.Add(decision);
        AppendRecoveryEntry(decision);
        _logger.LogInformation(
            "CrashRecoveryService: committed orphan changes for project {Project} as {Sha}",
            entry.Name, commit.Sha);

        // Attach the commit reference to the job's task.json so the UI shows
        // the recovered SHA on the card. Only when we have a target job.
        if (jobId != null && jobFolder != null && !string.IsNullOrWhiteSpace(commit.Sha))
        {
            _mutations.SetJobCommitOnFolder(jobFolder, new TaskCommitInfo
            {
                Sha = commit.Sha!,
                ShortSha = commit.Sha!.Length > 7 ? commit.Sha[..7] : commit.Sha,
                Message = $"crash-recovery: orphan changes for {jobId}",
                FilesChanged = pathspecs?.Count ?? 0,
                Files = pathspecs?.ToList() ?? new List<string>(),
                At = DateTime.UtcNow
            });
        }
    }

    private enum CrashRecoveryCommitScope
    {
        All,
        None,
        Scoped,
    }

    private sealed record CrashRecoveryCommitPlan(CrashRecoveryCommitScope Scope, IReadOnlyList<string> Paths);

    private CrashRecoveryCommitPlan PlanCrashRecoveryCommitScope(
        WatchPathEntry entry,
        string? jobId,
        string? jobFolder,
        string repoRoot)
    {
        var all = new CrashRecoveryCommitPlan(CrashRecoveryCommitScope.All, []);
        if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(jobFolder))
            return all;

        var firstActivityUtc = ReadFirstSessionEventAt(jobFolder);
        if (firstActivityUtc == null)
            return all;

        var status = _git.GetStatusForRepoRoot(repoRoot);
        if (!status.IsRepo || status.Files.Count == 0)
            return all;

        var scoped = new List<string>();
        foreach (var file in status.Files)
        {
            if (string.IsNullOrWhiteSpace(file.Path)) continue;
            var fullPath = Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!File.Exists(fullPath))
                {
                    scoped.Add(file.Path);
                    continue;
                }

                if (File.GetLastWriteTimeUtc(fullPath) >= firstActivityUtc.Value)
                    scoped.Add(file.Path);
            }
            catch
            {
                scoped.Add(file.Path);
            }
        }

        if (scoped.Count == 0)
            return new CrashRecoveryCommitPlan(CrashRecoveryCommitScope.None, []);

        return new CrashRecoveryCommitPlan(CrashRecoveryCommitScope.Scoped, scoped);
    }

    private static DateTime? ReadFirstSessionEventAt(string jobFolder)
    {
        var path = TaskPaths.SessionEventsLog(jobFolder);
        if (!File.Exists(path)) return null;

        DateTime? first = null;
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = JsonSerializer.Deserialize<SessionEvent>(line, TaskJsonFile.ReadOpts);
                if (evt == null || evt.Ts == default) continue;
                var ts = evt.Ts.Kind == DateTimeKind.Utc ? evt.Ts : evt.Ts.ToUniversalTime();
                if (first == null || ts < first.Value) first = ts;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "CrashRecoveryService: Best-effort recovery: ignore torn session-event rows.");
                // Best-effort recovery: ignore torn session-event rows.
            }
        }

        return first;
    }

    /// <summary>
    /// Phase 3: requeue any 3-progress job whose run was interrupted mid-flight.
    /// The signal is a <c>.pickup-lock.json</c> left in the folder whose owning
    /// pid is no longer running on this host - the runner stamps that lock right
    /// before it spawns the CLI and releases it in a finally block, so a stale
    /// lock at boot means the backend died between those two points. We clear
    /// the lock and move the job 3-progress -> 2-ready so the next pickup tick
    /// starts it cleanly. A live foreign lock (another backend on the same
    /// workspace) is left untouched - that run is not ours to requeue.
    /// </summary>
    private async Task RecoverInterruptedRunsAsync(
        WatchPathEntry entry,
        List<RecoveryDecision> decisions,
        CancellationToken ct)
    {
        var progressDir = Path.Combine(entry.Path, TaskStates.Progress);
        if (!Directory.Exists(progressDir)) return;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            ct.ThrowIfCancellationRequested();

            // No task.json means this is a folder-shaped orphan, not a real
            // interrupted run; leave it for the runner's own orphan sweep.
            if (!File.Exists(Path.Combine(jobFolder, "task.json"))) continue;

            // Only a present lock signals an interrupted run. A 3-progress job
            // with no lock was never mid-spawn (or was already released); the
            // strict-iteration picker resumes it in place. Don't disturb it.
            var existing = _pickupLock.Peek(jobFolder);
            if (existing == null) continue;

            // ClearIfStale removes the lock only when its owner pid is dead on
            // this host. A live foreign owner returns false and is left alone.
            if (!_pickupLock.ClearIfStale(jobFolder))
            {
                _logger.LogInformation(
                    "CrashRecoveryService: pickup lock on {Folder} is held by a live owner ({Backend} pid={Pid}); leaving the run in place",
                    jobFolder, existing.BackendName, existing.Pid);
                continue;
            }

            var jobId = Path.GetFileName(jobFolder);

            // Leave a human-readable trace in the job folder so the requeue is
            // never silent. This is the diagnostic the 2026-05-30 incident was
            // missing entirely (empty logs/, no cli-output.log).
            WriteInterruptedRunDiagnostic(jobFolder, existing);

            try
            {
                var moveOutcome = await _transitions.MoveAsync(jobId, TaskStates.Ready, entry.Path, ct);
                if (moveOutcome.Status == MoveJobStatus.Success)
                {
                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.RunInterruptedRequeued,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = TaskStates.Ready,
                        Reason = $"run interrupted mid-flight (stale pickup lock from {existing.BackendName} pid={existing.Pid}); requeued to 2-ready and stale lock cleared"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogInformation(
                        "CrashRecoveryService: requeued interrupted run {JobId} -> 2-ready (project {Project}); cleared stale lock from {Backend} pid={Pid}",
                        jobId, entry.Name, existing.BackendName, existing.Pid);
                }
                else
                {
                    var decision = new RecoveryDecision
                    {
                        At = DateTime.UtcNow,
                        Kind = RecoveryDecisionKinds.RunInterruptedRequeueFailed,
                        ProjectName = entry.Name,
                        JobId = jobId,
                        TargetState = TaskStates.Ready,
                        Reason = $"requeue refused: {moveOutcome.Status} {moveOutcome.Message}"
                    };
                    decisions.Add(decision);
                    AppendRecoveryEntry(decision);
                    _logger.LogWarning(
                        "CrashRecoveryService: could not requeue interrupted run {JobId}: {Status} {Message}",
                        jobId, moveOutcome.Status, moveOutcome.Message);
                }
            }
            catch (Exception ex)
            {
                var decision = new RecoveryDecision
                {
                    At = DateTime.UtcNow,
                    Kind = RecoveryDecisionKinds.RunInterruptedRequeueFailed,
                    ProjectName = entry.Name,
                    JobId = jobId,
                    TargetState = TaskStates.Ready,
                    Reason = $"exception: {ex.Message}"
                };
                decisions.Add(decision);
                AppendRecoveryEntry(decision);
                _logger.LogError(ex, "CrashRecoveryService: requeue for {JobId} threw", jobId);
            }
        }
    }

    /// <summary>
    /// Clear a stale pickup lock if one is present and its owner pid is dead.
    /// Records a <see cref="RecoveryDecisionKinds.StalePickupLockCleared"/>
    /// decision so the cleanup is auditable. Used by the completion-marker
    /// path; the interrupted-run path clears inline because it also moves the
    /// folder. Best-effort: a live foreign lock is left untouched.
    /// </summary>
    private void TryClearStaleLock(
        string jobFolder,
        WatchPathEntry entry,
        string jobId,
        List<RecoveryDecision> decisions)
    {
        var existing = _pickupLock.Peek(jobFolder);
        if (existing == null) return;
        if (!_pickupLock.ClearIfStale(jobFolder)) return;

        var decision = new RecoveryDecision
        {
            At = DateTime.UtcNow,
            Kind = RecoveryDecisionKinds.StalePickupLockCleared,
            ProjectName = entry.Name,
            JobId = jobId,
            Reason = $"cleared stale pickup lock from {existing.BackendName} pid={existing.Pid}"
        };
        decisions.Add(decision);
        AppendRecoveryEntry(decision);
    }

    /// <summary>
    /// Append a one-line interrupted-run note to the job's cli-output.log so an
    /// operator reading the Activity Log sees why the run stopped and was
    /// requeued, instead of an empty logs/ dir. Best-effort.
    /// </summary>
    private void WriteInterruptedRunDiagnostic(string jobFolder, PickupLockInfo lockInfo)
    {
        try
        {
            var logsDir = Path.Combine(jobFolder, TaskPaths.LogsDirName);
            Directory.CreateDirectory(logsDir);
            var logPath = Path.Combine(logsDir, TaskPaths.CliOutputLogFileName);
            var now = DateTime.UtcNow;
            var line =
                $"[{now:HH:mm:ss.fff}] [system] [taskboard] Run interrupted by a backend restart " +
                $"(stale pickup lock from {lockInfo.BackendName} pid={lockInfo.Pid}, acquired {lockInfo.AcquiredAt:u}). " +
                "Requeued to 2-ready; this interruption does not count as a task failure.";
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 0)
                File.AppendAllText(logPath, Environment.NewLine + line, Encoding.UTF8);
            else
                File.WriteAllText(logPath, line, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CrashRecoveryService: failed to write interrupted-run diagnostic for {Folder}", jobFolder);
        }
    }

    private static (string? JobId, string? TaskFolder) FindMostRecentlyActiveProgressJob(WatchPathEntry entry)
    {
        var progressDir = Path.Combine(entry.Path, TaskStates.Progress);
        if (!Directory.Exists(progressDir)) return (null, null);

        string? bestId = null;
        string? bestFolder = null;
        DateTime bestAt = DateTime.MinValue;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            var jobJsonPath = Path.Combine(jobFolder, "task.json");
            if (!File.Exists(jobJsonPath)) continue;

            var slug = Path.GetFileName(jobFolder);
            // Mid-move casualty: same slug already lives in a later lane, so
            // the real run already finished. Attributing the rescued commit
            // here would tag a folder that is about to be reconciled away.
            if (SlugExistsInDownstreamLane(entry.Path, slug)) continue;

            try
            {
                var json = File.ReadAllText(jobJsonPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                DateTime at;
                if (root.TryGetProperty("lastProgressAt", out var lpEl)
                    && lpEl.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(lpEl.GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsedLp))
                {
                    at = parsedLp;
                }
                else
                {
                    // Fall back to task.json mtime; better than nothing.
                    at = File.GetLastWriteTimeUtc(jobJsonPath);
                }

                if (at > bestAt)
                {
                    bestAt = at;
                    bestId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : Path.GetFileName(jobFolder);
                    bestFolder = jobFolder;
                }
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "CrashRecoveryService: Ignore unreadable task.json entries; recovery is best-effort.");
                // Ignore unreadable task.json entries; recovery is best-effort.
            }
        }

        return (bestId, bestFolder);
    }

    /// <summary>
    /// Cross-lane lookup matched to the one in
    /// <see cref="StaleProgressArchiver"/>: lanes where a finished twin of a
    /// 3-progress slug can live. 3a-failed-pickup is deliberately excluded so
    /// a pre-fix phantom marker can never mask a real attribution candidate.
    /// Drift-watchlist rule <c>orphan-detection-checks-other-lanes</c>.
    /// </summary>
    private static readonly string[] DownstreamLanesForOrphanReconciliation =
    {
        TaskStates.AutoReview,
        TaskStates.HumanReview,
        TaskStates.Escalated,
        TaskStates.Completed,
        TaskStates.Archive,
    };

    private static bool SlugExistsInDownstreamLane(string watchPath, string slug)
    {
        foreach (var lane in DownstreamLanesForOrphanReconciliation)
        {
            if (Directory.Exists(Path.Combine(watchPath, lane, slug))) return true;
        }
        return false;
    }

    private void AppendRecoveryEntry(RecoveryDecision decision)
    {
        try
        {
            _appender.AppendAsync(RecoveryLogPath, decision, RecoveryJsonOptions).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CrashRecoveryService: failed to append recovery.jsonl");
        }

        // Mirror onto the structured backend log so daily-log scrapers
        // and Layer 3 review pick it up alongside the rest of boot.
        try
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} INFO  Backend.CrashRecovery " +
                       $"{decision.Kind} project={decision.ProjectName} jobId={decision.JobId ?? "(none)"} " +
                       $"sha={decision.CommitSha ?? "-"} target={decision.TargetState ?? "-"} reason=\"{decision.Reason}\"";
            _logSink.WriteRaw(line);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "CrashRecoveryService: Logging the recovery must never crash the boot path.");
            // Logging the recovery must never crash the boot path.
        }
    }

    private static readonly JsonSerializerOptions RecoveryJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>One row in <c>logs/backend/recovery.jsonl</c>.</summary>
public sealed record RecoveryDecision
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    [JsonPropertyName("targetState")] public string? TargetState { get; init; }
    [JsonPropertyName("commitSha")] public string? CommitSha { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>String constants for <see cref="RecoveryDecision.Kind"/>.</summary>
public static class RecoveryDecisionKinds
{
    public const string TransitionCompleted = "transition-completed";
    public const string TransitionFailed = "transition-failed";
    public const string OrphanCommitted = "orphan-committed";
    public const string OrphanCommitFailed = "orphan-commit-failed";
    /// <summary>
    /// C1: orphan changes were detected but skipped because no 3-progress
    /// job was active to attribute them to (= likely a human editor
    /// session, not a crashed agent run). Set
    /// ATP_CRASH_RECOVERY_AGGRESSIVE=1 to re-enable the old auto-commit.
    /// </summary>
    public const string OrphanSkipped = "orphan-skipped";
    /// <summary>An interrupted mid-flight run was requeued from 3-progress back to 2-ready.</summary>
    public const string RunInterruptedRequeued = "run-interrupted-requeued";
    /// <summary>An interrupted run was detected but the requeue move failed.</summary>
    public const string RunInterruptedRequeueFailed = "run-interrupted-requeue-failed";
    /// <summary>A stale .pickup-lock.json (owner pid dead on this host) was removed at boot.</summary>
    public const string StalePickupLockCleared = "stale-pickup-lock-cleared";
}
