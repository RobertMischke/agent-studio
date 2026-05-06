using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Boot-time sweep that surfaces <c>3-progress</c> folders the orchestrator
/// has lost track of (ADR-0001: at most one running task per project).
///
/// <para>Pairs with <see cref="CrashRecoveryService"/>: that one rescues
/// uncommitted file changes and finishes pending transitions. This one moves
/// the *folders* themselves so the lane visually returns to the one-job
/// promise after a backend that died mid-run, an <c>update-stable.sh</c>
/// cycle that killed the runner, or a progress-first pickup that failed to
/// resume.</para>
///
/// Decision shape per folder:
/// <list type="bullet">
///   <item>Latest activity = <c>logs/cli-output.log</c> mtime, falling back
///   to <c>job.json</c> mtime. Folders with neither are treated as
///   <c>epoch 0</c>.</item>
///   <item>If younger than <c>Supervisor:StuckResumeWindowMinutes</c> (default
///   60), the progress-first pickup will resume it and the sweep leaves it
///   alone. Setting the window to <c>0</c> turns the sweep off.</item>
///   <item>If older AND a completion sentinel
///   (<c>[[TASK_DONE]]</c> / <c>[[TASK_BLOCKED]]</c> /
///   <c>[[TASK_NEEDS_INPUT]]</c>) appears in the last 50 lines of
///   <c>cli-output.log</c>, the sweep finishes the
///   <c>3-progress -&gt; 4-auto-review</c> transition the orchestrator missed
///   and appends a <c>[recovered-from-stuck-progress]</c> note to the chat
///   log.</item>
///   <item>If older with no sentinel and at least one file present, the
///   folder is moved to
///   <c>3a-failed-pickup/&lt;original-slug&gt;-orphan-&lt;utc-date&gt;/</c>
///   (ADR-0028: pickup failures are loud, not silent). A
///   <c>failed-pickup-reason.md</c> placard records the kind, last activity,
///   and sweep timestamp.</item>
///   <item>If older AND empty (no <c>job.json</c>, no <c>cli-output.log</c>),
///   the folder is moved to
///   <c>3a-failed-pickup/&lt;original-slug&gt;-empty-&lt;utc-date&gt;/</c>
///   with the same placard. Empty stale folders gain a synthetic
///   <c>job.json</c> so the kanban can render the card.</item>
/// </list>
///
/// <para>Single-state-machine authority: every move goes through
/// <see cref="JobStateMachine"/> or <see cref="JobTransitionService"/>; the
/// sweep never moves folders directly.</para>
///
/// <para>Idempotent: a second run on the same lane is a no-op (the candidates
/// are no longer in <c>3-progress</c>).</para>
///
/// <para>Defensive guard: cross-checks the runner's current
/// <c>activeJobId</c> per project before any move so a folder that is the
/// active job (would be unusual at boot, but possible if invoked later) is
/// never touched.</para>
/// </summary>
public sealed class StaleProgressArchiver
{
    private readonly JobScannerService _scanner;
    private readonly JobStateMachine _states;
    private readonly JobTransitionService _transitions;
    private readonly OrchestratorChatLog _chatLog;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StaleProgressArchiver> _logger;

    public const int DefaultStuckResumeWindowMinutes = 60;
    private const int SentinelTailLineWindow = 50;

    /// <summary>Test seam: when set, replaces the runner-status lookup so unit
    /// tests can simulate an active job without standing up <see cref="TaskRunnerService"/>.</summary>
    internal Func<RunnerStatus?>? StatusProviderOverride { get; set; }

    public StaleProgressArchiver(
        JobScannerService scanner,
        JobStateMachine states,
        JobTransitionService transitions,
        OrchestratorChatLog chatLog,
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<StaleProgressArchiver> logger)
    {
        _scanner = scanner;
        _states = states;
        _transitions = transitions;
        _chatLog = chatLog;
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Runs the sweep across all watched projects. Returns one decision per
    /// folder examined (including <c>fresh</c> verdicts) so callers and tests
    /// can assert on what happened without re-parsing the JSONL log.
    /// </summary>
    public async Task<IReadOnlyList<StaleProgressDecision>> SweepAsync(CancellationToken ct = default)
    {
        var decisions = new List<StaleProgressDecision>();

        var windowMinutes = _configuration.GetValue("Supervisor:StuckResumeWindowMinutes", DefaultStuckResumeWindowMinutes);
        if (windowMinutes <= 0)
        {
            _logger.LogInformation(
                "StaleProgressArchiver: disabled (Supervisor:StuckResumeWindowMinutes = {Window}); skipping sweep.",
                windowMinutes);
            return decisions;
        }

        var threshold = TimeSpan.FromMinutes(windowMinutes);
        var now = DateTime.UtcNow;

        var activeByProject = SafeGetActiveJobIds();

        foreach (var entry in _scanner.GetWatchPaths())
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(entry.Path) || !Directory.Exists(entry.Path)) continue;

            var progressDir = Path.Combine(entry.Path, JobStates.Progress);
            if (!Directory.Exists(progressDir)) continue;

            activeByProject.TryGetValue(entry.Name, out var activeJobId);

            foreach (var jobFolder in Directory.EnumerateDirectories(progressDir))
            {
                ct.ThrowIfCancellationRequested();
                var slug = Path.GetFileName(jobFolder);

                if (!string.IsNullOrEmpty(activeJobId)
                    && string.Equals(slug, activeJobId, StringComparison.OrdinalIgnoreCase))
                {
                    decisions.Add(Skip(entry.Name, slug, "matches active job", at: now));
                    continue;
                }

                var (lastActivity, isEmpty) = MeasureFolder(jobFolder);
                var age = now - lastActivity;
                if (age < threshold)
                {
                    decisions.Add(new StaleProgressDecision
                    {
                        At = now,
                        Kind = StaleProgressDecisionKinds.Fresh,
                        ProjectName = entry.Name,
                        Slug = slug,
                        AgeSeconds = (long)age.TotalSeconds,
                        Reason = "within resume window"
                    });
                    continue;
                }

                StaleProgressDecision decision;
                if (isEmpty)
                {
                    decision = MoveToFailedPickup(entry, jobFolder, slug, now, kind: FailureKind.Empty, lastActivity: lastActivity);
                }
                else if (TryFindSentinel(jobFolder, out var keyword))
                {
                    decision = await RecoverViaTransitionAsync(entry, jobFolder, slug, keyword!, now, ct);
                }
                else
                {
                    decision = MoveToFailedPickup(entry, jobFolder, slug, now, kind: FailureKind.Orphan, lastActivity: lastActivity);
                }

                decisions.Add(decision);
                AppendOrphanRecoveryEntry(decision);
            }
        }

        var actionable = decisions.Count(d =>
            d.Kind == StaleProgressDecisionKinds.MovedToFailedPickup ||
            d.Kind == StaleProgressDecisionKinds.MoveToFailedPickupFailed ||
            d.Kind == StaleProgressDecisionKinds.RecoveredToReview ||
            d.Kind == StaleProgressDecisionKinds.RecoveryFailed);
        _logger.LogInformation(
            "StaleProgressArchiver: completed boot sweep with {Total} candidate(s), {Actionable} actionable.",
            decisions.Count, actionable);

        return decisions;
    }

    private enum FailureKind { Orphan, Empty }

    private Dictionary<string, string?> SafeGetActiveJobIds()
    {
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Resolve TaskRunnerService lazily so the archiver can run before
            // the runner host has wired itself fully (DI graph still holds it).
            // Tests inject StatusProviderOverride directly to avoid the heavy
            // service.
            RunnerStatus? status = StatusProviderOverride != null
                ? StatusProviderOverride()
                : (_services.GetService(typeof(TaskRunnerService)) as TaskRunnerService)?.GetStatus();
            if (status?.Projects == null) return map;
            foreach (var (projectName, projectStatus) in status.Projects)
            {
                map[projectName] = projectStatus?.ActiveJobId;
            }
        }
        catch (Exception ex)
        {
            // The defensive guard must never block the sweep; an empty map
            // means "do not skip anything" which is safe at boot time.
            _logger.LogWarning(ex, "StaleProgressArchiver: could not read runner status; treating no jobs as active.");
        }
        return map;
    }

    private static (DateTime LastActivity, bool IsEmpty) MeasureFolder(string jobFolder)
    {
        var cliLog = JobPaths.CliOutputLog(jobFolder);
        var jobJson = Path.Combine(jobFolder, "job.json");
        var hasLog = File.Exists(cliLog);
        var hasJson = File.Exists(jobJson);

        if (!hasLog && !hasJson)
        {
            // Either truly empty, or holds only stray files. Treat as empty
            // for archival purposes; lastActivity = epoch so it always crosses
            // the threshold.
            return (DateTime.MinValue.ToUniversalTime(), IsEmpty: true);
        }

        DateTime stamp;
        if (hasLog)
        {
            try { stamp = File.GetLastWriteTimeUtc(cliLog); }
            catch { stamp = File.GetLastWriteTimeUtc(jobJson); }
        }
        else
        {
            stamp = File.GetLastWriteTimeUtc(jobJson);
        }
        return (stamp, IsEmpty: false);
    }

    private static readonly Regex SentinelRegex = new(
        @"\[\[TASK_(?<keyword>DONE|BLOCKED|NEEDS_INPUT)(?::[^\]]*)?\]\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryFindSentinel(string jobFolder, out string? keyword)
    {
        keyword = null;
        var path = JobPaths.CliOutputLog(jobFolder);
        if (!File.Exists(path)) return false;

        try
        {
            var tail = ReadTailLines(path, SentinelTailLineWindow);
            for (int i = tail.Count - 1; i >= 0; i--)
            {
                var match = SentinelRegex.Match(tail[i]);
                if (match.Success)
                {
                    keyword = match.Groups["keyword"].Value.ToUpperInvariant();
                    return true;
                }
            }
        }
        catch
        {
            // Best-effort. Treat unreadable logs as "no sentinel"; the folder
            // will be archived as orphan rather than recovered.
        }
        return false;
    }

    private static List<string> ReadTailLines(string path, int maxLines)
    {
        // Read the whole file - cli-output.log is small (KB to a few MB);
        // a streaming tail would be over-engineering for boot-time use.
        var all = File.ReadAllLines(path);
        if (all.Length <= maxLines) return new List<string>(all);
        return new List<string>(all[(all.Length - maxLines)..]);
    }

    private async Task<StaleProgressDecision> RecoverViaTransitionAsync(
        WatchPathEntry entry, string jobFolder, string slug, string keyword,
        DateTime now, CancellationToken ct)
    {
        // Use jobId from job.json when available; folder name is the canonical
        // fallback (the application uses folder name as jobId everywhere).
        var jobId = TryReadJobId(jobFolder) ?? slug;
        try
        {
            var outcome = await _transitions.MoveAsync(jobId, JobStates.AutoReview, entry.Path, ct);
            if (outcome.Status != MoveJobStatus.Success)
            {
                return new StaleProgressDecision
                {
                    At = now,
                    Kind = StaleProgressDecisionKinds.RecoveryFailed,
                    ProjectName = entry.Name,
                    Slug = slug,
                    JobId = jobId,
                    SentinelKeyword = keyword,
                    Reason = $"transition to {JobStates.AutoReview} refused: {outcome.Status} {outcome.Message}"
                };
            }

            // Append the chat-log note on the moved folder so the protocol
            // pane shows the recovery alongside the agent's own output.
            var movedInfo = _scanner.FindJob(jobId, entry.Path);
            if (movedInfo != null)
            {
                _chatLog.AppendSupervisor(
                    movedInfo,
                    "recovered-from-stuck-progress",
                    $"Boot sweep finished a missed transition: agent had emitted [[TASK_{keyword}]] " +
                    "in 3-progress and the orchestrator never moved the folder. Promoted to 4-auto-review.");
            }

            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RecoveredToReview,
                ProjectName = entry.Name,
                Slug = slug,
                JobId = jobId,
                SentinelKeyword = keyword,
                TargetState = JobStates.AutoReview,
                Reason = $"sentinel TASK_{keyword} survived; finished missed transition"
            };
        }
        catch (Exception ex)
        {
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RecoveryFailed,
                ProjectName = entry.Name,
                Slug = slug,
                JobId = jobId,
                SentinelKeyword = keyword,
                Reason = $"exception: {ex.Message}"
            };
        }
    }

    private StaleProgressDecision MoveToFailedPickup(
        WatchPathEntry entry,
        string jobFolder,
        string slug,
        DateTime now,
        FailureKind kind,
        DateTime lastActivity)
    {
        // ADR-0028: orphan and empty 3-progress folders move to the visible
        // 3a-failed-pickup lane, not silently into 7-archive. The lane card
        // carries a failed-pickup-reason.md placard so the user sees what the
        // boot sweep saw.
        var suffix = kind == FailureKind.Empty ? "empty" : "orphan";
        var datePart = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newSlug = $"{slug}-{suffix}-{datePart}";
        var attempt = newSlug;
        for (int i = 2; Directory.Exists(Path.Combine(entry.Path, JobStates.FailedPickup, attempt)) && i < 1000; i++)
        {
            attempt = $"{newSlug}-{i}";
        }

        var outcome = _states.MoveFolderToFailedPickup(jobFolder, attempt);
        if (outcome.Status != MoveJobStatus.Success)
        {
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.MoveToFailedPickupFailed,
                ProjectName = entry.Name,
                Slug = slug,
                FailedPickupSlug = attempt,
                Reason = $"move to {JobStates.FailedPickup} refused: {outcome.Status} {outcome.Message}"
            };
        }

        var movedFolder = Path.Combine(entry.Path, JobStates.FailedPickup, attempt);
        TryWriteReasonPlacard(movedFolder, kind, lastActivity, now);

        return new StaleProgressDecision
        {
            At = now,
            Kind = StaleProgressDecisionKinds.MovedToFailedPickup,
            ProjectName = entry.Name,
            Slug = slug,
            FailedPickupSlug = attempt,
            FailureKind = suffix,
            TargetState = JobStates.FailedPickup,
            Reason = kind == FailureKind.Empty
                ? "stale folder with no job.json or cli-output.log; surfaced in 3a-failed-pickup"
                : "stale folder past resume window with no completion sentinel; surfaced in 3a-failed-pickup"
        };
    }

    private void TryWriteReasonPlacard(string folder, FailureKind kind, DateTime lastActivity, DateTime sweepAt)
    {
        try
        {
            var placard = BuildReasonPlacard(kind, lastActivity, sweepAt);
            File.WriteAllText(Path.Combine(folder, "failed-pickup-reason.md"), placard);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "StaleProgressArchiver: could not write failed-pickup-reason.md in {Folder}", folder);
        }
    }

    private static string BuildReasonPlacard(FailureKind kind, DateTime lastActivity, DateTime sweepAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Pickup failure");
        sb.AppendLine();
        sb.Append("**Kind**: ").AppendLine(kind == FailureKind.Empty ? "empty" : "orphan");
        sb.Append("**Detected at**: ").AppendLine(sweepAt.ToString("o", CultureInfo.InvariantCulture));
        if (lastActivity > DateTime.MinValue.ToUniversalTime())
        {
            sb.Append("**Last activity**: ").AppendLine(lastActivity.ToString("o", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.AppendLine("**Last activity**: none (folder had no job.json or cli-output.log)");
        }
        sb.AppendLine();
        sb.AppendLine(kind == FailureKind.Empty
            ? "The boot-time sweep found a `3-progress` folder with no `job.json` and no `cli-output.log`. The runner could not resume it; the orchestrator never finished a transition. Surfacing it loudly in `3a-failed-pickup` so it is not silently archived."
            : "The boot-time sweep found a `3-progress` folder past the resume window with no completion sentinel (`[[TASK_DONE]]` / `[[TASK_BLOCKED]]` / `[[TASK_NEEDS_INPUT]]`) in the tail of `cli-output.log`. Either the run died mid-stream and the orchestrator did not see it, or the agent never emitted a sentinel. Surfacing it loudly in `3a-failed-pickup` so it is not silently archived.");
        return sb.ToString();
    }

    private static StaleProgressDecision Skip(string projectName, string slug, string reason, DateTime at)
        => new()
        {
            At = at,
            Kind = StaleProgressDecisionKinds.Skipped,
            ProjectName = projectName,
            Slug = slug,
            Reason = reason
        };

    private static string? TryReadJobId(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "job.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        }
        catch { return null; }
    }

    private void AppendOrphanRecoveryEntry(StaleProgressDecision decision)
    {
        // Resolve the workspace root the same way the supervisor logs do.
        // Without TaskRepository configured the JSONL is silently skipped;
        // the structured backend logger still mirrors the decision.
        var workspaceRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            _logger.LogDebug(
                "StaleProgressArchiver: TaskRepository not configured; skipping orphan-recoveries.jsonl entry for {Slug}.",
                decision.Slug);
            return;
        }

        try
        {
            var dir = Path.Combine(workspaceRoot, "logs");
            Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(decision, JsonOptions);
            File.AppendAllText(Path.Combine(dir, "orphan-recoveries.jsonl"), line + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StaleProgressArchiver: failed to append orphan-recoveries.jsonl");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>One row in <c>&lt;workspace&gt;/logs/orphan-recoveries.jsonl</c>.</summary>
/// <remarks>
/// Schema: <c>docs/schemas/orphan-recovery.schema.json</c>.
/// </remarks>
public sealed record StaleProgressDecision
{
    [JsonPropertyName("at")] public DateTime At { get; init; }
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("projectName")] public string ProjectName { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("jobId")] public string? JobId { get; init; }
    /// <summary>Renamed slug under <c>3a-failed-pickup</c> after a successful loud-not-archived move (ADR-0028).</summary>
    [JsonPropertyName("failedPickupSlug")] public string? FailedPickupSlug { get; init; }
    /// <summary><c>orphan</c> or <c>empty</c>: which boot-sweep verdict produced the move.</summary>
    [JsonPropertyName("failureKind")] public string? FailureKind { get; init; }
    [JsonPropertyName("targetState")] public string? TargetState { get; init; }
    [JsonPropertyName("sentinelKeyword")] public string? SentinelKeyword { get; init; }
    [JsonPropertyName("ageSeconds")] public long? AgeSeconds { get; init; }
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>String constants for <see cref="StaleProgressDecision.Kind"/>.</summary>
public static class StaleProgressDecisionKinds
{
    /// <summary>Folder is younger than the resume window; left for progress-first pickup.</summary>
    public const string Fresh = "fresh";
    /// <summary>Folder is the runner's currently active job; left alone.</summary>
    public const string Skipped = "skipped";
    /// <summary>Sentinel found in tail; folder moved to <c>4-auto-review</c>.</summary>
    public const string RecoveredToReview = "recovered-to-review";
    /// <summary>Sentinel found but the transition refused or threw.</summary>
    public const string RecoveryFailed = "recovery-failed";
    /// <summary>Stale orphan or empty folder moved to <c>3a-failed-pickup</c> (ADR-0028).</summary>
    public const string MovedToFailedPickup = "moved-to-failed-pickup";
    /// <summary>Move to <c>3a-failed-pickup</c> refused (e.g. target slug already exists).</summary>
    public const string MoveToFailedPickupFailed = "move-to-failed-pickup-failed";
}
