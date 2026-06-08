using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Discovery + read surface for jobs on disk. Resolves the configured
/// watch paths (with the <c>.orchestrator.yml</c> pointer flow), scans
/// the state subfolders into <see cref="TaskInfo"/> records, hydrates
/// the per-job detail view (status / prompt / context-usage / log /
/// summary state), and serves read-only file lookups including the
/// <c>attachments/</c> and <c>results/</c> binary mirrors.
///
/// Writes against <c>task.json</c> live in the sibling services in this
/// folder: <see cref="TaskStateMachine"/> for folder moves,
/// <see cref="TaskMutationService"/> for field-level edits and
/// attachments, and <see cref="TaskSessionLog"/> for session telemetry.
/// </summary>
public class TaskScannerService : ITaskScanner
{
    private readonly IConfiguration _config;
    private readonly ILogger<TaskScannerService> _logger;
    private readonly SummaryGenerationService _summaryService;

    /// <summary>
    /// Optional in-memory snapshot cache. Wired by DI through
    /// <see cref="SetIndexCache"/> after both services are constructed
    /// (avoids the constructor cycle: cache needs scanner for raw reads,
    /// scanner needs cache for hot-path reads).
    /// </summary>
    private TaskIndexCache? _indexCache;

    /// <summary>
    /// Watch paths that resolved to a non-existent folder and have already
    /// been warned about. <see cref="ScanAllJobsRaw"/> runs on every cache
    /// refresh, which a busy job's FileSystemWatcher churn can trigger many
    /// times per second; without this latch the "Watch path does not exist"
    /// warning spammed the api log endlessly and buried the real crash cause
    /// in the last seconds before a silent host death (observed 2026-06-02
    /// against the misconfigured Runbook watch path). Warn once per path; a
    /// path that later reappears is removed so a fresh disappearance still
    /// surfaces. Concurrent because the scan fans out across cache readers.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _warnedMissingWatchPaths = new(StringComparer.OrdinalIgnoreCase);

    public TaskScannerService(IConfiguration config, ILogger<TaskScannerService> logger, SummaryGenerationService summaryService)
    {
        _config = config;
        _logger = logger;
        _summaryService = summaryService;
    }

    /// <summary>
    /// Late-binds the cache so <see cref="ScanAllJobs"/> can serve from
    /// memory. Called once at startup from Program.cs after the cache
    /// singleton has been resolved. Without this call the scanner stays
    /// in pre-Cycle-1 disk-walk-on-every-read mode (the existing tests
    /// that build a scanner directly continue to work without a cache).
    /// </summary>
    public void SetIndexCache(TaskIndexCache cache) => _indexCache = cache;

    /// <summary>
    /// Invalidates the in-memory snapshot, if a cache is wired. Mutation
    /// services call this right after a folder move / task.json rewrite so
    /// the next read sees the change synchronously rather than waiting for
    /// the FileSystemWatcher's debounce window. No-op when no cache is
    /// registered (test fixtures that build the scanner directly).
    /// </summary>
    public void InvalidateCache() =>
        _indexCache?.Invalidate(TaskIndexCache.InvalidationSource.Mutation);

    public List<WatchPathEntry> GetWatchPaths()
    {
        var raw = _config.GetSection("WatchPaths").Get<List<WatchPathEntry>>() ?? [];
        var resolved = new List<WatchPathEntry>(raw.Count);
        foreach (var entry in raw)
        {
            resolved.Add(ResolveWatchPath(entry));
        }
        return resolved;
    }

    /// <summary>
    /// Resolves a watch path entry's effective task folder. Resolution order:
    /// 1. If <c>Path</c> is explicitly set in config, use it as-is (backward compatible).
    /// 2. Otherwise, if <c>RootPath</c> contains <c>.orchestrator.yml</c> with a <c>projectKey</c>,
    ///    resolve to <c>&lt;TaskRepository&gt;/projects/&lt;projectKey&gt;</c>.
    ///    The central <c>TaskRepository</c> path comes from app configuration.
    /// 3. Otherwise, fall back to <c>&lt;RootPath&gt;/.orchestrator/jobs</c> (legacy layout).
    /// </summary>
    private WatchPathEntry ResolveWatchPath(WatchPathEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Path)) return entry;
        if (string.IsNullOrWhiteSpace(entry.RootPath)) return entry;

        var pointerPath = Path.Combine(entry.RootPath, ".orchestrator.yml");
        if (File.Exists(pointerPath))
        {
            try
            {
                var pointer = ReadOrchestratorPointer(pointerPath);
                var taskRepository = _config["TaskRepository"];
                if (!string.IsNullOrWhiteSpace(pointer.ProjectKey) && !string.IsNullOrWhiteSpace(taskRepository))
                {
                    var combined = Path.GetFullPath(Path.Combine(taskRepository, "projects", pointer.ProjectKey));
                    return entry with { Path = combined };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read pointer {Pointer} — falling back to legacy layout", pointerPath);
            }
        }

        var legacy = Path.Combine(entry.RootPath, ".orchestrator", "jobs");
        return entry with { Path = legacy };
    }

    private record OrchestratorPointer(string ProjectKey);

    /// <summary>
    /// Minimal YAML key:value parser for the flat <c>.orchestrator.yml</c> pointer schema.
    /// Currently only <c>projectKey</c> is read.
    /// </summary>
    private static OrchestratorPointer ReadOrchestratorPointer(string path)
    {
        string projectKey = "";
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim().Trim('"', '\'');
            if (key == "projectKey") projectKey = value;
        }
        return new OrchestratorPointer(projectKey);
    }

    /// <summary>
    /// Returns all jobs across configured watch paths. Cycle-1 hot-path
    /// optimization: when a <see cref="TaskIndexCache"/> is registered (the
    /// production case), this returns the cached snapshot in O(1); the
    /// cache refreshes itself on FileSystemWatcher events and on explicit
    /// invalidation from mutation services. Tests that build the scanner
    /// directly (no cache wired) keep the original disk-walk semantics.
    /// </summary>
    public List<TaskInfo> ScanAllJobs()
    {
        if (_indexCache != null)
        {
            // ImmutableList -> List materialization is cheap (copy of refs).
            // Callers that mutate the result still get isolation.
            return _indexCache.GetSnapshot().ToList();
        }
        return ScanAllJobsRaw();
    }

    /// <summary>
    /// The uncached disk walk. Always reads from disk; used by
    /// <see cref="TaskIndexCache"/> for refresh and by callers that want to
    /// bypass the cache (tests, recovery paths).
    /// </summary>
    public List<TaskInfo> ScanAllJobsRaw()
    {
        var sw = Stopwatch.StartNew();

        // Phase 1 — cheap directory enumeration. The flat layout is
        // authoritative when present: jobs live under jobs/<bucket>/<key>/ and
        // the lane comes from task.json.state. The legacy lane scan remains as a
        // read fallback for pre-migration tests and partially initialized
        // workspaces.
        var candidates = new List<(string jobDir, WatchPathEntry entry, string state)>();
        foreach (var entry in GetWatchPaths())
        {
            if (!Directory.Exists(entry.Path))
            {
                // Log-once per path: this runs on every cache refresh, so an
                // unthrottled warning floods the log under watcher churn.
                if (_warnedMissingWatchPaths.TryAdd(entry.Path, 0))
                    _logger.LogWarning("Watch path does not exist: {Path}", entry.Path);
                continue;
            }
            // Path is present again — clear the latch so a future disappearance
            // is surfaced rather than silently swallowed.
            _warnedMissingWatchPaths.TryRemove(entry.Path, out _);

            var flatJobs = TaskStorageLayout.EnumerateJobDirs(entry.Path).ToList();
            if (flatJobs.Count > 0)
            {
                foreach (var jobDir in flatJobs)
                    candidates.Add((jobDir, entry, ""));
                continue;
            }

            foreach (var state in TaskStates.All)
            {
                var stateDir = Path.Combine(entry.Path, state);
                if (!Directory.Exists(stateDir)) continue;

                foreach (var jobDir in Directory.GetDirectories(stateDir))
                {
                    var dirName = Path.GetFileName(jobDir);
                    if (dirName.StartsWith('_')) continue;
                    candidates.Add((jobDir, entry, state));
                }
            }
        }

        // Phase 2 — parse each folder in parallel. ScanJobFolder is read-only
        // (the only write is a rare divergent-id self-heal that targets that
        // folder's own task.json, so distinct folders never contend) and returns
        // an independent TaskInfo. The dominant per-folder cost is
        // GetLastActivityTime's recursive file walk plus the JSON parse — both
        // CPU/IO that overlap well across the dev host's cores. A full board
        // (~1k folders, each with logs/ + results/ subtrees) walked
        // sequentially was the "Neuladen ist langsam" cost the user reported:
        // every cache miss (a reorder that dirtied the index, or the
        // click-into-card FindJob right after a sort) paid the whole walk.
        // Result order is irrelevant; every consumer sorts by State/Order.
        var jobs = candidates
            .AsParallel()
            .WithDegreeOfParallelism(Math.Max(2, Environment.ProcessorCount))
            .Select(c => ScanJobFolder(c.jobDir, c.entry, c.state))
            .Where(j => j != null)
            .Select(j => j!)
            .ToList();

        sw.Stop();
        // Only log when the walk is slow enough to matter, so steady-state
        // cache hits (which never reach here) and fast scans stay quiet.
        if (sw.ElapsedMilliseconds >= 250)
        {
            _logger.LogInformation(
                "ScanAllJobsRaw scanned {Count} task folders in {ElapsedMs}ms (parallel x{Dop})",
                jobs.Count, sw.ElapsedMilliseconds, Math.Max(2, Environment.ProcessorCount));
        }
        return jobs;
    }

    public TaskInfo? ScanJobFolder(string jobDir, WatchPathEntry entry, string state)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var raw = JsonSerializer.Deserialize<JsonElement>(json, TaskJsonFile.ReadOpts);

            var lastActivity = GetLastActivityTime(jobDir);

            var folderId = Path.GetFileName(jobDir);
            var isFlatLayout = IsFlatLayoutJobDir(jobDir);
            var resolvedId = folderId;
            if (isFlatLayout)
            {
                // New layout: folder name is the stable key, while task.json.id
                // remains the external slug/id. Lane is metadata too, so prefer
                // task.json.state over the enumeration context.
                resolvedId = raw.TryGetProperty("id", out var flatId)
                             && flatId.ValueKind == JsonValueKind.String
                             && flatId.GetString() is { Length: > 0 } flatJsonId
                    ? flatJsonId
                    : folderId;
            }
            else
            {
                // Legacy layout: the folder name is the canonical job id.
                // Anything else silently breaks URL slugs, log paths, MoveJob
                // targets, and the runner's job lookups, so self-heal it.
                if (raw.TryGetProperty("id", out var id)
                    && id.GetString() is { Length: > 0 } jsonId
                    && jsonId != folderId)
                {
                    _logger.LogWarning(
                        "Job folder '{Dir}' has divergent id '{JsonId}' in task.json - rewriting to match folder name '{FolderId}'.",
                        jobDir, jsonId, folderId);
                    TaskJsonFile.UpdateField(jobDir, "id", folderId, _logger);
                }
            }

            var resolvedState = raw.TryGetProperty("state", out var stateEl)
                                && stateEl.ValueKind == JsonValueKind.String
                                && stateEl.GetString() is { Length: > 0 } jsonState
                ? jsonState
                : state;

            // If a flat-layout folder was scanned but lacks usable state, it is
            // corrupt. Do not surface a lane-less card; the index rebuild/migrator
            // path logs the underlying bad task.json separately.
            if (string.IsNullOrWhiteSpace(resolvedState)) return null;

            var ownerClientId = ResolveOwnerClientId(raw, jobDir);
            var (commitChain, legacyCommit) = ReadCommitChain(raw);

            return new TaskInfo
            {
                Id = resolvedId,
                TaskKey = TaskIdentity.CreateKey(entry.Path, resolvedId),
                Key = ReadReferenceKey(raw),
                OwnerClientId = ownerClientId,
                Title = raw.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                State = resolvedState,
                Order = raw.TryGetProperty("order", out var ord) && ord.TryGetInt32(out var orderVal) ? orderVal : 999,
                Agent = raw.TryGetProperty("agent", out var agent) ? agent.GetString() ?? "" : "",
                CreatedAt = raw.TryGetProperty("createdAt", out var created) && created.TryGetDateTime(out var dt) ? dt : File.GetCreationTime(jobJsonPath),
                WatchPath = entry.Path,
                ProjectName = entry.Name,
                FolderPath = jobDir,
                LastActivity = lastActivity,
                // Lane-entry sort anchor. Legacy folders written before this
                // field existed fall back to lastActivity, so the lane-entry
                // sort degrades gracefully instead of treating them as epoch.
                EnteredLaneAt = raw.TryGetProperty("enteredLaneAt", out var entered) && entered.TryGetDateTime(out var entDt) ? entDt : lastActivity,
                SessionName = raw.TryGetProperty("sessionName", out var sn) ? sn.GetString() : null,
                LastUsage = raw.TryGetProperty("lastUsage", out var lu) && lu.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<SessionUsage>(lu.GetRawText(), TaskJsonFile.ReadOpts)
                    : null,
                Model = raw.TryGetProperty("model", out var md) ? md.GetString() : null,
                ThinkingLevel = raw.TryGetProperty("thinkingLevel", out var tl) ? tl.GetString() : null,
                CliType = raw.TryGetProperty("cliType", out var ct) ? ct.GetString() : null,
                Kind = TaskKinds.Normalize(raw.TryGetProperty("kind", out var kd) ? kd.GetString() : null),
                EpicId = raw.TryGetProperty("epicId", out var ep) && !string.IsNullOrWhiteSpace(ep.GetString()) ? ep.GetString() : null,
                Mode = TaskModes.Normalize(raw.TryGetProperty("mode", out var md0) ? md0.GetString() : null),
                AllowWebAccess = raw.TryGetProperty("allowWebAccess", out var awa) && awa.ValueKind == JsonValueKind.True,
                UseOwnSession = raw.TryGetProperty("useOwnSession", out var uos) && uos.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? uos.GetBoolean()
                    : null,
                Commit = legacyCommit,
                Commits = commitChain,
                CodeActivityDetected = DetectCodeActivity(raw, jobDir),
                SessionChain = ReadSessionChain(raw),
                PendingIntent = ReadPendingIntent(jobDir),
                OutcomeIssue = ResolveOutcomeIssue(jobDir, resolvedState),
                Fixture = raw.TryGetProperty("fixture", out var fix)
                    && fix.ValueKind is JsonValueKind.True,
                Phase = ReadPhase(raw, resolvedState, jobDir),
                TaskType = ReadTaskType(raw),
                Tags = ReadTags(raw),
                References = ReadReferences(raw)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse task.json in {Dir}", jobDir);
            return null;
        }
    }

    private static bool IsFlatLayoutJobDir(string jobDir)
    {
        var bucketDir = Path.GetDirectoryName(jobDir);
        var jobsDir = bucketDir == null ? null : Path.GetDirectoryName(bucketDir);
        return string.Equals(Path.GetFileName(jobsDir), TaskStorageLayout.JobsDirName, StringComparison.Ordinal);
    }

    public TaskInfo? FindJob(string jobId, string? watchPath = null)
    {
        var matches = ScanAllJobs().Where(j => j.Id == jobId);
        if (!string.IsNullOrWhiteSpace(watchPath))
        {
            matches = matches.Where(j => string.Equals(j.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase));
        }

        var resolved = matches.ToList();
        if (resolved.Count == 1) return resolved[0];
        if (resolved.Count > 1)
        {
            // Duplicate job IDs can occur when umlauts were stripped differently during creation,
            // or when a folder copy was left behind after a failed move. Prefer the job in the
            // earliest (most active) state so the user can still open and manage it.
            _logger.LogWarning("Duplicate job id {JobId} found in {Count} locations; returning job in earliest state", jobId, resolved.Count);
            return resolved
                .OrderBy(j => Array.IndexOf(TaskStates.All, j.State))
                .First();
        }

        return null;
    }

    public TaskDetail? GetJobDetail(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var dir = info.FolderPath;
        var statusMd = ReadFileOrNull(Path.Combine(dir, "status.md"));
        return new TaskDetail
        {
            Info = info,
            PromptMarkdown = ReadFileOrNull(Path.Combine(dir, "prompt.md")),
            PromptHistory = ReadPromptHistory(dir),
            TitleHistory = TitleHistoryLog.Read(dir),
            StatusMarkdown = statusMd,
            ContextUsage = ReadContextUsage(dir),
            Log = BuildLog(dir),
            SummaryState = ResolveSummaryState(info.TaskKey, statusMd),
            ReviewEvidence = ReviewEvidenceLog.ReadLatestPerId(dir, _logger)
        };
    }

    /// <summary>
    /// Builds the pre-filled coding-task draft for "promote a finished
    /// planning task" (see docs/research/planning-research-task-kinds-2026-05.md).
    /// Returns null when the job is not found. Title + prompt body come from
    /// the planning report (<c>status.md</c>); every image under the job's
    /// <c>results/</c> and <c>attachments/</c> folders is listed (deduped by
    /// file name) so the modal can copy them byte-for-byte. The returned
    /// attachment <c>Url</c> is left blank here — the endpoint layer owns the
    /// API route shape and fills it in.
    /// </summary>
    public PromoteToCodingResponse? BuildPromoteToCodingPlan(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var dir = info.FolderPath;
        var statusMd = ReadFileOrNull(Path.Combine(dir, "status.md"));

        var attachments = new List<PromoteAttachmentRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPromotableImages(dir, "results", attachments, seen);
        CollectPromotableImages(dir, "attachments", attachments, seen);

        return new PromoteToCodingResponse
        {
            Title = PlanningPromotion.DeriveTitle(info.Title, statusMd, info.Id),
            PromptMarkdown = PlanningPromotion.ExtractProposedTaskPrompt(statusMd),
            Mode = TaskModes.Coding,
            TargetState = TaskStates.Preparation,
            WatchPath = info.WatchPath,
            ProjectName = info.ProjectName,
            Attachments = attachments,
        };
    }

    private static readonly HashSet<string> PromotableImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

    private static void CollectPromotableImages(
        string jobDir, string subDir, List<PromoteAttachmentRef> sink, HashSet<string> seen)
    {
        var folder = Path.Combine(jobDir, subDir);
        if (!Directory.Exists(folder)) return;

        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(path);
            if (!PromotableImageExtensions.Contains(Path.GetExtension(name))) continue;
            if (!seen.Add(name)) continue;
            sink.Add(new PromoteAttachmentRef { FileName = name, Source = subDir, Url = "" });
        }
    }

    /// <summary>
    /// Whether this job shows any sign that code landed: a stamped
    /// auto-commit, or at least one run that moved repo HEAD (a non-trivial
    /// <c>before..after</c> SHA range in <c>session-events.jsonl</c>). Feeds
    /// <see cref="TaskInfo.CodeActivityDetected"/> so the UI can tell an
    /// analysis-only task (no activity) apart from one where work landed but
    /// the attribution chain is still empty. Deliberately a boolean, not a
    /// count: the commit total is owned by <see cref="TaskInfo.Commits"/>
    /// (the single source of truth), and a count here would re-introduce the
    /// drift this change removes.
    ///
    /// Cheap by construction: skips the disk read entirely when the job has
    /// no auto-commit AND no session log (the majority of <c>1-preparation</c>
    /// / <c>2-ready</c> jobs), and short-circuits on the first range that
    /// moved HEAD.
    /// </summary>
    private static bool DetectCodeActivity(JsonElement raw, string jobFolder)
    {
        if (raw.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object)
            return true;

        var sessionLog = TaskPaths.SessionEventsLog(jobFolder);
        if (!File.Exists(sessionLog)) return false;

        foreach (var line in File.ReadLines(sessionLog))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            SessionEvent? evt;
            try { evt = JsonSerializer.Deserialize<SessionEvent>(line, TaskJsonFile.ReadOpts); }
            catch { continue; }
            if (evt == null) continue;
            if (string.IsNullOrWhiteSpace(evt.HeadShaBefore) || string.IsNullOrWhiteSpace(evt.HeadShaAfter)) continue;
            if (string.Equals(evt.HeadShaBefore, evt.HeadShaAfter, StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the job's <c>ownerClientId</c>, migrating legacy task.json
    /// files (which predate the field) by writing <see cref="DefaultClientIdentity.Id"/>
    /// back to disk so the next scan finds a non-null value.
    /// </summary>
    private string ResolveOwnerClientId(JsonElement raw, string jobDir)
    {
        if (raw.TryGetProperty("ownerClientId", out var owner)
            && owner.ValueKind == JsonValueKind.String
            && owner.GetString() is { Length: > 0 } existing)
        {
            return existing;
        }
        // Migration: stamp the default identity on legacy jobs so attribution is
        // non-null everywhere. Idempotent against re-scans because subsequent
        // reads find the value above.
        TaskJsonFile.UpdateField(jobDir, "ownerClientId", DefaultClientIdentity.Id, _logger);
        _logger.LogInformation("Migrated job folder '{Dir}' to ownerClientId='{Owner}'", jobDir, DefaultClientIdentity.Id);
        return DefaultClientIdentity.Id;
    }

    /// <summary>
    /// Reads the optional <c>phase</c> field from <c>task.json</c>. The wire
    /// field stays null when absent on disk; the frontend's lane projection
    /// then falls back to <see cref="LifecyclePhases.DefaultFor"/>. This is
    /// the compatibility contract from
    /// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c>: existing
    /// job folders that predate the field continue to render in the default
    /// lane of their state without a one-shot migration that rewrites every
    /// <c>task.json</c>. Unknown phase strings, or phase strings that do not
    /// belong to <paramref name="state"/>, are dropped with a warning so a
    /// hand-edited / corrupted file cannot wedge the board.
    /// </summary>
    private string? ReadPhase(JsonElement raw, string state, string jobDir)
    {
        if (!raw.TryGetProperty("phase", out var phaseEl)) return null;
        if (phaseEl.ValueKind != JsonValueKind.String) return null;
        var value = phaseEl.GetString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!LifecyclePhases.All.Contains(value))
        {
            _logger.LogWarning("Unknown phase '{Phase}' in {Dir}; ignoring", value, jobDir);
            return null;
        }
        if (!LifecyclePhases.IsAllowed(state, value))
        {
            _logger.LogWarning("Phase '{Phase}' is not allowed for state '{State}' in {Dir}; ignoring", value, state, jobDir);
            return null;
        }
        return value;
    }

    /// <summary>
    /// Reads the optional <c>taskType</c> field from <c>task.json</c>. Missing
    /// or unknown values fall back to <see cref="TaskTypes.Chore"/>, the
    /// safe neutral default for legacy and technical work that predates the
    /// field. No write-back: lazy defaulting keeps boot scans cheap.
    /// </summary>
    private static string ReadTaskType(JsonElement raw)
    {
        if (!raw.TryGetProperty("taskType", out var t)) return TaskTypes.Chore;
        if (t.ValueKind != JsonValueKind.String) return TaskTypes.Chore;
        return TaskTypes.Normalize(t.GetString());
    }

    /// <summary>
    /// Reads the F33 Linear-style reference key (<c>ATP-130</c>, <c>RB-42</c>)
    /// from <c>task.json</c>. Returns null when the field is absent or
    /// non-string so the UI can fall back to <see cref="TaskInfo.Id"/>.
    /// </summary>
    private static string? ReadReferenceKey(JsonElement raw)
    {
        if (!raw.TryGetProperty("key", out var k)) return null;
        if (k.ValueKind != JsonValueKind.String) return null;
        var value = k.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Reads the optional <c>tags</c> string array from <c>task.json</c>. Drops
    /// non-string entries. Returns an empty list when the field is absent or
    /// the wrong shape; never throws on a malformed value.
    /// </summary>
    private static List<string> ReadTags(JsonElement raw)
    {
        if (!raw.TryGetProperty("tags", out var arr)) return [];
        if (arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            var s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
        }
        return list;
    }

    /// <summary>
    /// Reads the F34 <c>"references"</c> object from <c>task.json</c>. Absent,
    /// null, or non-object yields an empty <see cref="TaskReferences"/>; a
    /// malformed object is tolerated the same way so a bad edge can never
    /// fail the whole scan.
    /// </summary>
    private static TaskReferences ReadReferences(JsonElement raw)
    {
        if (!raw.TryGetProperty("references", out var refs) || refs.ValueKind != JsonValueKind.Object)
            return new TaskReferences();
        try
        {
            return JsonSerializer.Deserialize<TaskReferences>(refs.GetRawText(), TaskJsonFile.ReadOpts)
                ?? new TaskReferences();
        }
        catch
        {
            return new TaskReferences();
        }
    }

    /// <summary>
    /// Reads <c>pending-intent.json</c> if present. Returns null when the
    /// job has no saved follow-up draft. See <see cref="PendingIntent"/>.
    /// </summary>
    private static PendingIntent? ReadPendingIntent(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "pending-intent.json");
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PendingIntent>(raw, TaskJsonFile.ReadOpts);
        }
        catch
        {
            return null;
        }
    }

    private const int OutcomeIssueTailBytes = 16 * 1024;

    private static TaskOutcomeIssue? ResolveOutcomeIssue(string jobFolder, string state)
    {
        var logPath = TaskPaths.CliOutputLog(jobFolder);
        if (!File.Exists(logPath)) return null;

        var tail = ReadTailUtf8(logPath, OutcomeIssueTailBytes);
        if (string.IsNullOrWhiteSpace(tail)) return null;

        var completed = string.Equals(state, TaskStates.Completed, StringComparison.OrdinalIgnoreCase);
        var lastSeenAt = File.GetLastWriteTimeUtc(logPath);
        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();

            // Reconcile with the final verdict: the orchestrator's accept note
            // (move to 5-human-review / 6-completed) supersedes every earlier
            // intermediate-cycle outcome marker. Scanning newest-first, the first
            // accept line means anything above it is resolved - so an accepted
            // task never carries a stale classifier-unknown/Warn chip (ASS-775).
            if (IsAcceptReconcileLine(line)) return null;

            if (line.Contains("[agent-git-violation]", StringComparison.OrdinalIgnoreCase))
                return BuildOutcomeIssue("agent-git-violation", "Agent git violation", "High", line, lastSeenAt);

            // Never derive an outcome issue from an orchestrator decision/reissue/
            // meta line or a supervisor line. Those carry prose - e.g. an accept
            // reason that mentions "classifier-unknown" - that must not be read as
            // a runner outcome and must never become the issue summary. The typed
            // issue tags ([classifier-unknown], [missing-terminal-sentinel], ...)
            // are NOT meta, so genuine outcome lines are still derived.
            if (IsOrchestratorMetaLine(line)) continue;

            if (TryResolveOutcomeIssue(line, lastSeenAt, out var issue))
            {
                // A terminal 6-completed card was accepted; a Warn-class ambiguity
                // chip contradicts that and is a stale intermediate-cycle artifact
                // even if the accept note has scrolled out of the read tail.
                if (completed && TaskOutcomeIssueReconciliation.IsVerdictContradicting(issue))
                    return null;
                return issue;
            }
        }

        return null;
    }

    private static readonly string[] OrchestratorMetaTags =
        ["[decision]", "[reissue]", "[heuristic]", "[intervention]", "[steer]", "[giveup]"];

    /// <summary>
    /// True for orchestrator decision/reissue/meta lines and supervisor lines.
    /// These carry prose, not a typed runner outcome, so they are never a source
    /// for a <see cref="TaskOutcomeIssue"/> (kind or summary).
    /// </summary>
    private static bool IsOrchestratorMetaLine(string line)
    {
        var lower = line.ToLowerInvariant();
        if (lower.Contains("[supervisor]")) return true;
        foreach (var tag in OrchestratorMetaTags)
            if (lower.Contains(tag)) return true;
        return false;
    }

    /// <summary>
    /// True for the orchestrator's accept decision note (<c>Auto-review accepted
    /// "X" ... Moved to 5-human-review</c>) and the boot backfill's reconcile note.
    /// Both mark the run as accepted, which supersedes earlier outcome markers.
    /// </summary>
    private static bool IsAcceptReconcileLine(string line)
    {
        var lower = line.ToLowerInvariant();
        if (!lower.Contains("[decision]")) return false;
        return lower.Contains("auto-review accepted")
            || lower.Contains("reconciled on accept");
    }

    private static string ReadTailUtf8(string path, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length <= 0) return "";
            var length = (int)Math.Min(maxBytes, fs.Length);
            fs.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = fs.Read(buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch
        {
            return "";
        }
    }

    private static bool TryResolveOutcomeIssue(string line, DateTime lastSeenAt, out TaskOutcomeIssue? issue)
    {
        issue = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        var lower = line.ToLowerInvariant();
        // EnvironmentBlocker takes precedence: the marker is the runtime
        // signal that the host environment (sandbox / logon session /
        // ACLs) refused to let the agent execute. Surfaced as its own
        // chip so the user does not waste time inspecting an empty
        // change set as if it were a normal permission denial.
        if (lower.Contains("environment-blocker") || lower.Contains("[environment-blocker]"))
        {
            issue = BuildOutcomeIssue("environment-blocker", "Environment blocker", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("permission-blocked") || lower.Contains("permission denied and could not request permission"))
        {
            issue = BuildOutcomeIssue("permission-blocked", "Permission blocked", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("watchdog-timeout") || (lower.Contains("[watchdog]") && lower.Contains("killed after")))
        {
            issue = BuildOutcomeIssue("watchdog-timeout", "Watchdog timeout", "High", line, lastSeenAt);
            return true;
        }
        // Quarantined / context-overflow are terminal, non-retryable circuit-breaker
        // outcomes (the run was parked in human review to stop an endless reissue
        // loop), so they rank High like the other unrecoverable runner outcomes.
        if (lower.Contains("quarantined"))
        {
            issue = BuildOutcomeIssue("quarantined", "Quarantined", "High", line, lastSeenAt);
            return true;
        }
        // Require the BRACKETED runner marker, never the bare word. The runner
        // emits "[worktree-containment] ..." (ProjectRunner) only on a real
        // containment violation. The previous bare-substring match also fired
        // when an AGENT merely *mentioned* the pipeline step in its stdout prose
        // (e.g. a self-modifying task describing `worktree-containment` /
        // `git-commit-attribution`) -> a false High-severity chip + spurious
        // reissue (ASS-914). The bracketed form is the runner's structured tag
        // and cannot be produced by the agent describing the pipeline.
        if (lower.Contains("[worktree-containment]"))
        {
            issue = BuildOutcomeIssue("worktree-containment", "Worktree containment", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("[agent-git-violation]"))
        {
            issue = BuildOutcomeIssue("agent-git-violation", "Agent git violation", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("context-overflow"))
        {
            issue = BuildOutcomeIssue("context-overflow", "Context overflow", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("missing-terminal-sentinel"))
        {
            issue = BuildOutcomeIssue("missing-terminal-sentinel", "Missing sentinel", "Warn", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("classifier-unknown") || lower.Contains("could not classify the agent's reply"))
        {
            issue = BuildOutcomeIssue("classifier-unknown", "Classifier unknown", "Warn", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("heuristic-done"))
        {
            issue = BuildOutcomeIssue("heuristic-done", "Heuristic done", "Warn", line, lastSeenAt);
            return true;
        }

        return false;
    }

    private static TaskOutcomeIssue BuildOutcomeIssue(string kind, string label, string severity, string rawLine, DateTime lastSeenAt)
        => new()
        {
            Kind = kind,
            Label = label,
            Severity = severity,
            Summary = SummarizeOutcomeLine(rawLine),
            LastSeenAt = lastSeenAt
        };

    private static string SummarizeOutcomeLine(string line)
    {
        var trimmed = line.Trim();
        var end = trimmed.IndexOf(']');
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && end > 0)
        {
            trimmed = trimmed[(end + 1)..].Trim();
        }
        if (trimmed.Length <= 260) return trimmed;
        return trimmed[..257].TrimEnd() + "...";
    }

    /// <summary>
    /// Reads any <c>prompt-N.md</c> siblings of <c>prompt.md</c> in the job
    /// folder and returns them ordered by N. Used by the Task Description
    /// pane to render the blog-style timeline of task extensions written by
    /// Extend mode.
    /// </summary>
    private static List<TaskPromptHistoryEntry> ReadPromptHistory(string jobFolder)
    {
        var result = new List<TaskPromptHistoryEntry>();
        if (!Directory.Exists(jobFolder)) return result;
        foreach (var path in Directory.EnumerateFiles(jobFolder, "prompt-*.md"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var dash = name.IndexOf('-');
            if (dash < 0 || dash >= name.Length - 1) continue;
            if (!int.TryParse(name[(dash + 1)..], out var index)) continue;
            string body;
            try { body = File.ReadAllText(path); }
            catch { continue; }
            DateTime writtenAt;
            try { writtenAt = File.GetLastWriteTimeUtc(path); }
            catch { writtenAt = DateTime.UtcNow; }
            result.Add(new TaskPromptHistoryEntry
            {
                Index = index,
                FileName = Path.GetFileName(path),
                Markdown = body,
                WrittenAt = writtenAt
            });
        }
        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    /// <summary>
    /// Returns the in-memory summary state if there is one, otherwise infers
    /// a baseline from disk: <c>Ready</c> when status.md exists with content,
    /// <c>None</c> when absent. After a backend restart any previous
    /// <c>Generating</c> / <c>Failed</c> state is forgotten — acceptable, since
    /// the user can simply re-run.
    /// </summary>
    private TaskSummaryState ResolveSummaryState(string jobKey, string? statusMarkdown)
    {
        var live = _summaryService.GetState(jobKey);
        if (live != null) return live;
        return new TaskSummaryState
        {
            Status = string.IsNullOrWhiteSpace(statusMarkdown) ? TaskSummaryStatus.None : TaskSummaryStatus.Ready,
            BytesWritten = statusMarkdown?.Length
        };
    }

    /// <summary>
    /// Reads <c>sessionChain</c> from task.json with a tolerant fallback: if the
    /// field is missing but a legacy <c>sessionName</c> exists, return a single-
    /// element chain. Anything else returns an empty list.
    /// </summary>
    /// <summary>
    /// Reads the task's commit chain from <c>task.json</c>. Returns a tuple
    /// <c>(chain, legacy)</c> where <c>chain</c> is the ordered list of
    /// commits this task has produced (oldest -&gt; newest) and <c>legacy</c>
    /// is the singular <see cref="TaskInfo.Commit"/> value kept for
    /// backwards compatibility with consumers that have not been
    /// migrated. Reads three shapes:
    /// <list type="bullet">
    /// <item><c>commits</c> is an array of objects -&gt; chain = array,
    ///   legacy = last entry.</item>
    /// <item><c>commits</c> is missing but <c>commit</c> is an object -&gt;
    ///   chain = [commit], legacy = commit (the legacy single-commit
    ///   shape that predates this work).</item>
    /// <item>Neither field present -&gt; chain = [], legacy = null.</item>
    /// </list>
    /// </summary>
    private static (List<TaskCommitInfo> chain, TaskCommitInfo? legacy) ReadCommitChain(JsonElement raw)
    {
        var chain = new List<TaskCommitInfo>();
        if (raw.TryGetProperty("commits", out var commitsEl) && commitsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in commitsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(item.GetRawText(), TaskJsonFile.ReadOpts);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha)) chain.Add(parsed);
            }
        }
        TaskCommitInfo? legacy = null;
        if (raw.TryGetProperty("commit", out var commitEl) && commitEl.ValueKind == JsonValueKind.Object)
        {
            legacy = JsonSerializer.Deserialize<TaskCommitInfo>(commitEl.GetRawText(), TaskJsonFile.ReadOpts);
        }
        if (chain.Count == 0 && legacy != null && !string.IsNullOrWhiteSpace(legacy.Sha))
        {
            chain.Add(legacy);
        }
        if (chain.Count > 0)
        {
            // Singular field always tracks the newest entry so legacy
            // consumers see the latest commit, not a stale first one.
            legacy = chain[^1];
        }
        return (chain, legacy);
    }

    private static List<string> ReadSessionChain(JsonElement raw)
    {
        if (raw.TryGetProperty("sessionChain", out var chain) && chain.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in chain.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    list.Add(s);
            }
            return list;
        }
        if (raw.TryGetProperty("sessionName", out var sn) && sn.ValueKind == JsonValueKind.String
            && sn.GetString() is { Length: > 0 } legacy)
        {
            return [legacy];
        }
        return [];
    }

    private ContextUsageSnapshot? ReadContextUsage(string jobDir)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var raw = JsonSerializer.Deserialize<JsonElement>(json, TaskJsonFile.ReadOpts);
            if (!raw.TryGetProperty("contextUsage", out var contextUsage) || contextUsage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ContextUsageSnapshot>(contextUsage.GetRawText(), TaskJsonFile.ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read contextUsage from {TaskDir}", jobDir);
            return null;
        }
    }

    public string? ReadJobFile(string jobId, string fileName, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        // Path-traversal guard: only files directly in the job root.
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return null;

        // Editable / always-known files plus any *.md file the agents / operators
        // drop in the job root (surfaced by the Files tab).
        var allowed = new[] { "prompt.md", "status.md", "task.json" };
        var isMarkdown = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        if (!allowed.Contains(fileName) && !isMarkdown) return null;

        return ReadFileOrNull(Path.Combine(info.FolderPath, fileName));
    }

    /// <summary>
    /// Lists every <c>.md</c> file directly in the job root, sorted for the
    /// Files tab (prompt first, then aspect-* alphabetical, then *_NOTE / *_NOTES
    /// alphabetical, then everything else alphabetical). <c>status.md</c> is
    /// excluded because it has its own Protocol tab. Subfolders
    /// (<c>logs/</c>, <c>results/</c>, <c>attachments/</c>) are out of scope.
    /// </summary>
    public TaskArtifactsResponse? ListArtifacts(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var dir = info.FolderPath;
        if (!Directory.Exists(dir)) return new TaskArtifactsResponse { JobId = jobId, Files = [] };

        var artifacts = new List<TaskArtifact>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, "status.md", StringComparison.OrdinalIgnoreCase)) continue;

            var (kind, aspectName) = ClassifyArtifact(name);
            FileInfo fi;
            try { fi = new FileInfo(path); }
            catch { continue; }

            artifacts.Add(new TaskArtifact
            {
                Name = name,
                SizeBytes = fi.Length,
                Mtime = fi.LastWriteTimeUtc,
                Kind = kind,
                AspectName = aspectName,
            });
        }

        artifacts.Sort(CompareArtifactsForFilesTab);

        return new TaskArtifactsResponse { JobId = jobId, Files = artifacts };
    }

    private static (TaskArtifactKind Kind, string? AspectName) ClassifyArtifact(string fileName)
    {
        if (string.Equals(fileName, "prompt.md", StringComparison.OrdinalIgnoreCase))
            return (TaskArtifactKind.Prompt, null);

        if (fileName.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var aspect = fileName.Substring("aspect-".Length, fileName.Length - "aspect-".Length - ".md".Length);
            return (TaskArtifactKind.Aspect, aspect);
        }

        if (fileName.EndsWith("_NOTE.md", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("_NOTES.md", StringComparison.OrdinalIgnoreCase))
            return (TaskArtifactKind.Note, null);

        return (TaskArtifactKind.Other, null);
    }

    private static int CompareArtifactsForFilesTab(TaskArtifact a, TaskArtifact b)
    {
        // Prompt always wins; otherwise group by kind ordinal, then by display key.
        int rank(TaskArtifact x) => x.Kind switch
        {
            TaskArtifactKind.Prompt => 0,
            TaskArtifactKind.Aspect => 1,
            TaskArtifactKind.Note   => 2,
            _                      => 3,
        };

        var rA = rank(a);
        var rB = rank(b);
        if (rA != rB) return rA.CompareTo(rB);

        // Within aspect group, sort by aspect name; otherwise by file name.
        var keyA = a.Kind == TaskArtifactKind.Aspect ? (a.AspectName ?? a.Name) : a.Name;
        var keyB = b.Kind == TaskArtifactKind.Aspect ? (b.AspectName ?? b.Name) : b.Name;
        return string.Compare(keyA, keyB, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadFileOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    public (string? Path, string? ContentType) ResolveAttachment(string jobId, string fileName, string? watchPath = null)
        => ResolveJobBinaryFile(jobId, "attachments", fileName, watchPath);

    /// <summary>
    /// Read-only counterpart to <see cref="ResolveAttachment"/> for the
    /// <c>results/</c> folder where agents drop screenshots they want to keep
    /// in the protocol. Same path-traversal guards, same image content-type
    /// mapping. See <c>docs/protocol-style.md</c> for the folder contract.
    /// </summary>
    public (string? Path, string? ContentType) ResolveResult(string jobId, string fileName, string? watchPath = null)
        => ResolveJobBinaryFile(jobId, "results", fileName, watchPath);

    private (string? Path, string? ContentType) ResolveJobBinaryFile(string jobId, string subDir, string fileName, string? watchPath)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return (null, null);

        var info = FindJob(jobId, watchPath);
        if (info == null) return (null, null);

        var dir = Path.Combine(info.FolderPath, subDir);
        var fullPath = Path.Combine(dir, fileName);
        if (!File.Exists(fullPath)) return (null, null);

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var contentType = ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };
        return (fullPath, contentType);
    }

    private static List<TaskLogEntry> BuildLog(string dir)
    {
        var entries = new List<TaskLogEntry>();

        var jobJson = Path.Combine(dir, "task.json");
        if (File.Exists(jobJson))
        {
            entries.Add(new TaskLogEntry
            {
                Timestamp = File.GetCreationTime(jobJson),
                Event = "Job created"
            });
        }

        var promptMd = Path.Combine(dir, "prompt.md");
        if (File.Exists(promptMd))
        {
            entries.Add(new TaskLogEntry
            {
                Timestamp = File.GetLastWriteTime(promptMd),
                Event = "Prompt written"
            });
        }

        var statusMd = Path.Combine(dir, "status.md");
        if (File.Exists(statusMd))
        {
            entries.Add(new TaskLogEntry
            {
                Timestamp = File.GetLastWriteTime(statusMd),
                Event = "Status updated"
            });
        }

        // Pick up any files in a logs/ subfolder as log entries
        var logsDir = Path.Combine(dir, "logs");
        if (Directory.Exists(logsDir))
        {
            foreach (var f in Directory.GetFiles(logsDir, "*", SearchOption.AllDirectories).Take(30))
            {
                entries.Add(new TaskLogEntry
                {
                    Timestamp = File.GetLastWriteTime(f),
                    Event = "Log entry",
                    Detail = Path.GetFileName(f)
                });
            }
        }

        return entries.OrderBy(e => e.Timestamp).ToList();
    }

    private static DateTime GetLastActivityTime(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => File.GetLastWriteTime(f))
                .DefaultIfEmpty(Directory.GetLastWriteTime(dir))
                .Max();
        }
        catch { return Directory.GetLastWriteTime(dir); }
    }

}
