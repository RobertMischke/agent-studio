using System.Globalization;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Registry;

namespace OrchestratorApi.Services.Tasks;

/// <summary>
/// Field-level writes against an existing job: the per-field setters
/// (model, cli-type, title, useOwnSession, commit, contextUsage),
/// editing of <c>prompt.md</c>, the create-job flow that mints a new
/// job folder, the binary attachment uploader, and the
/// continuation-note appender that records user follow-ups into
/// <c>prompt.md</c>.
///
/// Pattern: every public method does <see cref="TaskScannerService.FindJob"/>
/// → <see cref="TaskJsonFile.UpdateField"/>. Splitting this out of the
/// scanner keeps the read surface focused on read and makes the write
/// surface easy to grep when a "where do we touch this field" question
/// comes up.
/// </summary>
public class TaskMutationService
{
    private readonly TaskScannerService _scanner;
    private readonly ClientIdentityStore _clients;
    private readonly ProjectRegistry _projectRegistry;
    private readonly TaskChangeNotifier _notifier;
    private readonly ILogger<TaskMutationService> _logger;
    // ADR-0049: optional unified-timeline writer. Production DI supplies it;
    // tests that construct TaskMutationService directly may pass null and
    // simply skip the timeline event.
    private readonly TimelineLog? _timeline;
    // Lane mutex: serialise the slug-uniqueness check + folder create in
    // CreateJob with the other lane writers (move/archive/delete) so two
    // concurrent creates cannot pick the same dedupe suffix and land two
    // folders on the same slug. Optional so test fixtures that build the
    // service directly keep compiling; the NullSingleton still serialises.
    private readonly LaneMutexRegistry _laneMutex;

    public TaskMutationService(TaskScannerService scanner, ClientIdentityStore clients, ProjectRegistry projectRegistry, TaskChangeNotifier notifier, ILogger<TaskMutationService> logger, TimelineLog? timeline = null, LaneMutexRegistry? laneMutex = null)
    {
        _scanner = scanner;
        _clients = clients;
        _projectRegistry = projectRegistry;
        _notifier = notifier;
        _logger = logger;
        _timeline = timeline;
        _laneMutex = laneMutex ?? LaneMutexRegistry.NullSingleton;
    }

    /// <summary>
    /// Cycle 2: every public mutation that writes to disk routes its
    /// success return through this helper so the in-memory snapshot is
    /// invalidated synchronously. Without this, a POST-then-GET sequence
    /// (e.g. SetJobTitle then refresh) could see the pre-write snapshot
    /// for up to 250 ms (the FileSystemWatcher debounce window).
    /// Also publishes a typed <c>jobUpdated</c> event to
    /// <see cref="TaskChangeNotifier"/> so the SignalR hub can push the
    /// change to connected clients without waiting for the next poll.
    /// </summary>
    private bool Updated(TaskInfo info)
    {
        _scanner.InvalidateCache();
        _notifier.PublishUpdated(info.ProjectName, info.Id, info.WatchPath);
        return true;
    }

    /// <summary>
    /// Folder-only invalidation for the internal helpers
    /// (<see cref="SetJobCommitOnFolder"/>, <see cref="AppendJobCommitOnFolder"/>,
    /// <see cref="SetJobLastProgressAt"/>, <see cref="SetJobPhase"/>) whose
    /// callers do not always have a <see cref="TaskInfo"/> in hand. Skips the
    /// SignalR push: those code paths are either user-invisible heartbeats
    /// (<c>lastProgressAt</c>), or land on a job that the user-facing
    /// surface (Move, CreateJob) is about to push for separately.
    /// </summary>
    private bool Updated() { _scanner.InvalidateCache(); return true; }

    public bool SetJobModel(string jobId, string? model, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var normalizedModel = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        TaskJsonFile.UpdateField(info.FolderPath, "model", normalizedModel ?? "", _logger);
        TaskJsonFile.UpdateField(
            info.FolderPath,
            "thinkingLevel",
            CliThinkingLevels.Normalize(info.CliType, normalizedModel, info.ThinkingLevel) ?? "",
            _logger);
        return Updated();
    }

    public bool SetJobThinkingLevel(string jobId, string? thinkingLevel, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var normalized = CliThinkingLevels.Normalize(info.CliType, info.Model, thinkingLevel);
        TaskJsonFile.UpdateField(info.FolderPath, "thinkingLevel", normalized ?? "", _logger);
        return Updated();
    }

    /// <summary>
    /// Epics assignment way 2 (post-hoc): attach this task to a parent epic, or
    /// detach it (<paramref name="epicId"/> null/empty clears the link). Writes
    /// the <c>epicId</c> field on task.json via the mutation layer (API-only
    /// job-folder rule, ADR-0024). Returns false when the task is not found.
    /// </summary>
    public bool SetJobEpic(string jobId, string? epicId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "epicId", epicId ?? "", _logger);
        return Updated();
    }

    public bool SetJobCliType(string jobId, string cliType, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var normalized = CliTypes.Normalize(cliType);
        TaskJsonFile.UpdateField(info.FolderPath, "cliType", normalized, _logger);
        TaskJsonFile.UpdateField(
            info.FolderPath,
            "thinkingLevel",
            CliThinkingLevels.Normalize(normalized, info.Model, info.ThinkingLevel) ?? "",
            _logger);
        // Keep the parallel `agent` field in lockstep with `cliType`. The two
        // were originally meant to address different layers (which CLI vs.
        // which logical agent) but every supported CLI maps 1:1 to one agent
        // value, and the kanban card's text label reads `agent` while the
        // icon reads `cliType`. A mass-flip of cliType without syncing agent
        // produced cards showing "claude" with the Codex icon on 2026-05-12
        // (see job bug-clitype-and-agent-fields-drift-on-mass-flip).
        TaskJsonFile.UpdateField(info.FolderPath, "agent", normalized, _logger);
        // Switching CLI invalidates the previous session - clear it so the next run mints a new one.
        if (!string.Equals(normalized, info.CliType, StringComparison.OrdinalIgnoreCase))
        {
            TaskJsonFile.UpdateField(info.FolderPath, "sessionName", "", _logger);
        }
        return Updated();
    }

    public bool SetJobUseOwnSession(string jobId, bool useOwn, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "useOwnSession", useOwn, _logger);
        return Updated();
    }

    public bool SetJobCommit(string jobId, TaskCommitInfo commit, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        return AppendJobCommitOnFolder(info.FolderPath, commit);
    }

    public bool SetJobCommitOnFolder(string folderPath, TaskCommitInfo commit)
    {
        if (!Directory.Exists(folderPath)) return false;
        AppendJobCommitOnFolder(folderPath, commit);
        return Updated();
    }

    /// <summary>
    /// Append a new commit to the task's commit chain in <c>task.json</c>.
    /// Existing entries are preserved (oldest -&gt; newest); the singular
    /// legacy <c>commit</c> field is also updated to mirror the newest
    /// entry so consumers that still read the old shape see the latest
    /// commit, not a stale first one. Dedupes by SHA so a re-stamp from
    /// the same SHA does not bloat the chain - in that case we replace
    /// the existing entry in place to refresh metadata
    /// (file count, message, timestamp) without re-ordering.
    ///
    /// <para>
    /// Tasks regularly produce more than one commit across iterations:
    /// continue-mode adds a follow-up, crash-recovery leaves a recovery
    /// commit plus a follow-up, operator-driven steers ("change this and
    /// continue") often add a separate commit. Each of those goes
    /// through this method so the detail view can render the full chain.
    /// </para>
    /// </summary>
    public bool AppendJobCommitOnFolder(string folderPath, TaskCommitInfo commit)
    {
        if (!Directory.Exists(folderPath)) return false;
        var jobJsonPath = Path.Combine(folderPath, "task.json");
        if (!File.Exists(jobJsonPath)) return false;
        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, TaskJsonFile.ReadOpts)
                      ?? new Dictionary<string, JsonElement>();

            var chain = new List<TaskCommitInfo>();
            if (doc.TryGetValue("commits", out var commitsEl) && commitsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in commitsEl.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(item.GetRawText(), TaskJsonFile.ReadOpts);
                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha)) chain.Add(parsed);
                }
            }
            else if (doc.TryGetValue("commit", out var legacyEl) && legacyEl.ValueKind == JsonValueKind.Object)
            {
                var parsed = JsonSerializer.Deserialize<TaskCommitInfo>(legacyEl.GetRawText(), TaskJsonFile.ReadOpts);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha)) chain.Add(parsed);
            }

            var existingIdx = chain.FindIndex(c => string.Equals(c.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0) chain[existingIdx] = commit;
            else chain.Add(commit);

            TaskJsonFile.UpdateField(folderPath, "commit", chain[^1], _logger);
            TaskJsonFile.UpdateField(folderPath, "commits", chain, _logger);
            return Updated();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append commit to {Folder}", folderPath);
            return false;
        }
    }

    /// <summary>
    /// Persist the result of the deterministic commit-attribution post-step
    /// (ADR "Commit-Attribution-Regel"): a replace-all write of the
    /// <c>commits</c> chain (now carrying attribution + confidence). The
    /// singular legacy <c>commit</c> field is kept pointing at the newest
    /// attributed entry so old readers still see the latest commit. Idempotent:
    /// re-running with the same git state rewrites identical content.
    /// </summary>
    public bool SetCommitAttributionOnFolder(
        string folderPath,
        IReadOnlyList<TaskCommitInfo> attributed)
    {
        if (!Directory.Exists(folderPath)) return false;
        var ordered = attributed
            .Where(c => !string.IsNullOrWhiteSpace(c.Sha))
            .OrderBy(c => c.At)
            .ToList();
        return WriteCommitState(folderPath, ordered);
    }

    private bool WriteCommitState(string folderPath, List<TaskCommitInfo> chain)
    {
        try
        {
            TaskJsonFile.UpdateField(folderPath, "commits", chain, _logger);
            TaskJsonFile.UpdateField(folderPath, "commit", chain.Count > 0 ? chain[^1] : null, _logger);
            // Drop the obsolete operator-override array (removed feature) so the
            // file is not left carrying a dead field after a rewrite.
            TaskJsonFile.RemoveField(folderPath, "excludedCommits", _logger);
            return Updated();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write commit state to {Folder}", folderPath);
            return false;
        }
    }

    /// <summary>
    /// Stamp a UTC progress heartbeat onto the job's <c>task.json</c>. Written
    /// on every CLI-output flush so <see cref="OrchestratorApi.Services.Runner.CrashRecoveryService"/>
    /// can attribute orphan working-tree changes to the most-recently-active
    /// job in <c>3-progress</c> on the next backend boot. ADR-0020.
    /// </summary>
    public bool SetJobLastProgressAt(string folderPath, DateTime utcNow)
    {
        if (!Directory.Exists(folderPath)) return false;
        TaskJsonFile.UpdateField(folderPath, "lastProgressAt",
            utcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), _logger);
        // Intentionally NOT calling Updated(): this is the per-CLI-flush
        // heartbeat (called every few seconds during an active run), and
        // lastProgressAt does not surface in TaskInfo (the kanban card
        // reads LastActivity from disk mtime via GetLastActivityTime).
        // Invalidating here would force a full rescan on every CLI line.
        return true;
    }

    public bool SetJobTaskType(string jobId, string taskType, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "taskType", TaskTypes.Normalize(taskType), _logger);
        return Updated();
    }

    /// <summary>
    /// Replace-all write of the per-job tag id array. Tag ids are normalized
    /// via <see cref="NormalizeTagId"/> (lowercase, <c>[a-z0-9-]</c>, max 32
    /// chars), de-duplicated case-insensitively, and the order of the
    /// caller's list is preserved. Empty input clears the field. The registry
    /// is consulted by readers, not writers: an unknown id is accepted at
    /// write time and rendered as a ghost chip until it lands in
    /// <c>tags.json</c> (or the job is re-tagged).
    /// </summary>
    public bool SetJobTags(string jobId, IEnumerable<string> tags, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var clean = (tags ?? Array.Empty<string>())
            .Select(NormalizeTagId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        TaskJsonFile.UpdateField(info.FolderPath, "tags", clean, _logger);
        return Updated();
    }

    /// <summary>
    /// Merge-add a single tag id without clobbering existing tags. Used by
    /// runner-side typing paths (e.g. the Codex silent-completion detector
    /// stamps <c>outcome:silent-finish</c>) where the call site does not own
    /// the full tag set and must not race with operator-authored tags. The
    /// tag id is normalised through <see cref="NormalizeTagId"/>; an
    /// already-present id (case-insensitive) is a no-op that still returns
    /// <c>true</c>. Returns <c>false</c> when the job cannot be found or the
    /// supplied id normalises to empty.
    /// </summary>
    public bool AddJobTag(string jobId, string tag, string? watchPath = null)
    {
        var normalized = NormalizeTagId(tag);
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var existing = (IReadOnlyList<string>?)info.Tags ?? Array.Empty<string>();
        if (existing.Any(t => string.Equals(NormalizeTagId(t), normalized, StringComparison.OrdinalIgnoreCase)))
            return true;
        var merged = existing
            .Select(NormalizeTagId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Append(normalized)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        TaskJsonFile.UpdateField(info.FolderPath, "tags", merged, _logger);
        return Updated();
    }

    /// <summary>
    /// F34: replace-all write of the structured <c>references</c> object
    /// (dependsOn / relatedTo / blockedBy / supersedes). The supplied set is
    /// normalised (trim, drop blanks, de-duplicate case-insensitively per kind)
    /// and written atomically. Validation that referenced keys exist, that the
    /// task does not reference itself, and that dependsOn stays a DAG is the
    /// caller's job (the endpoint owns the cross-task graph); this writer is
    /// deliberately thin so it can also persist a programmatically-built set.
    /// </summary>
    public bool SetTaskReferences(string jobId, TaskReferences references, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var clean = TaskReferenceValidator.Normalize(references ?? new TaskReferences());
        TaskJsonFile.UpdateField(info.FolderPath, "references", clean, _logger);
        _logger.LogInformation(
            "task-references-set job={JobId} dependsOn={DependsOn} relatedTo={RelatedTo} blockedBy={BlockedBy} supersedes={Supersedes}",
            jobId, clean.DependsOn.Count, clean.RelatedTo.Count, clean.BlockedBy.Count, clean.Supersedes.Count);
        return Updated();
    }

    /// <summary>
    /// Coerce arbitrary user input into the tag-id grammar: lowercase ASCII
    /// letters, digits, and hyphens, length 1..32. Unknown characters are
    /// dropped (the spec calls for silent server-side stripping; an empty
    /// result is filtered upstream so an all-junk input becomes a no-op).
    /// </summary>
    public static string NormalizeTagId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(Math.Min(s.Length, 32));
        foreach (var c in s)
        {
            if (sb.Length >= 32) break;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-') sb.Append(c);
            else if (c == ' ' || c == '_') sb.Append('-');
        }
        // collapse runs of '-' and trim
        var collapsed = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return collapsed;
    }

    public bool SetJobTitle(string jobId, string title, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var trimmed = title.Trim();
        // Only record a history entry when the value actually changes.
        // A no-op rename (re-submitting the same string from a stale UI)
        // would otherwise bloat the audit trail with identical rows.
        var previous = info.Title ?? "";
        if (!string.Equals(previous, trimmed, StringComparison.Ordinal))
        {
            TitleHistoryLog.Append(info.FolderPath, new TaskTitleHistoryEntry
            {
                At = DateTime.UtcNow,
                OldTitle = previous,
                NewTitle = trimmed,
                Source = "api"
            }, _logger);
            _logger.LogInformation(
                "title-changed jobId={JobId} old={Old} new={New}",
                jobId, previous, trimmed);
        }
        TaskJsonFile.UpdateField(info.FolderPath, "title", trimmed, _logger);
        return Updated();
    }

    public bool UpdateContextUsage(string jobId, ContextUsageSnapshot snapshot, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "contextUsage", snapshot, _logger);
        // Skip Updated(): contextUsage is not surfaced on the kanban card and
        // updates fire on every CLI activity flush during an active run.
        return true;
    }

    /// <summary>
    /// Application-owned write of the optional <c>phase</c> field on a job's
    /// <c>task.json</c>. Used by the orchestrator-intake hosted service to
    /// move a 2-ready card through <c>human-ready → intake-running →
    /// intake-passed | intake-blocked</c> without changing the filesystem
    /// state. Pass <see cref="LifecyclePhases.IsAllowed"/> values; an empty
    /// string clears the field. Validation against the state happens at the
    /// scanner so a corrupt write is rendered inert rather than fatal.
    /// </summary>
    public bool SetJobPhase(string folderPath, string? phase)
    {
        if (!Directory.Exists(folderPath)) return false;
        TaskJsonFile.UpdateField(folderPath, "phase", phase ?? "", _logger);
        return Updated();
    }

    /// <summary>
    /// Application-owned write of the append-only <c>provenance</c> object on a
    /// job's <c>task.json</c> (ASS-1724). The caller
    /// (<see cref="TaskProvenanceService"/>) owns the append semantics - it reads
    /// the current provenance off <see cref="TaskInfo"/>, adds one transition, and
    /// hands the merged record here for a replace-all write. Folder-only
    /// invalidation: provenance is not surfaced on the kanban card, so no SignalR
    /// push is needed; the read endpoint pulls it fresh on demand.
    /// </summary>
    public bool SetProvenanceOnFolder(string folderPath, TaskProvenance provenance)
    {
        if (!Directory.Exists(folderPath)) return false;
        TaskJsonFile.UpdateField(folderPath, "provenance", provenance, _logger);
        return Updated();
    }

    public string? CreateJob(CreateJobRequest req)
    {
        var watchPaths = _scanner.GetWatchPaths();
        var entry = string.IsNullOrEmpty(req.WatchPath)
            ? watchPaths.FirstOrDefault()
            : watchPaths.FirstOrDefault(w => w.Path == req.WatchPath);

        if (entry == null) return null;

        // Backlog is the default landing lane: a job created with no explicit
        // targetState lands in 0-backlog (triage staging) instead of 1-preparation.
        // Callers that want the legacy "create-and-prep" or "create-and-ready"
        // behavior pass an explicit targetState. This is the load-bearing
        // semantics from the backlog-lane spec: preparation is for *active*
        // prep, not raw intake.
        var targetState = req.TargetState switch
        {
            TaskStates.Backlog => TaskStates.Backlog,
            TaskStates.Preparation => TaskStates.Preparation,
            TaskStates.Ready => TaskStates.Ready,
            _ => TaskStates.Backlog
        };

        // Sanitize ID: transliterate umlauts, lowercase, replace spaces with dashes, only allow safe chars
        var baseSlug = string.IsNullOrWhiteSpace(req.Id)
            ? ToSlug(req.Title)
            : req.Id;
        if (string.IsNullOrEmpty(baseSlug)) return null;

        var taskKey = MintTaskKey(entry.Path);
        var storageId = taskKey ?? baseSlug;
        TaskStorageLayout.TryParseKeyNumber(storageId, out var storageNumber);

        // Root-cause fix for duplicate-slug folders: the external id must be
        // unique across the project even though it is no longer the physical
        // folder name. Reserve the resolved id + create the jobs/<bucket>/<key>
        // folder under the lane mutex so concurrent creates cannot pick the
        // same external id or storage id.
        string jobId;
        string jobDir;
        using (_laneMutex.Acquire(entry.Path))
        {
            jobId = EnsureUniqueJobId(entry.Path, baseSlug);
            jobDir = TaskStorageLayout.JobDir(entry.Path, storageNumber, storageId);
            for (var suffix = 2; Directory.Exists(jobDir); suffix++)
            {
                storageId = $"{taskKey ?? baseSlug}-{suffix}";
                TaskStorageLayout.TryParseKeyNumber(storageId, out storageNumber);
                jobDir = TaskStorageLayout.JobDir(entry.Path, storageNumber, storageId);
            }
            Directory.CreateDirectory(jobDir);
        }
        if (!string.Equals(jobId, baseSlug, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "job-slug-deduped baseSlug={BaseSlug} resolvedSlug={ResolvedSlug} watchPath={WatchPath} targetState={TargetState}",
                baseSlug, jobId, entry.Path, targetState);
        }

        // Land new jobs at the bottom of their target lane so the visible order
        // in the UI matches the backend pickup order (OrderBy(Order) ascending).
        // Falling back to the request's default (999) collides every new job on
        // the same key, so tie-break would depend on filesystem scan order and
        // the user has no way to predict which one runs next.
        var existingMaxOrder = _scanner.ScanAllJobs()
            .Where(j => j.WatchPath == entry.Path && j.State == targetState)
            .Select(j => (int?)j.Order)
            .Max();
        var resolvedOrder = req.Order != 999 ? req.Order : (existingMaxOrder ?? 0) + 10;

        var ownerClientId = !string.IsNullOrWhiteSpace(req.OwnerClientId)
            ? req.OwnerClientId!
            : DefaultClientIdentity.Id;

        // Materialize effective agent/cliType/model from the owner's
        // client defaults when the request does not carry explicit values.
        // This prevents the old "agent: human, cliType: null, model: null"
        // triple that was misleading on the card and in the dataset.
        var ownerIdentity = _clients.Find(ownerClientId);
        var effectiveCliType = !string.IsNullOrWhiteSpace(req.CliType)
            ? CliTypes.Normalize(req.CliType)
            : (ownerIdentity?.DefaultCliType is { } dc && CliTypes.IsValid(dc)
                ? CliTypes.Normalize(dc)
                : null);
        var effectiveModel = !string.IsNullOrWhiteSpace(req.Model)
            ? req.Model.Trim()
            : ownerIdentity?.DefaultModel;
        var effectiveThinkingLevel = CliThinkingLevels.Normalize(
            effectiveCliType,
            effectiveModel,
            !string.IsNullOrWhiteSpace(req.ThinkingLevel)
                ? req.ThinkingLevel
                : ownerIdentity?.DefaultThinkingLevel);
        var effectiveAgent = string.Equals(req.Agent, AgentTypes.Human, StringComparison.OrdinalIgnoreCase)
            ? AgentTypes.Human
            : effectiveCliType ?? req.Agent;

        var materializedFromDefaults = effectiveCliType != null && string.IsNullOrWhiteSpace(req.CliType);
        if (materializedFromDefaults)
        {
            _logger.LogInformation(
                "job-defaults-materialized jobId={JobId} owner={Owner} agent={Agent} cliType={CliType} model={Model}",
                jobId, ownerClientId, effectiveAgent, effectiveCliType, effectiveModel);
        }

        var jobJson = new Dictionary<string, object?>
        {
            ["id"] = jobId,
            ["title"] = req.Title,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            // Lane-entry sort anchor: a freshly created task has just entered
            // its initial lane, so stamp it now. Re-stamped on every move.
            ["enteredLaneAt"] = DateTime.UtcNow.ToString("o"),
            ["state"] = targetState,
            ["order"] = resolvedOrder,
            ["agent"] = effectiveAgent,
            ["ownerClientId"] = ownerClientId
        };
        if (!string.IsNullOrWhiteSpace(effectiveModel))
            jobJson["model"] = effectiveModel;
        if (!string.IsNullOrWhiteSpace(effectiveThinkingLevel))
            jobJson["thinkingLevel"] = effectiveThinkingLevel;
        if (!string.IsNullOrWhiteSpace(effectiveCliType))
            jobJson["cliType"] = effectiveCliType;
        // Epics: card kind (task|epic) + optional parent epic (assignment way 1,
        // at create time). kind is always written so a fresh task.json is explicit.
        jobJson["kind"] = TaskKinds.Normalize(req.Kind);
        if (!string.IsNullOrWhiteSpace(req.EpicId))
            jobJson["epicId"] = req.EpicId;
        // Execution mode (coding|planning|research) + web-access. mode is always
        // written; web access defaults by mode when the request omits it
        // (research on, else off) - see planning-research-task-kinds note.
        var effectiveMode = TaskModes.Normalize(req.Mode);
        jobJson["mode"] = effectiveMode;
        jobJson["allowWebAccess"] = req.AllowWebAccess ?? (effectiveMode == TaskModes.Research);
        if (req.Fixture)
            jobJson["fixture"] = true;

        // taskType is always written so a fresh task.json carries an explicit
        // value. Legacy folders without the field render as Chore (the
        // scanner's lazy default), so we only emit it on create.
        jobJson["taskType"] = TaskTypes.Normalize(req.TaskType);

        if (req.Tags is { Count: > 0 })
        {
            jobJson["tags"] = req.Tags
                .Select(NormalizeTagId)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        jobJson["key"] = storageId;

        File.WriteAllText(Path.Combine(jobDir, "task.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));

        if (!string.IsNullOrWhiteSpace(req.PromptMarkdown))
            File.WriteAllText(Path.Combine(jobDir, "prompt.md"), req.PromptMarkdown);

        var location = TaskStorageLayout.Location(storageNumber, storageId);
        TaskLayoutIndex.Upsert(entry.Path, storageId, location, targetState, _logger);

        // ADR-0049: open the per-job timeline with prompt_created so the
        // Overview strip has something to render before the first agent run.
        _timeline?.Append(
            jobDir,
            TimelineEventKinds.PromptCreated,
            string.IsNullOrWhiteSpace(req.Agent) ? TimelineActors.System : TimelineActors.Human(ownerClientId),
            summary: string.IsNullOrWhiteSpace(req.Title) ? $"Task {jobId} created" : $"Task created: {req.Title}",
            payloadRef: "prompt.md",
            details: new()
            {
                ["targetState"] = targetState ?? string.Empty,
                ["agent"] = effectiveAgent ?? string.Empty,
            });

        _scanner.InvalidateCache();
        // Push a typed jobCreated to connected clients so other tabs render
        // the new card within ~1s instead of waiting for the next board poll.
        // Resolve the just-written TaskInfo so the bridge can ship the canonical
        // row; on the rare miss the bridge falls back to a bulk re-pull.
        var created = _scanner.FindJob(jobId, entry.Path);
        _notifier.PublishCreated(created?.ProjectName ?? string.Empty, jobId, entry.Path);
        return jobId;
    }

    private string EnsureUniqueJobId(string watchPath, string baseSlug)
    {
        var used = _scanner.ScanAllJobs()
            .Where(j => string.Equals(j.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase))
            .Select(j => j.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(baseSlug)) return baseSlug;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseSlug}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    public bool UpdateJobFile(string jobId, string fileName, string content, string? watchPath = null)
    {
        var allowed = new[] { "prompt.md" };
        if (!allowed.Contains(fileName)) return false;

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        // Liveness (is a CLI actually running?) is checked by the endpoint via
        // TaskRunnerService.IsJobLive - the "3-progress" folder alone is not a
        // reliable signal because jobs stay there after stop / crash / restart.

        var filePath = Path.Combine(info.FolderPath, fileName);
        WriteAllTextWithRetry(filePath, content);
        // prompt.md does not affect kanban-card fields, but UpdateJobFile is
        // user-initiated (edit prompt) and the next read should see the
        // change for any consumer that pulls TaskDetail with the prompt body.
        return Updated();
    }

    /// <summary>
    /// Writes a text file tolerating transient Windows file-locks. The file
    /// can be briefly held by editors (VSCode), search indexers, AV scanners,
    /// or our own readers (status panel, log panel). A short retry loop with
    /// FileShare.ReadWrite avoids surfacing an HTTP 500 to the user for what
    /// is almost always a sub-second contention.
    /// </summary>
    private static void WriteAllTextWithRetry(string filePath, string content)
    {
        const int maxAttempts = 8;
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        IOException? last = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException ex) when (attempt < maxAttempts - 1)
            {
                last = ex;
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        if (last != null) throw last;
    }

    /// <summary>
    /// Saves a binary attachment (typically a pasted/dropped screenshot) into the job folder's
    /// <c>attachments/</c> subdirectory and returns the stored file name. Reused inside the prompt
    /// editor as a relative reference (<c>![alt](attachments/abc.png)</c>) so the CLI agent can
    /// resolve the same image directly from disk via the relative path in <c>prompt.md</c>.
    /// </summary>
    public (string? FileName, string? Error) SaveAttachment(string jobId, string? watchPath, byte[] content, string? originalFileName, string? contentType)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");
        if (content.Length == 0) return (null, "Empty file");
        if (content.Length > 10 * 1024 * 1024) return (null, "File too large (max 10 MB)");

        var ext = ResolveImageExtension(originalFileName, contentType);
        if (ext == null) return (null, "Unsupported file type - only png, jpg, gif, webp allowed");

        var attachmentsDir = Path.Combine(info.FolderPath, "attachments");
        Directory.CreateDirectory(attachmentsDir);

        // Short random ID keeps generated markdown readable; collisions are vanishingly rare
        // inside one job folder (~16M IDs at 4 bytes hex).
        string fileName;
        string fullPath;
        do
        {
            fileName = $"{Guid.NewGuid():N}"[..8] + ext;
            fullPath = Path.Combine(attachmentsDir, fileName);
        } while (File.Exists(fullPath));

        File.WriteAllBytes(fullPath, content);
        return (fileName, null);
    }

    private static string? ResolveImageExtension(string? originalFileName, string? contentType)
    {
        var ext = string.IsNullOrWhiteSpace(originalFileName)
            ? null
            : Path.GetExtension(originalFileName).ToLowerInvariant();

        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp") return ext == ".jpeg" ? ".jpg" : ext;

        return contentType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            _ => null
        };
    }

    /// <summary>
    /// Appends a "Continuous Session Nachtrag" block to <c>prompt.md</c> so the user's follow-up
    /// stays visible as part of the task description. <c>status.md</c> is intentionally not touched -
    /// it is owned by the post-run summary generator.
    /// </summary>
    /// <summary>
    /// Persist a user follow-up as a saved <see cref="PendingIntent"/> on the
    /// target job. Used by the busy-project queue path: when the user sends a
    /// follow-up to a job that is not the project's current active job, the
    /// intent is saved here, the job is later promoted to <c>2-ready</c>, and
    /// the auto-pickup loop consumes the saved intent on its next tick.
    /// Latest-wins: a second save overwrites the first.
    /// </summary>
    public PendingIntent? SavePendingIntent(
        string jobId,
        string mode,
        string prompt,
        string reason,
        string? activeJobId,
        string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return null;
        var intent = new PendingIntent
        {
            Mode = ContinueModes.Normalize(mode),
            Prompt = prompt ?? string.Empty,
            SavedAt = DateTime.UtcNow,
            SavedReason = string.IsNullOrWhiteSpace(reason) ? "project-busy" : reason,
            SavedAgainstActiveJobId = activeJobId
        };
        try
        {
            var path = Path.Combine(info.FolderPath, "pending-intent.json");
            File.WriteAllText(path,
                JsonSerializer.Serialize(intent, _pendingIntentWriteOpts),
                Encoding.UTF8);
            // PendingIntent appears on TaskInfo (kanban card shows the intent),
            // so the snapshot must be invalidated for the next read to see it.
            _scanner.InvalidateCache();
            return intent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save pending-intent.json for {JobId}", jobId);
            return null;
        }
    }

    private static readonly JsonSerializerOptions _pendingIntentWriteOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Read and consume a saved pending intent. Returns null when there is
    /// nothing to consume. The file is renamed to
    /// <c>pending-intent.consumed.json</c> first, then deleted on success;
    /// if the caller's run fails to spawn, the rollback rule is to rename it
    /// back so the next tick retries instead of losing the user's input.
    /// </summary>
    public PendingIntent? ReadAndStashPendingIntent(string jobFolder)
    {
        var path = Path.Combine(jobFolder, "pending-intent.json");
        if (!File.Exists(path)) return null;
        try
        {
            var raw = File.ReadAllText(path);
            var intent = JsonSerializer.Deserialize<PendingIntent>(raw, TaskJsonFile.ReadOpts);
            if (intent == null) return null;
            var stash = Path.Combine(jobFolder, "pending-intent.consumed.json");
            if (File.Exists(stash)) File.Delete(stash);
            File.Move(path, stash);
            // pending-intent.json gone → TaskInfo.PendingIntent should be null.
            _scanner.InvalidateCache();
            return intent;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read pending-intent.json at {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Finalize a successful pending-intent consumption: drop the stashed
    /// <c>pending-intent.consumed.json</c>. Call once the run is known to
    /// have spawned successfully.
    /// </summary>
    public void DiscardStashedPendingIntent(string jobFolder)
    {
        var stash = Path.Combine(jobFolder, "pending-intent.consumed.json");
        if (File.Exists(stash))
        {
            try { File.Delete(stash); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Stash}", stash); }
        }
    }

    /// <summary>
    /// Roll back a failed pending-intent consumption: move the stash back to
    /// <c>pending-intent.json</c> so the next pickup tries again. If the
    /// canonical file already exists (rare race), the stash is dropped to
    /// honor latest-wins.
    /// </summary>
    public void RollbackStashedPendingIntent(string jobFolder)
    {
        var stash = Path.Combine(jobFolder, "pending-intent.consumed.json");
        if (!File.Exists(stash)) return;
        var canonical = Path.Combine(jobFolder, "pending-intent.json");
        try
        {
            if (File.Exists(canonical))
            {
                File.Delete(stash);
            }
            else
            {
                File.Move(stash, canonical);
            }
            _scanner.InvalidateCache();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to roll back pending-intent at {Stash}", stash);
        }
    }

    public bool AppendContinuationNote(string jobId, string followupPrompt, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(followupPrompt)) return false;

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var block = $"\n\n---\n\n## Continuous Session Note - {timestamp}\n\n{followupPrompt.TrimEnd()}\n";

        AppendWithLeadingNewline(Path.Combine(info.FolderPath, "prompt.md"), block);
        return true;
    }

    private static void AppendWithLeadingNewline(string filePath, string block)
    {
        try
        {
            if (File.Exists(filePath))
            {
                var existing = File.ReadAllText(filePath);
                var separator = existing.EndsWith('\n') ? string.Empty : "\n";
                File.AppendAllText(filePath, separator + block);
            }
            else
            {
                File.WriteAllText(filePath, block.TrimStart('\n'));
            }
        }
        catch
        {
            // Best-effort append - failure to persist the addendum should not block the CLI resume.
        }
    }

    /// <summary>
    /// Boot-time migration: rewrites jobs that carry the misleading
    /// <c>agent: "human" + cliType: null + model: null</c> triple when
    /// the owner client has configured defaults. Idempotent (no-op on
    /// already-migrated jobs). Returns the number of jobs backfilled.
    /// </summary>
    public int BackfillAgentDefaults()
    {
        var count = 0;
        foreach (var job in _scanner.ScanAllJobs())
        {
            if (!string.Equals(job.Agent, AgentTypes.Human, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(job.CliType) || !string.IsNullOrWhiteSpace(job.Model))
                continue;

            var ownerId = job.OwnerClientId;
            if (string.IsNullOrWhiteSpace(ownerId)) continue;

            var owner = _clients.Find(ownerId);
            if (owner == null) continue;

            var cliType = owner.DefaultCliType is { } dc && CliTypes.IsValid(dc)
                ? CliTypes.Normalize(dc)
                : null;
            var model = owner.DefaultModel;
            var thinkingLevel = CliThinkingLevels.Normalize(cliType, model, owner.DefaultThinkingLevel);

            if (cliType == null && model == null && thinkingLevel == null) continue;

            if (cliType != null)
            {
                TaskJsonFile.UpdateField(job.FolderPath, "cliType", cliType, _logger);
                TaskJsonFile.UpdateField(job.FolderPath, "agent", cliType, _logger);
            }
            if (model != null)
                TaskJsonFile.UpdateField(job.FolderPath, "model", model, _logger);
            if (thinkingLevel != null)
                TaskJsonFile.UpdateField(job.FolderPath, "thinkingLevel", thinkingLevel, _logger);

            count++;
            _logger.LogInformation(
                "backfill-agent-defaults jobId={JobId} owner={Owner} agent={Agent} cliType={CliType} model={Model} thinkingLevel={ThinkingLevel}",
                job.Id, ownerId, cliType ?? job.Agent, cliType, model, thinkingLevel);
        }
        if (count > 0) _scanner.InvalidateCache();
        return count;
    }

    /// <summary>
    /// Mint a <c>SHC-NNN</c> task key for the project that owns
    /// <paramref name="watchPath"/>. Returns null when the project
    /// registry has no record for this path (non-fatal; the job will
    /// simply have <c>key: null</c>).
    ///
    /// <para>Before issuing, the per-project counter floor is re-derived
    /// from the keys actually present on disk. The in-memory
    /// <c>NextTaskKeySeq</c> is the fast path, but it can be rewound under
    /// the registry (e.g. a second backend sharing this workspace persists
    /// a stale snapshot, clobbering the live counter). Deriving the floor
    /// from disk on every mint makes the on-disk keys the source of truth,
    /// so a rewound counter can never re-issue a key that is already in
    /// use. This closed the bug where ASS-594/598 (and a contiguous band
    /// around them) were each minted onto two different tasks.</para>
    /// </summary>
    private string? MintTaskKey(string watchPath)
    {
        try
        {
            var project = _projectRegistry.FindByStorageLocation(watchPath);
            if (project == null) return null;

            var floor = HighestExistingKeyNumber(project.Id, project.ShortCode) + 1;
            _projectRegistry.EnsureTaskKeyFloor(project.Id, floor);

            var seq = _projectRegistry.IssueNextTaskKey(project.Id);
            return $"{project.ShortCode}-{seq}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "task-key-mint-failed watchPath={WatchPath}", watchPath);
            return null;
        }
    }

    /// <summary>
    /// Highest numeric tail among the keys currently on disk for the
    /// project identified by <paramref name="projectId"/> /
    /// <paramref name="shortCode"/>. Returns 0 when the project has no
    /// keyed tasks yet. Reads from the (cached) scanner snapshot.
    /// </summary>
    private int HighestExistingKeyNumber(string projectId, string shortCode)
    {
        var max = 0;
        foreach (var job in _scanner.ScanAllJobs())
        {
            if (string.IsNullOrWhiteSpace(job.Key)) continue;
            var owner = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (owner == null ||
                !string.Equals(owner.Id, projectId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (TaskKeyNumbers.TryParse(job.Key, shortCode, out var n) && n > max)
                max = n;
        }
        return max;
    }

    /// <summary>
    /// Boot-time backfill: stamps a <c>key</c> on every job whose
    /// <c>task.json</c> currently has no key. Idempotent; jobs that
    /// already carry a key are skipped. The project counter floor is first
    /// raised past the highest key already on disk (across all tasks, not
    /// only the ones being stamped) so a freshly stamped key cannot collide
    /// with an existing one even when the in-memory counter has drifted
    /// behind disk. Returns the number of jobs stamped.
    /// </summary>
    public int BackfillTaskKeys()
    {
        var stamped = 0;

        // Raise every keyed project's floor past its on-disk high-water mark
        // before issuing any new number. This repairs a counter that drifted
        // below disk (the same root cause as the duplicate-key bug) so the
        // stamps below land above existing keys.
        var byProject = new Dictionary<string, (string shortCode, int max)>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in _scanner.ScanAllJobs())
        {
            var project = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (project == null) continue;
            if (TaskKeyNumbers.TryParse(job.Key, project.ShortCode, out var n))
            {
                if (!byProject.TryGetValue(project.Id, out var cur) || n > cur.max)
                    byProject[project.Id] = (project.ShortCode, n);
            }
        }
        foreach (var (projectId, info) in byProject)
            _projectRegistry.EnsureTaskKeyFloor(projectId, info.max + 1);

        foreach (var job in _scanner.ScanAllJobs())
        {
            if (!string.IsNullOrWhiteSpace(job.Key)) continue;

            var project = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (project == null) continue;

            var seq = _projectRegistry.IssueNextTaskKey(project.Id);
            var key = $"{project.ShortCode}-{seq}";
            TaskJsonFile.UpdateField(job.FolderPath, "key", key, _logger);
            stamped++;
        }

        if (stamped > 0) _scanner.InvalidateCache();
        return stamped;
    }

    /// <summary>
    /// One-shot sweep that resolves duplicate display keys: two or more
    /// tasks in the same project carrying the identical <c>key</c>. The
    /// oldest task (earliest <c>createdAt</c>, id as tiebreak) keeps the
    /// contested key; every later namesake is re-keyed with a fresh number
    /// minted above the project's on-disk high-water mark. Only the
    /// <c>key</c> field changes - task ids, folders, and content are
    /// untouched. Idempotent: a second run with no collisions returns 0.
    /// Returns the number of tasks re-keyed.
    /// </summary>
    public int DeduplicateTaskKeys()
    {
        // Group keyed tasks by (projectId, key). A group with >1 member is a
        // collision.
        var groups = new Dictionary<(string ProjectId, string Key), List<TaskInfo>>();
        foreach (var job in _scanner.ScanAllJobs())
        {
            if (string.IsNullOrWhiteSpace(job.Key)) continue;
            var project = _projectRegistry.FindByStorageLocation(job.WatchPath);
            if (project == null) continue;
            var gk = (project.Id, job.Key!.Trim());
            if (!groups.TryGetValue(gk, out var list))
                groups[gk] = list = new List<TaskInfo>();
            list.Add(job);
        }

        var collisions = groups
            .Where(kvp => kvp.Value.Count > 1)
            .ToList();
        if (collisions.Count == 0) return 0;

        // Raise each affected project's counter above its on-disk maximum so
        // the replacement keys we mint cannot collide with an existing one.
        foreach (var projectId in collisions.Select(c => c.Key.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var project = _projectRegistry.FindById(projectId);
            if (project == null) continue;
            var floor = HighestExistingKeyNumber(projectId, project.ShortCode) + 1;
            _projectRegistry.EnsureTaskKeyFloor(projectId, floor);
        }

        var rekeyed = 0;
        foreach (var ((projectId, oldKey), members) in collisions)
        {
            var project = _projectRegistry.FindById(projectId);
            if (project == null) continue;

            // Deterministic keeper: the task that has held the key longest.
            var ordered = members
                .OrderBy(m => m.CreatedAt)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .ToList();
            var keeper = ordered[0];

            for (var i = 1; i < ordered.Count; i++)
            {
                var seq = _projectRegistry.IssueNextTaskKey(projectId);
                var newKey = $"{project.ShortCode}-{seq}";
                TaskJsonFile.UpdateField(ordered[i].FolderPath, "key", newKey, _logger);
                rekeyed++;
                _logger.LogInformation(
                    "task-key-dedup project={ProjectId} oldKey={OldKey} newKey={NewKey} jobId={JobId} keeper={KeeperId}",
                    projectId, oldKey, newKey, ordered[i].Id, keeper.Id);
            }
        }

        if (rekeyed > 0) _scanner.InvalidateCache();
        return rekeyed;
    }

    private static string ToSlug(string text)
    {
        // Transliterate German umlauts to ASCII equivalents
        var s = text
            .Replace("ä", "ae").Replace("Ä", "ae")
            .Replace("ö", "oe").Replace("Ö", "oe")
            .Replace("ü", "ue").Replace("Ü", "ue")
            .Replace("ß", "ss");
        // Decompose other accented characters and strip combining marks
        s = string.Concat(
            s.Normalize(NormalizationForm.FormD)
             .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark));
        s = s.ToLowerInvariant().Replace(' ', '-');
        return System.Text.RegularExpressions.Regex.Replace(s, @"[^a-z0-9\-]", "");
    }
}
