using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

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
    private readonly FileGenerationIndex? _fileGenerationIndex;
    private readonly AgentStudio.Registry.ProjectRegistry? _projectRegistry;

    /// <summary>
    /// Optional in-memory snapshot cache. Wired by DI through
    /// <see cref="SetIndexCache"/> after both services are constructed
    /// (avoids the constructor cycle: cache needs scanner for raw reads,
    /// scanner needs cache for hot-path reads).
    /// </summary>
    private TaskIndexCache? _indexCache;
    private JobStatsMetadataCache? _statsMetadataCache;
    private readonly ConcurrentDictionary<string, ArchivedFolderSnapshot> _archivedFolders =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private sealed record ArchivedFolderSnapshot(long TaskJsonLength, DateTime TaskJsonWriteUtc, TaskInfo Task);

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

    /// <summary>
    /// Invalid phase metadata is persisted until a mutation repairs the task.
    /// A full index refresh may inspect that same task many times per second,
    /// so warn once per task/state/value tuple instead of once per scan.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _warnedInvalidPhases = new(StringComparer.OrdinalIgnoreCase);

    public TaskScannerService(
        IConfiguration config,
        ILogger<TaskScannerService> logger,
        SummaryGenerationService summaryService,
        FileGenerationIndex? fileGenerationIndex = null,
        AgentStudio.Registry.ProjectRegistry? projectRegistry = null)
    {
        _config = config;
        _logger = logger;
        _summaryService = summaryService;
        _fileGenerationIndex = fileGenerationIndex;
        _projectRegistry = projectRegistry;
    }

    /// <summary>
    /// Late-binds the cache so <see cref="ScanAllJobs"/> can serve from
    /// memory. Called once at startup from Program.cs after the cache
    /// singleton has been resolved. Without this call the scanner stays
    /// in pre-Cycle-1 disk-walk-on-every-read mode (the existing tests
    /// that build a scanner directly continue to work without a cache).
    /// </summary>
    public void SetIndexCache(TaskIndexCache cache) => _indexCache = cache;

    public void SetStatsMetadataCache(JobStatsMetadataCache cache) => _statsMetadataCache = cache;

    /// <summary>
    /// Invalidates the in-memory snapshot, if a cache is wired. Mutation
    /// services call this right after a folder move / task.json rewrite so
    /// the next read sees the change synchronously rather than waiting for
    /// the FileSystemWatcher's debounce window. No-op when no cache is
    /// registered (test fixtures that build the scanner directly).
    /// </summary>
    public void InvalidateCache()
    {
        _indexCache?.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        _statsMetadataCache?.Invalidate();
    }

    public List<WatchPathEntry> GetWatchPaths()
    {
        var raw = _config.GetSection("WatchPaths").Get<List<WatchPathEntry>>() ?? [];
        var resolved = new List<WatchPathEntry>(raw.Count);
        foreach (var entry in raw)
        {
            resolved.Add(ResolveWatchPath(entry));
        }

        // WatchPaths is bootstrap compatibility only. API-created projects are
        // registry records and must be readable without a settings edit or restart.
        if (_projectRegistry != null)
        {
            var registryProjects = _projectRegistry.List().Where(p => !p.Archived).ToList();
            for (var i = 0; i < resolved.Count; i++)
            {
                var project = registryProjects.FirstOrDefault(p => string.Equals(
                    NormalizeWatchPath(p.StorageLocation), NormalizeWatchPath(resolved[i].Path),
                    StringComparison.OrdinalIgnoreCase));
                if (project != null)
                {
                    resolved[i] = resolved[i] with
                    {
                        Name = project.DisplayName,
                        RootPath = project.RootPath ?? resolved[i].RootPath,
                        RepositoryPath = project.RepositoryPath ?? resolved[i].RepositoryPath,
                    };
                }
            }

            foreach (var project in registryProjects)
            {
                if (resolved.Any(entry => string.Equals(
                        NormalizeWatchPath(entry.Path), NormalizeWatchPath(project.StorageLocation),
                        StringComparison.OrdinalIgnoreCase)))
                    continue;

                resolved.Add(new WatchPathEntry
                {
                    Name = project.DisplayName,
                    Path = project.StorageLocation,
                    RootPath = project.RootPath ?? "",
                    RepositoryPath = project.RepositoryPath ?? "",
                });
            }
        }
        return resolved;
    }

    private static string NormalizeWatchPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "" : path.Replace('\\', '/').TrimEnd('/');

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
    /// Returns the live task snapshot used by automated pickup, recovery,
    /// review, supervisor, and maintenance scans. Explicit E2E fixtures remain
    /// available through <see cref="ScanAllJobs"/> for opted-in API reads, but
    /// they never participate in background automation.
    /// </summary>
    public List<TaskInfo> ScanAllAutomationJobs()
        => ScanAllJobs().Where(task => !task.Fixture).ToList();

    /// <summary>
    /// Returns only the terminal <c>7-archive</c> tasks, slim-hydrated. Mirrors
    /// <see cref="ScanAllJobs"/>: when a <see cref="TaskIndexCache"/> is wired
    /// (production) this is an O(1) read of the archive partition the cache
    /// already built from its single shared disk walk; tests without a cache
    /// fall back to filtering a fresh raw scan. Powers the paged
    /// <c>GET /api/tasks/archive</c> read endpoint (ASS-1727) - the board
    /// responses deliberately exclude this lane, so it has its own read path.
    /// </summary>
    public List<TaskInfo> ScanArchivedJobs()
    {
        if (_indexCache != null)
        {
            return _indexCache.GetArchiveSnapshot().ToList();
        }
        return ScanAllJobsRaw()
            .Where(j => string.Equals(j.State, TaskStates.Archive, StringComparison.Ordinal))
            .ToList();
    }

    /// <summary>
    /// AGT-2029 — the live board snapshot plus the terminal <c>7-archive</c>
    /// lane, in one list. <see cref="ScanAllJobs"/> deliberately omits archive
    /// (hundreds of terminal cards would bloat every poll), but a waits-on
    /// dependency is fulfilled once its target reaches <c>6-completed</c> OR
    /// <c>7-archive</c>, so cross-project fulfillment resolution must be able to
    /// see archived targets. Cache-backed: O(1) read of the two partitions the
    /// index cache already built; without a cache a single raw walk already
    /// includes archive, so this avoids the double disk walk that
    /// <c>ScanAllJobs().Concat(ScanArchivedJobs())</c> would cost in tests.
    /// </summary>
    public List<TaskInfo> ScanAllJobsWithArchive()
    {
        if (_indexCache != null)
        {
            var (live, archived) = _indexCache.GetSnapshotPartitions();
            var combined = new List<TaskInfo>(live.Count + archived.Count);
            combined.AddRange(live);
            combined.AddRange(archived);
            return combined;
        }
        // No cache (tests / recovery): the raw disk walk already slim-hydrates
        // the 7-archive lane, so it is archive-inclusive by construction.
        return ScanAllJobsRaw();
    }

    /// <summary>Archive-inclusive counterpart of <see cref="ScanAllAutomationJobs"/>.</summary>
    public List<TaskInfo> ScanAllAutomationJobsWithArchive()
        => ScanAllJobsWithArchive().Where(task => !task.Fixture).ToList();

    /// <summary>
    /// Returns the reverse-reference graph built for the current task snapshot.
    /// Production reads reuse the graph published by <see cref="TaskIndexCache"/>
    /// instead of rebuilding it on every claim poll or endpoint request.
    /// </summary>
    public TaskReferenceIndex GetReferenceIndex()
    {
        return _indexCache?.GetReferenceIndex()
               ?? TaskReferenceIndex.Build(ScanAllJobsRaw());
    }

    /// <summary>
    /// Returns live tasks and their archive-inclusive reference graph from one
    /// cache generation. The uncached compatibility path also performs only
    /// one raw scan.
    /// </summary>
    public (IReadOnlyList<TaskInfo> Live, TaskReferenceIndex References)
        GetLiveSnapshotWithReferenceIndex()
    {
        if (_indexCache != null)
            return _indexCache.GetLiveSnapshotWithReferenceIndex();

        var all = ScanAllJobsRaw();
        return (
            all.Where(task => !string.Equals(task.State, TaskStates.Archive, StringComparison.Ordinal)).ToList(),
            TaskReferenceIndex.Build(all));
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
        // bounded metadata probes plus JSON parsing are independent per folder
        // and overlap well across the dev host's cores. A full board
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

        // Archived tasks are terminal and remain memoized across live snapshot
        // refreshes. Drop entries only when their folder disappeared (archive
        // move/delete), so the memo cannot grow with historical locations.
        var candidatePaths = candidates
            .Select(candidate => candidate.jobDir)
            .ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var archivedPath in _archivedFolders.Keys)
        {
            if (!candidatePaths.Contains(archivedPath))
                _archivedFolders.TryRemove(archivedPath, out _);
        }

        sw.Stop();
        // Only log when the walk is slow enough to matter, so steady-state
        // cache hits (which never reach here) and fast scans stay quiet.
        if (sw.ElapsedMilliseconds >= 250)
        {
            var archived = jobs.Count(j => string.Equals(j.State, TaskStates.Archive, StringComparison.Ordinal));
            _logger.LogInformation(
                "ScanAllJobsRaw scanned {Count} task folders ({Archived} archived slim-hydrated) in {ElapsedMs}ms (parallel x{Dop})",
                jobs.Count, archived, sw.ElapsedMilliseconds, Math.Max(2, Environment.ProcessorCount));
        }
        return jobs;
    }

    public TaskInfo? ScanJobFolder(string jobDir, WatchPathEntry entry, string state)
    {
        var jobJsonPath = Path.Combine(jobDir, "task.json");
        if (!File.Exists(jobJsonPath)) return null;

        try
        {
            var taskJsonInfo = new FileInfo(jobJsonPath);
            if (_archivedFolders.TryGetValue(jobDir, out var archived)
                && archived.TaskJsonLength == taskJsonInfo.Length
                && archived.TaskJsonWriteUtc == taskJsonInfo.LastWriteTimeUtc)
            {
                return archived.Task;
            }

            var json = File.ReadAllText(jobJsonPath);
            var raw = JsonSerializer.Deserialize<JsonElement>(json, TaskJsonFile.ReadOpts);

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

            // Slim hydration for the terminal 7-archive lane. The per-folder cost
            // of a scan is dominated by three disk walks - the recursive
            // last-activity walk, the cli-output.log tail read, and the
            // session-events.jsonl scan - and all three only feed live-card
            // affordances (freshness sort, outcome chip, code-activity flag) that
            // a terminal archived card does not need. Skipping them for the ~748
            // archived folders is the memory/CPU/garbage win this lane targets;
            // the cheap task.json header below still carries Id/Title/State/
            // Commits so archived cards render and stats drill-downs resolve their
            // title. Aggregate token/usage numbers come from the Agent Message Bus
            // (logs/bus/), never from these records, so slimming them changes no
            // statistic.
            var isArchive = string.Equals(resolvedState, TaskStates.Archive, StringComparison.Ordinal);
            var lastActivity = isArchive
                ? File.GetLastWriteTime(jobJsonPath)
                : GetLastActivityTime(jobDir);

            var ownerClientId = ResolveOwnerClientId(raw, jobDir);
            var (commitChain, legacyCommit) = ReadCommitChain(raw);

            var info = new TaskInfo
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
                // Provenance was added with model qualification. Missing means
                // legacy and is conservatively treated as an explicit pin.
                ModelExplicit = !raw.TryGetProperty("modelExplicit", out var modelExplicit)
                    || modelExplicit.ValueKind != JsonValueKind.False,
                ThinkingLevel = raw.TryGetProperty("thinkingLevel", out var tl) ? tl.GetString() : null,
                ThinkingLevelExplicit = !raw.TryGetProperty("thinkingLevelExplicit", out var thinkingExplicit)
                    || thinkingExplicit.ValueKind != JsonValueKind.False,
                CliType = raw.TryGetProperty("cliType", out var ct) ? ct.GetString() : null,
                QuotaWait = QuotaWaitMarker.ToStatus(QuotaWaitMarker.TryRead(jobDir, _logger)),
                Kind = TaskKinds.Normalize(raw.TryGetProperty("kind", out var kd) ? kd.GetString() : null),
                EpicId = raw.TryGetProperty("epicId", out var ep) && !string.IsNullOrWhiteSpace(ep.GetString()) ? ep.GetString() : null,
                Mode = TaskModes.Normalize(raw.TryGetProperty("mode", out var md0) ? md0.GetString() : null),
                AllowWebAccess = raw.TryGetProperty("allowWebAccess", out var awa) && awa.ValueKind == JsonValueKind.True,
                UseOwnSession = raw.TryGetProperty("useOwnSession", out var uos) && uos.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? uos.GetBoolean()
                    : null,
                Commit = legacyCommit,
                Commits = commitChain,
                IntegrationBranch = raw.TryGetProperty("integrationBranch", out var integrationBranch)
                    && integrationBranch.ValueKind == JsonValueKind.String
                    ? TaskIntegrationBranch.NormalizeRef(integrationBranch.GetString())
                    : null,
                CodeActivityDetected = DetectCodeActivity(raw, jobDir, scanSessionLog: !isArchive),
                SessionChain = ReadSessionChain(raw),
                PendingIntent = ReadPendingIntent(jobDir),
                OutcomeIssue = isArchive ? null : ResolveOutcomeIssue(jobDir, resolvedState),
                Fixture = raw.TryGetProperty("fixture", out var fix)
                    && fix.ValueKind is JsonValueKind.True,
                Phase = ReadPhase(raw, resolvedState, jobDir),
                PhaseEnteredAt = raw.TryGetProperty("phaseEnteredAt", out var phaseEntered)
                    && phaseEntered.TryGetDateTime(out var phaseEnteredAt)
                        ? phaseEnteredAt.ToUniversalTime()
                        : null,
                PostProcessingChecks = ReadPostProcessingChecks(jobDir, resolvedState),
                SteerPendingSince = ReadSteerPendingSince(jobDir, resolvedState),
                TaskType = ReadTaskType(raw),
                Tags = ReadTags(raw),
                References = ReadReferences(raw),
                RelatedWikiPages = ReadRelatedWikiPages(raw, entry),
                Provenance = ReadProvenance(raw),
                ExternalCompletion = ReadExternalCompletion(raw)
            };
            if (isArchive)
            {
                taskJsonInfo.Refresh();
                _archivedFolders[jobDir] = new ArchivedFolderSnapshot(
                    taskJsonInfo.Length,
                    taskJsonInfo.LastWriteTimeUtc,
                    info);
            }
            else
            {
                _archivedFolders.TryRemove(jobDir, out _);
            }
            return info;
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
        // Public task routes accept both the physical job id/slug and the
        // stable project key shown on cards. Remote runners receive that key
        // from the claim endpoint, so every subsequent prompt/log/completion
        // lookup must resolve the same identity after a lane move.
        IReadOnlyList<TaskInfo>? archivedSnapshot = null;
        IEnumerable<TaskInfo> liveSnapshot;
        if (_indexCache != null)
        {
            var partitions = _indexCache.GetSnapshotPartitions();
            liveSnapshot = partitions.Live;
            archivedSnapshot = partitions.Archive;
        }
        else
        {
            liveSnapshot = ScanAllJobs();
        }

        var matches = liveSnapshot.Where(j => MatchesTaskIdentity(j, jobId));
        if (!string.IsNullOrWhiteSpace(watchPath))
        {
            // Path-aware, OS-correct project match. A raw OrdinalIgnoreCase
            // string compare 404'd a card whose stored WatchPath spelled the
            // same directory differently (separator/trailing-slash) and, on
            // Linux, matched the WRONG project when two paths differed only in
            // case. See WatchPathComparison (AGT-1940).
            matches = matches.Where(j => WatchPathComparison.PathsEqual(j.WatchPath, watchPath));
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

        if (_indexCache != null)
        {
            // Archive is part of the same published cache generation. Never
            // fall back to raw archive enumeration here: that path parsed every
            // archived folder for each archived detail lookup,
            // bypassing the cache on V1 review and task-reference requests.
            resolved = archivedSnapshot!
                .Where(j => MatchesTaskIdentity(j, jobId))
                .Where(j => string.IsNullOrWhiteSpace(watchPath)
                            || WatchPathComparison.PathsEqual(j.WatchPath, watchPath))
                .ToList();
            if (resolved.Count == 1) return resolved[0];
            if (resolved.Count > 1)
            {
                _logger.LogWarning("Duplicate archived job id {JobId} found in {Count} locations; returning first archive match", jobId, resolved.Count);
                return resolved.First();
            }
        }

        return null;
    }

    private static bool MatchesTaskIdentity(TaskInfo info, string identity)
        => string.Equals(info.Id, identity, StringComparison.OrdinalIgnoreCase)
           || string.Equals(info.TaskKey, identity, StringComparison.OrdinalIgnoreCase)
           || string.Equals(info.Key, identity, StringComparison.OrdinalIgnoreCase);


    public TaskDetail? GetJobDetail(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var dir = info.FolderPath;
        var statusMd = ReadFileOrNull(Path.Combine(dir, "status.md"));
        var generated = _fileGenerationIndex?.ReadForJob(dir)
            ?? new Dictionary<string, FileGenerationMeta>(StringComparer.OrdinalIgnoreCase);
        return new TaskDetail
        {
            Info = info,
            PromptMarkdown = ReadFileOrNull(Path.Combine(dir, "prompt.md")),
            EnrichmentReport = PromptEnrichmentService.ReadReport(dir),
            PromptHistory = ReadPromptHistory(dir),
            TitleHistory = TitleHistoryLog.Read(dir),
            StatusMarkdown = statusMd,
            StatusGeneration = generated.GetValueOrDefault("status.md"),
            ContextUsage = ReadContextUsage(dir),
            Log = BuildLog(dir),
            SummaryState = ResolveSummaryState(info.TaskKey, statusMd),
            ReviewEvidence = ReviewEvidenceLog.ReadLatestPerId(dir, _logger)
        };
    }

    /// <summary>
    /// Builds the pre-filled coding-task draft for "promote a finished
    /// planning task" (see docs/concepts/planning-research-task-kinds-2026-05.md).
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

    /// <summary>
    /// Reads the validated implementation-card proposals from a delivered
    /// concept Workbench. The published document is the durable source of
    /// truth, not the agent's status report.
    /// </summary>
    public PromoteConceptResponse? BuildPromoteConceptPlan(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null || !TaskModes.IsConcept(info.Mode)) return null;

        var publication = AgentStudio.Pipeline.ConceptWorkbenchStore.Read(info.FolderPath);
        if (publication == null || string.IsNullOrWhiteSpace(publication.RepoRelativeDirectory))
            return null;

        var entry = GetWatchPaths().FirstOrDefault(candidate =>
            WatchPathComparison.PathsEqual(candidate.Path, info.WatchPath));
        var repositoryRoot = entry?.RepositoryPath;
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            repositoryRoot = entry?.RootPath;
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            return null;

        var review = AgentStudio.Pipeline.ConceptWorkbenchContract.ReviewDirectory(
            repositoryRoot, publication.RepoRelativeDirectory);
        if (!review.IsComplete || review.Descriptor == null) return null;

        return new PromoteConceptResponse
        {
            Source = new ConceptSourceDocument
            {
                RepoRelativePath = publication.RepoRelativeEntrypoint,
                Title = review.Descriptor.Title,
            },
            Items = review.Descriptor.ImplementationTasks,
            WatchPath = info.WatchPath,
            ProjectName = info.ProjectName,
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
    ///
    /// <para><paramref name="scanSessionLog"/> is <c>false</c> on the slim
    /// archive-hydration path: an archived card only needs the O(1) inline
    /// auto-commit check, not the full <c>session-events.jsonl</c> scan that
    /// dominates the per-folder cost. Archived tasks that landed work carry the
    /// inline <c>commit</c> field, so the flag stays correct for them; the only
    /// loss is the rare archived task whose sole evidence of code activity was a
    /// HEAD-moving session range with no stamped commit, which the terminal lane
    /// does not surface anyway.</para>
    /// </summary>
    private const int SessionEventScanBytes = 256 * 1024;

    private static bool DetectCodeActivity(JsonElement raw, string jobFolder, bool scanSessionLog = true)
    {
        if (raw.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object)
            return true;

        if (!scanSessionLog) return false;

        var sessionLog = TaskPaths.SessionEventsLog(jobFolder);
        if (!File.Exists(sessionLog)) return false;

        // The latest run is the useful signal and session logs can grow for the
        // lifetime of a task. Bound scanner work to a tail window instead of
        // parsing an unbounded JSONL file on every snapshot refresh.
        var tail = ReadTailUtf8(sessionLog, SessionEventScanBytes);
        foreach (var line in tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
    /// <c>docs/concepts/expanded-lifecycle-lanes-plan-2026-05.md</c>: existing
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
            var warningKey = $"unknown\n{jobDir}\n{state}\n{value}";
            if (_warnedInvalidPhases.TryAdd(warningKey, 0))
                _logger.LogWarning("Unknown phase '{Phase}' in {Dir}; ignoring", value, jobDir);
            return null;
        }
        if (!LifecyclePhases.IsAllowed(state, value))
        {
            var warningKey = $"state\n{jobDir}\n{state}\n{value}";
            if (_warnedInvalidPhases.TryAdd(warningKey, 0))
                _logger.LogWarning("Phase '{Phase}' is not allowed for state '{State}' in {Dir}; ignoring", value, state, jobDir);
            return null;
        }
        return value;
    }

    private List<LifecycleCheck> ReadPostProcessingChecks(string jobDir, string state)
    {
        if (!string.Equals(state, TaskStates.AutoReview, StringComparison.Ordinal)) return [];
        var path = Path.Combine(jobDir, "lifecycle.json");
        if (!File.Exists(path)) return [];
        try
        {
            var snapshot = JsonSerializer.Deserialize<LifecycleSnapshot>(
                File.ReadAllText(path), TaskJsonFile.ReadOpts);
            return snapshot?.PostProcessingChecks ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to read post-processing lifecycle checks from {Path}; returning an empty projection",
                path);
            return [];
        }
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

    private static List<RelatedWikiPage> ReadRelatedWikiPages(JsonElement raw, WatchPathEntry entry)
    {
        if (!raw.TryGetProperty("relatedWikiPages", out var refs) || refs.ValueKind != JsonValueKind.Array)
            return [];
        try
        {
            var pages = JsonSerializer.Deserialize<List<RelatedWikiPage>>(refs.GetRawText(), TaskJsonFile.ReadOpts) ?? [];
            var root = string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RootPath : entry.RepositoryPath;
            return pages
                .Where(p => !string.IsNullOrWhiteSpace(p.RelPath))
                .GroupBy(p => p.RelPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First() with
                {
                    Exists = !string.IsNullOrWhiteSpace(root)
                        && File.Exists(Path.Combine(root!, g.First().RelPath.Replace('/', Path.DirectorySeparatorChar)))
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads the append-only <c>provenance</c> object (ASS-1724) if present.
    /// Returns null on legacy <c>task.json</c> files that predate the field.
    /// </summary>
    private static TaskProvenance? ReadProvenance(JsonElement raw)
    {
        if (!raw.TryGetProperty("provenance", out var prov) || prov.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            return JsonSerializer.Deserialize<TaskProvenance>(prov.GetRawText(), TaskJsonFile.ReadOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the <c>externalCompletion</c> object written by the out-of-band
    /// completion endpoint. Returns null on tasks finished through the normal
    /// runner/review path (the common case). See
    /// <see cref="ExternalCompletionInfo"/>.
    /// </summary>
    private static ExternalCompletionInfo? ReadExternalCompletion(JsonElement raw)
    {
        if (!raw.TryGetProperty("externalCompletion", out var ext) || ext.ValueKind != JsonValueKind.Object)
            return null;
        try
        {
            return JsonSerializer.Deserialize<ExternalCompletionInfo>(ext.GetRawText(), TaskJsonFile.ReadOpts);
        }
        catch
        {
            return null;
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

    /// <summary>
    /// Run-Liveness Slice B: the UTC time a 3-progress card's steer-pending wait
    /// started, read from the durable <c>steer-pending.json</c> marker. Only
    /// 3-progress cards carry the wait, so the file probe is skipped otherwise.
    /// The wait-start is read directly (not through the runner marker type) to
    /// keep the scanner free of a Runner-layer dependency.
    /// </summary>
    private static DateTime? ReadSteerPendingSince(string jobFolder, string state)
    {
        if (!string.Equals(state, TaskStates.Progress, StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(jobFolder, "steer-pending.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("waitStartedAt", out var el)
                && el.ValueKind == JsonValueKind.String
                && el.TryGetDateTime(out var dt))
                return dt.ToUniversalTime();
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "TaskScannerService: torn / unreadable steer-pending.json - no wait pill");
        }
        return null;
    }

    private const int OutcomeIssueTailBytes = 16 * 1024;

    private static TaskOutcomeIssue? ResolveOutcomeIssue(string jobFolder, string state)
    {
        var logPath = TaskPaths.CliOutputLog(jobFolder);
        if (!File.Exists(logPath)) return null;

        var tail = ReadTailUtf8(logPath, OutcomeIssueTailBytes);
        if (string.IsNullOrWhiteSpace(tail)) return null;

        var acceptedVerdictSeen = string.Equals(state, TaskStates.Completed, StringComparison.OrdinalIgnoreCase);
        var lastSeenAt = File.GetLastWriteTimeUtc(logPath);
        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();

            // Reconcile with the final verdict: the orchestrator's accept note
            // (move to 5-human-review / 6-completed) supersedes only the
            // verdict-contradicting ambiguity chips. Non-suppressible issues,
            // such as a failed task-branch push, must remain visible after the
            // lane move.
            if (IsAcceptReconcileLine(line))
            {
                acceptedVerdictSeen = true;
                continue;
            }

            if (line.Contains("[agent-git-violation]", StringComparison.OrdinalIgnoreCase))
                return BuildOutcomeIssue("agent-git-violation", "Agent git violation", "High", line, lastSeenAt);
            if (line.Contains("[worker-head-advanced]", StringComparison.OrdinalIgnoreCase))
                return BuildOutcomeIssue("worker-head-advanced", "Worker advanced HEAD", "Info", line, lastSeenAt);

            // Never derive an outcome issue from an orchestrator decision/reissue/
            // meta line or a supervisor line. Those carry prose - e.g. an accept
            // reason that mentions "classifier-unknown" - that must not be read as
            // a runner outcome and must never become the issue summary. The typed
            // issue tags ([classifier-unknown], [missing-terminal-sentinel], ...)
            // are NOT meta, so genuine outcome lines are still derived.
            if (IsOrchestratorMetaLine(line)) continue;

            if (TryResolveOutcomeIssue(line, lastSeenAt, out var issue))
            {
                // An accepted card should drop stale ambiguity chips, but keep
                // scanning because an older non-suppressible issue can still be
                // the real visible outcome.
                if (TaskOutcomeIssueReconciliation.ShouldSuppress(issue, acceptedVerdictSeen))
                    continue;
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

    internal static string ReadTailUtf8(string path, int maxBytes)
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
        if (lower.Contains("[tool-router-error]"))
        {
            issue = BuildOutcomeIssue("tool-router-error", "Tool router error", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("[no-reply]"))
        {
            issue = BuildOutcomeIssue("no-reply", "No reply", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("empty-fast-exit"))
        {
            issue = BuildOutcomeIssue("empty-fast-exit", "Empty fast exit", "High", line, lastSeenAt);
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
        if (lower.Contains("[worker-head-advanced]"))
        {
            issue = BuildOutcomeIssue("worker-head-advanced", "Worker advanced HEAD", "Info", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("[integration-conflict]"))
        {
            issue = BuildOutcomeIssue("integration-conflict", "Integration conflict", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("[integration-error]"))
        {
            issue = BuildOutcomeIssue("integration-error", "Integration error", "High", line, lastSeenAt);
            return true;
        }
        if (lower.Contains("[task-branch-unpushed]"))
        {
            issue = BuildOutcomeIssue("task-branch-unpushed", "Task branch unpushed", "Warn", line, lastSeenAt);
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
    {
        var technicalDetails = NormalizeOutcomeLine(rawLine);
        return new()
        {
            Kind = kind,
            Label = label,
            Severity = severity,
            Summary = SummarizeOutcomeLine(technicalDetails),
            TechnicalDetails = technicalDetails,
            LastSeenAt = lastSeenAt
        };
    }

    private static string NormalizeOutcomeLine(string line)
    {
        var trimmed = line.Trim();
        var end = trimmed.IndexOf(']');
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && end > 0)
        {
            trimmed = trimmed[(end + 1)..].Trim();
        }
        return trimmed;
    }

    private static string SummarizeOutcomeLine(string normalizedLine)
    {
        var trimmed = normalizedLine.Trim();
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

        // Editable / always-known files plus any supported document the agents
        // or operators drop in the job root (surfaced by the Files tab).
        // Structured aspect verdicts also ship as `aspect-*.json`; those are
        // served too so the Files tab can fetch and render them structurally.
        // HTML is interactive only inside the frontend's allow-scripts sandbox;
        // allow-same-origin stays deliberately omitted there.
        var allowed = new[] { "prompt.md", "status.md", "task.json" };
        var isMarkdown = fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        var isHtml = fileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
        var isAspectJson = fileName.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (!allowed.Contains(fileName) && !isMarkdown && !isHtml && !isAspectJson) return null;

        return ReadFileOrNull(Path.Combine(info.FolderPath, fileName));
    }

    /// <summary>
    /// Lists every supported document directly in the job root, sorted for the
    /// Files tab (prompt first, then aspect-* alphabetical, then *_NOTE / *_NOTES
    /// alphabetical, then everything else). Supported documents are Markdown,
    /// HTML, and structured aspect JSON. <c>status.md</c> is excluded because it
    /// has its own Protocol tab. Subfolders
    /// (<c>logs/</c>, <c>results/</c>, <c>attachments/</c>) are out of scope.
    /// </summary>
    public TaskArtifactsResponse? ListArtifacts(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var dir = info.FolderPath;
        if (!Directory.Exists(dir)) return new TaskArtifactsResponse { JobId = jobId, Files = [] };

        var generated = _fileGenerationIndex?.ReadForJob(dir)
            ?? new Dictionary<string, FileGenerationMeta>(StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<TaskArtifact>();

        // Structured aspect JSON is the preferred (source-of-truth) artefact:
        // list it, and remember its stem so the markdown twin below is
        // suppressed — one card per aspect, not two. Legacy runs that only
        // wrote the markdown still surface it (their stem is never recorded).
        var suppressedMdTwins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(dir, "aspect-*.json", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var artifact = BuildArtifact(path, name, generated);
            if (artifact is null) continue;
            artifacts.Add(artifact);
            suppressedMdTwins.Add(Path.GetFileNameWithoutExtension(name) + ".md"); // aspect-x.json -> aspect-x.md
        }

        foreach (var path in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (string.Equals(name, "status.md", StringComparison.OrdinalIgnoreCase)) continue;
            if (suppressedMdTwins.Contains(name)) continue;

            var artifact = BuildArtifact(path, name, generated);
            if (artifact is null) continue;
            artifacts.Add(artifact);
        }

        // Self-contained HTML artifacts use the same Files-tab card contract as
        // Markdown. The frontend renders them with scripts enabled in an opaque
        // origin, so they can be interactive without Studio DOM or state access.
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)) continue;

            var artifact = BuildArtifact(path, name, generated);
            if (artifact is null) continue;
            artifacts.Add(artifact);
        }

        artifacts.Sort(CompareArtifactsForFilesTab);

        return new TaskArtifactsResponse { JobId = jobId, Files = artifacts };
    }

    private static TaskArtifact? BuildArtifact(
        string path, string name, IReadOnlyDictionary<string, FileGenerationMeta> generated)
    {
        var (kind, aspectName) = ClassifyArtifact(name);
        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch { return null; }

        return new TaskArtifact
        {
            Name = name,
            SizeBytes = fi.Length,
            Mtime = fi.LastWriteTimeUtc,
            Kind = kind,
            AspectName = aspectName,
            Generation = generated.GetValueOrDefault(name),
        };
    }

    private static (TaskArtifactKind Kind, string? AspectName) ClassifyArtifact(string fileName)
    {
        if (string.Equals(fileName, "prompt.md", StringComparison.OrdinalIgnoreCase))
            return (TaskArtifactKind.Prompt, null);

        // Aspect verdicts ship as a structured `.json` source of truth plus a
        // human-readable `.md` twin; both classify as Aspect so the Files tab
        // renders either shape (structured card for JSON, markdown for legacy).
        if (fileName.StartsWith("aspect-", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var ext in new[] { ".json", ".md" })
            {
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                {
                    var aspect = fileName.Substring(
                        "aspect-".Length, fileName.Length - "aspect-".Length - ext.Length);
                    return (TaskArtifactKind.Aspect, aspect);
                }
            }
        }

        if (fileName.StartsWith("code-review-", StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return (TaskArtifactKind.CodeReview, null);

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
            TaskArtifactKind.CodeReview => 2,
            TaskArtifactKind.Note   => 3,
            _                      => 4,
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
    /// mapping. See <c>docs/system/contracts/protocol-style.md</c> for the folder contract.
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
            // Directory mtimes plus the small set of files that drive card
            // freshness are a bounded approximation. The former recursive
            // walk visited every result and log artifact in every live task,
            // making one snapshot refresh proportional to workspace history.
            var latest = Directory.GetLastWriteTimeUtc(dir);
            foreach (var subdirectory in new[] { "logs", "results", "attachments" })
            {
                var path = Path.Combine(dir, subdirectory);
                if (Directory.Exists(path))
                    latest = Max(latest, Directory.GetLastWriteTimeUtc(path));
            }
            foreach (var path in new[]
                     {
                         Path.Combine(dir, "task.json"),
                         Path.Combine(dir, "status.md"),
                         TaskPaths.CliOutputLog(dir),
                         TaskPaths.SessionEventsLog(dir),
                     })
            {
                if (File.Exists(path))
                    latest = Max(latest, File.GetLastWriteTimeUtc(path));
            }
            return latest.ToLocalTime();
        }
        catch { return Directory.GetLastWriteTime(dir); }

        static DateTime Max(DateTime left, DateTime right) => left >= right ? left : right;
    }

}
