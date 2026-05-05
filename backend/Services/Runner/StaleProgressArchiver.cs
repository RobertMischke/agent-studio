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
/// Boot-time sweep that archives <c>3-progress</c> folders the orchestrator
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
///   <c>3-progress -&gt; 4-review</c> transition the orchestrator missed and
///   appends a <c>[recovered-from-stuck-progress]</c> note to the chat log.</item>
///   <item>If older with no sentinel and at least one file present, archive
///   to <c>6-archive/&lt;original-slug&gt;-orphan-&lt;utc-date&gt;/</c>.</item>
///   <item>If older AND empty (no <c>job.json</c>, no <c>cli-output.log</c>),
///   archive to <c>6-archive/&lt;original-slug&gt;-empty-&lt;utc-date&gt;/</c>.</item>
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
                    decision = ArchiveEmpty(entry, jobFolder, slug, now);
                }
                else if (TryFindSentinel(jobFolder, out var keyword))
                {
                    decision = await RecoverViaTransitionAsync(entry, jobFolder, slug, keyword!, now, ct);
                }
                else
                {
                    decision = ArchiveOrphan(entry, jobFolder, slug, now);
                }

                decisions.Add(decision);
                AppendOrphanRecoveryEntry(decision);
            }
        }

        var actionable = decisions.Count(d =>
            d.Kind == StaleProgressDecisionKinds.ArchivedOrphan ||
            d.Kind == StaleProgressDecisionKinds.ArchivedEmpty ||
            d.Kind == StaleProgressDecisionKinds.RecoveredToReview ||
            d.Kind == StaleProgressDecisionKinds.RecoveryFailed ||
            d.Kind == StaleProgressDecisionKinds.ArchiveFailed);
        _logger.LogInformation(
            "StaleProgressArchiver: completed boot sweep with {Total} candidate(s), {Actionable} actionable.",
            decisions.Count, actionable);

        return decisions;
    }

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
            var outcome = await _transitions.MoveAsync(jobId, JobStates.Review, entry.Path, ct);
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
                    Reason = $"transition to {JobStates.Review} refused: {outcome.Status} {outcome.Message}"
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
                    "in 3-progress and the orchestrator never moved the folder. Promoted to 4-review.");
            }

            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.RecoveredToReview,
                ProjectName = entry.Name,
                Slug = slug,
                JobId = jobId,
                SentinelKeyword = keyword,
                TargetState = JobStates.Review,
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

    private StaleProgressDecision ArchiveOrphan(WatchPathEntry entry, string jobFolder, string slug, DateTime now)
        => Archive(entry, jobFolder, slug, now, suffix: "orphan", kind: StaleProgressDecisionKinds.ArchivedOrphan);

    private StaleProgressDecision ArchiveEmpty(WatchPathEntry entry, string jobFolder, string slug, DateTime now)
        => Archive(entry, jobFolder, slug, now, suffix: "empty", kind: StaleProgressDecisionKinds.ArchivedEmpty);

    private StaleProgressDecision Archive(WatchPathEntry entry, string jobFolder, string slug, DateTime now, string suffix, string kind)
    {
        var datePart = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var newSlug = $"{slug}-{suffix}-{datePart}";
        // Disambiguate when the same slug is archived twice on the same day
        // (re-creates of the same task name across boots).
        var attempt = newSlug;
        for (int i = 2; Directory.Exists(Path.Combine(entry.Path, JobStates.Archive, attempt)) && i < 1000; i++)
        {
            attempt = $"{newSlug}-{i}";
        }

        var outcome = _states.ArchiveFolder(jobFolder, attempt);
        if (outcome.Status != MoveJobStatus.Success)
        {
            return new StaleProgressDecision
            {
                At = now,
                Kind = StaleProgressDecisionKinds.ArchiveFailed,
                ProjectName = entry.Name,
                Slug = slug,
                ArchiveSlug = attempt,
                Reason = $"archive refused: {outcome.Status} {outcome.Message}"
            };
        }

        return new StaleProgressDecision
        {
            At = now,
            Kind = kind,
            ProjectName = entry.Name,
            Slug = slug,
            ArchiveSlug = attempt,
            TargetState = JobStates.Archive,
            Reason = suffix == "empty"
                ? "stale folder with no job.json or cli-output.log"
                : "stale folder past resume window with no completion sentinel"
        };
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
    [JsonPropertyName("archiveSlug")] public string? ArchiveSlug { get; init; }
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
    /// <summary>Sentinel found in tail; folder moved to <c>4-review</c>.</summary>
    public const string RecoveredToReview = "recovered-to-review";
    /// <summary>Sentinel found but the transition refused or threw.</summary>
    public const string RecoveryFailed = "recovery-failed";
    /// <summary>Stale folder with content but no sentinel; archived as <c>-orphan-</c>.</summary>
    public const string ArchivedOrphan = "archived-orphan";
    /// <summary>Stale folder with no <c>job.json</c> and no <c>cli-output.log</c>; archived as <c>-empty-</c>.</summary>
    public const string ArchivedEmpty = "archived-empty";
    /// <summary>Archive call refused (e.g. target slug already exists).</summary>
    public const string ArchiveFailed = "archive-failed";
}
