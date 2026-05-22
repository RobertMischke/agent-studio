using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Diagnostics;
using OrchestratorApi.Services.Jobs;
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
///   <see cref="JobTransitionService.MoveAsync"/>, then clear the
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
    private readonly JobScannerService _scanner;
    private readonly JobTransitionService _transitions;
    private readonly JobMutationService _mutations;
    private readonly GitService _git;
    private readonly BackendFileLogSink _logSink;
    private readonly BackendFileLoggerOptions _logOptions;
    private readonly ILogger<CrashRecoveryService> _logger;
    private readonly IJsonlAppender _appender;

    public CrashRecoveryService(
        JobScannerService scanner,
        JobTransitionService transitions,
        JobMutationService mutations,
        GitService git,
        BackendFileLogSink logSink,
        IOptions<BackendFileLoggerOptions> logOptions,
        ILogger<CrashRecoveryService> logger,
        IJsonlAppender? appender = null)
    {
        _scanner = scanner;
        _transitions = transitions;
        _mutations = mutations;
        _git = git;
        _logSink = logSink;
        _logOptions = logOptions.Value;
        _logger = logger;
        _appender = appender ?? new JsonlAppender();
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
            // recently active job in 3-progress (if any).
            RecoverOrphanChanges(entry, decisions);
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
        var progressDir = Path.Combine(entry.Path, JobStates.Progress);
        if (!Directory.Exists(progressDir)) return;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            ct.ThrowIfCancellationRequested();
            var marker = CompletionMarker.TryRead(jobFolder, _logger);
            if (marker == null) continue;

            var jobJsonPath = Path.Combine(jobFolder, "job.json");
            if (!File.Exists(jobJsonPath))
            {
                CompletionMarker.Clear(jobFolder, _logger);
                continue;
            }

            var jobId = Path.GetFileName(jobFolder);
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
        // 3-progress by lastProgressAt. We deliberately read job.json
        // straight from disk: at boot time the JobScannerService's overlay
        // has not warmed up yet, and we need a single field.
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

        var commit = _git.CrashRecoveryCommit(entry.Name, repoRoot, message);
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

        // Attach the commit reference to the job's job.json so the UI shows
        // the recovered SHA on the card. Only when we have a target job.
        if (jobId != null && jobFolder != null && !string.IsNullOrWhiteSpace(commit.Sha))
        {
            _mutations.SetJobCommitOnFolder(jobFolder, new JobCommitInfo
            {
                Sha = commit.Sha!,
                ShortSha = commit.Sha!.Length > 7 ? commit.Sha[..7] : commit.Sha,
                Message = $"crash-recovery: orphan changes for {jobId}",
                FilesChanged = 0,
                Files = new List<string>(),
                At = DateTime.UtcNow
            });
        }
    }

    private static (string? JobId, string? JobFolder) FindMostRecentlyActiveProgressJob(WatchPathEntry entry)
    {
        var progressDir = Path.Combine(entry.Path, JobStates.Progress);
        if (!Directory.Exists(progressDir)) return (null, null);

        string? bestId = null;
        string? bestFolder = null;
        DateTime bestAt = DateTime.MinValue;

        foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
        {
            var jobJsonPath = Path.Combine(jobFolder, "job.json");
            if (!File.Exists(jobJsonPath)) continue;

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
                    // Fall back to job.json mtime; better than nothing.
                    at = File.GetLastWriteTimeUtc(jobJsonPath);
                }

                if (at > bestAt)
                {
                    bestAt = at;
                    bestId = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : Path.GetFileName(jobFolder);
                    bestFolder = jobFolder;
                }
            }
            catch
            {
                // Ignore unreadable job.json entries; recovery is best-effort.
            }
        }

        return (bestId, bestFolder);
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
        catch
        {
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
}
