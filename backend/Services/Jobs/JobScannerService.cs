using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Discovery + read surface for jobs on disk. Resolves the configured
/// watch paths (with the <c>.orchestrator.yml</c> pointer flow), scans
/// the state subfolders into <see cref="JobInfo"/> records, hydrates
/// the per-job detail view (status / prompt / context-usage / log /
/// summary state), and serves read-only file lookups including the
/// <c>attachments/</c> and <c>results/</c> binary mirrors.
///
/// Writes against <c>job.json</c> live in the sibling services in this
/// folder: <see cref="JobStateMachine"/> for folder moves,
/// <see cref="JobMutationService"/> for field-level edits and
/// attachments, and <see cref="JobSessionLog"/> for session telemetry.
/// </summary>
public class JobScannerService
{
    private readonly IConfiguration _config;
    private readonly ILogger<JobScannerService> _logger;
    private readonly SummaryGenerationService _summaryService;

    /// <summary>
    /// Optional in-memory snapshot cache. Wired by DI through
    /// <see cref="SetIndexCache"/> after both services are constructed
    /// (avoids the constructor cycle: cache needs scanner for raw reads,
    /// scanner needs cache for hot-path reads).
    /// </summary>
    private JobIndexCache? _indexCache;

    public JobScannerService(IConfiguration config, ILogger<JobScannerService> logger, SummaryGenerationService summaryService)
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
    public void SetIndexCache(JobIndexCache cache) => _indexCache = cache;

    /// <summary>
    /// Invalidates the in-memory snapshot, if a cache is wired. Mutation
    /// services call this right after a folder move / job.json rewrite so
    /// the next read sees the change synchronously rather than waiting for
    /// the FileSystemWatcher's debounce window. No-op when no cache is
    /// registered (test fixtures that build the scanner directly).
    /// </summary>
    public void InvalidateCache() =>
        _indexCache?.Invalidate(JobIndexCache.InvalidationSource.Mutation);

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
    /// optimization: when a <see cref="JobIndexCache"/> is registered (the
    /// production case), this returns the cached snapshot in O(1); the
    /// cache refreshes itself on FileSystemWatcher events and on explicit
    /// invalidation from mutation services. Tests that build the scanner
    /// directly (no cache wired) keep the original disk-walk semantics.
    /// </summary>
    public List<JobInfo> ScanAllJobs()
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
    /// <see cref="JobIndexCache"/> for refresh and by callers that want to
    /// bypass the cache (tests, recovery paths).
    /// </summary>
    public List<JobInfo> ScanAllJobsRaw()
    {
        var jobs = new List<JobInfo>();
        foreach (var entry in GetWatchPaths())
        {
            if (!Directory.Exists(entry.Path))
            {
                _logger.LogWarning("Watch path does not exist: {Path}", entry.Path);
                continue;
            }

            foreach (var state in JobStates.All)
            {
                var stateDir = Path.Combine(entry.Path, state);
                if (!Directory.Exists(stateDir)) continue;

                foreach (var jobDir in Directory.GetDirectories(stateDir))
                {
                    var dirName = Path.GetFileName(jobDir);
                    if (dirName.StartsWith('_')) continue;

                    var job = ScanJobFolder(jobDir, entry, state);
                    if (job != null) jobs.Add(job);
                }
            }
        }
        return jobs;
    }

    public JobInfo? ScanJobFolder(string jobDir, WatchPathEntry entry, string state)
    {
        var jobJsonPath = Path.Combine(jobDir, "job.json");
        if (!File.Exists(jobJsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var raw = JsonSerializer.Deserialize<JsonElement>(json, JobJsonFile.ReadOpts);

            var lastActivity = GetLastActivityTime(jobDir);

            // The folder name is the canonical job id. Anything else (URL slugs,
            // log paths, MoveJob targets, the runner's job lookups) keys off the
            // folder name, so a divergent `id` field in job.json silently breaks
            // those paths. If we see one, surface a warning and self-heal the
            // file so the divergence does not survive the next scan.
            var folderId = Path.GetFileName(jobDir);
            if (raw.TryGetProperty("id", out var id)
                && id.GetString() is { Length: > 0 } jsonId
                && jsonId != folderId)
            {
                _logger.LogWarning(
                    "Job folder '{Dir}' has divergent id '{JsonId}' in job.json — rewriting to match folder name '{FolderId}'.",
                    jobDir, jsonId, folderId);
                JobJsonFile.UpdateField(jobDir, "id", folderId, _logger);
            }
            var resolvedId = folderId;

            var ownerClientId = ResolveOwnerClientId(raw, jobDir);
            var (commitChain, legacyCommit) = ReadCommitChain(raw);

            return new JobInfo
            {
                Id = resolvedId,
                JobKey = JobIdentity.CreateKey(entry.Path, resolvedId),
                Key = ReadReferenceKey(raw),
                OwnerClientId = ownerClientId,
                Title = raw.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                State = state,
                Order = raw.TryGetProperty("order", out var ord) && ord.TryGetInt32(out var orderVal) ? orderVal : 999,
                Agent = raw.TryGetProperty("agent", out var agent) ? agent.GetString() ?? "" : "",
                CreatedAt = raw.TryGetProperty("createdAt", out var created) && created.TryGetDateTime(out var dt) ? dt : File.GetCreationTime(jobJsonPath),
                WatchPath = entry.Path,
                ProjectName = entry.Name,
                FolderPath = jobDir,
                LastActivity = lastActivity,
                SessionName = raw.TryGetProperty("sessionName", out var sn) ? sn.GetString() : null,
                LastUsage = raw.TryGetProperty("lastUsage", out var lu) && lu.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<SessionUsage>(lu.GetRawText(), JobJsonFile.ReadOpts)
                    : null,
                Model = raw.TryGetProperty("model", out var md) ? md.GetString() : null,
                CliType = raw.TryGetProperty("cliType", out var ct) ? ct.GetString() : null,
                UseOwnSession = raw.TryGetProperty("useOwnSession", out var uos) && uos.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? uos.GetBoolean()
                    : null,
                Commit = legacyCommit,
                Commits = commitChain,
                CommitCount = ComputeCommitCountHint(raw, jobDir),
                SessionChain = ReadSessionChain(raw),
                PendingIntent = ReadPendingIntent(jobDir),
                OutcomeIssue = ResolveOutcomeIssue(jobDir),
                Fixture = raw.TryGetProperty("fixture", out var fix)
                    && fix.ValueKind is JsonValueKind.True,
                Phase = ReadPhase(raw, state, jobDir),
                TaskType = ReadTaskType(raw),
                Tags = ReadTags(raw)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse job.json in {Dir}", jobDir);
            return null;
        }
    }

    public JobInfo? FindJob(string jobId, string? watchPath = null)
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
                .OrderBy(j => Array.IndexOf(JobStates.All, j.State))
                .First();
        }

        return null;
    }

    public JobDetail? GetJobDetail(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var dir = info.FolderPath;
        var statusMd = ReadFileOrNull(Path.Combine(dir, "status.md"));
        return new JobDetail
        {
            Info = info,
            PromptMarkdown = ReadFileOrNull(Path.Combine(dir, "prompt.md")),
            PromptHistory = ReadPromptHistory(dir),
            TitleHistory = TitleHistoryLog.Read(dir),
            StatusMarkdown = statusMd,
            ContextUsage = ReadContextUsage(dir),
            Log = BuildLog(dir),
            SummaryState = ResolveSummaryState(info.JobKey, statusMd),
            ReviewEvidence = ReviewEvidenceLog.ReadLatestPerId(dir, _logger)
        };
    }

    /// <summary>
    /// Lower-bound count of commits attributed to a job, derived from
    /// session-events.jsonl SHA ranges plus the auto-commit on
    /// <c>job.json</c>. Drives the kanban "+N commits" hint without
    /// paying per-render git costs. The exact list is computed lazily
    /// behind <c>/api/jobs/{id}/commits</c>.
    ///
    /// Cheap by construction: skips the disk read entirely when the job
    /// has no auto-commit AND no logs/ directory, which covers the
    /// majority of jobs in <c>1-preparation</c> / <c>2-ready</c>.
    /// </summary>
    private static int ComputeCommitCountHint(JsonElement raw, string jobFolder)
    {
        var hasAutoCommit = raw.TryGetProperty("commit", out var commit)
            && commit.ValueKind == JsonValueKind.Object;
        var sessionLog = JobPaths.SessionEventsLog(jobFolder);
        var hasSessionLog = File.Exists(sessionLog);
        if (!hasAutoCommit && !hasSessionLog) return 0;

        var seenRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenShas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;

        if (hasSessionLog)
        {
            foreach (var line in File.ReadLines(sessionLog))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                SessionEvent? evt;
                try { evt = JsonSerializer.Deserialize<SessionEvent>(line, JobJsonFile.ReadOpts); }
                catch { continue; }
                if (evt == null) continue;
                if (string.IsNullOrWhiteSpace(evt.HeadShaBefore) || string.IsNullOrWhiteSpace(evt.HeadShaAfter)) continue;
                if (string.Equals(evt.HeadShaBefore, evt.HeadShaAfter, StringComparison.OrdinalIgnoreCase)) continue;
                var key = evt.HeadShaBefore + ".." + evt.HeadShaAfter;
                if (!seenRanges.Add(key)) continue;
                seenShas.Add(evt.HeadShaAfter!);
                count++;
            }
        }

        if (hasAutoCommit
            && commit.TryGetProperty("sha", out var shaProp)
            && shaProp.ValueKind == JsonValueKind.String
            && shaProp.GetString() is { Length: > 0 } sha
            && seenShas.Add(sha))
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Returns the job's <c>ownerClientId</c>, migrating legacy job.json
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
        JobJsonFile.UpdateField(jobDir, "ownerClientId", DefaultClientIdentity.Id, _logger);
        _logger.LogInformation("Migrated job folder '{Dir}' to ownerClientId='{Owner}'", jobDir, DefaultClientIdentity.Id);
        return DefaultClientIdentity.Id;
    }

    /// <summary>
    /// Reads the optional <c>phase</c> field from <c>job.json</c>. The wire
    /// field stays null when absent on disk; the frontend's lane projection
    /// then falls back to <see cref="LifecyclePhases.DefaultFor"/>. This is
    /// the compatibility contract from
    /// <c>docs/research/expanded-lifecycle-lanes-plan-2026-05.md</c>: existing
    /// job folders that predate the field continue to render in the default
    /// lane of their state without a one-shot migration that rewrites every
    /// <c>job.json</c>. Unknown phase strings, or phase strings that do not
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
    /// Reads the optional <c>taskType</c> field from <c>job.json</c>. Missing
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
    /// Reads the optional <c>tags</c> string array from <c>job.json</c>. Drops
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
            return JsonSerializer.Deserialize<PendingIntent>(raw, JobJsonFile.ReadOpts);
        }
        catch
        {
            return null;
        }
    }

    private const int OutcomeIssueTailBytes = 16 * 1024;

    private static JobOutcomeIssue? ResolveOutcomeIssue(string jobFolder)
    {
        var logPath = JobPaths.CliOutputLog(jobFolder);
        if (!File.Exists(logPath)) return null;

        var tail = ReadTailUtf8(logPath, OutcomeIssueTailBytes);
        if (string.IsNullOrWhiteSpace(tail)) return null;

        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (TryResolveOutcomeIssue(line, File.GetLastWriteTimeUtc(logPath), out var issue))
            {
                return issue;
            }
        }

        return null;
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

    private static bool TryResolveOutcomeIssue(string line, DateTime lastSeenAt, out JobOutcomeIssue? issue)
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

    private static JobOutcomeIssue BuildOutcomeIssue(string kind, string label, string severity, string rawLine, DateTime lastSeenAt)
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
    private static List<JobPromptHistoryEntry> ReadPromptHistory(string jobFolder)
    {
        var result = new List<JobPromptHistoryEntry>();
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
            result.Add(new JobPromptHistoryEntry
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
    private JobSummaryState ResolveSummaryState(string jobKey, string? statusMarkdown)
    {
        var live = _summaryService.GetState(jobKey);
        if (live != null) return live;
        return new JobSummaryState
        {
            Status = string.IsNullOrWhiteSpace(statusMarkdown) ? JobSummaryStatus.None : JobSummaryStatus.Ready,
            BytesWritten = statusMarkdown?.Length
        };
    }

    /// <summary>
    /// Reads <c>sessionChain</c> from job.json with a tolerant fallback: if the
    /// field is missing but a legacy <c>sessionName</c> exists, return a single-
    /// element chain. Anything else returns an empty list.
    /// </summary>
    /// <summary>
    /// Reads the task's commit chain from <c>job.json</c>. Returns a tuple
    /// <c>(chain, legacy)</c> where <c>chain</c> is the ordered list of
    /// commits this task has produced (oldest -&gt; newest) and <c>legacy</c>
    /// is the singular <see cref="JobInfo.Commit"/> value kept for
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
    private static (List<JobCommitInfo> chain, JobCommitInfo? legacy) ReadCommitChain(JsonElement raw)
    {
        var chain = new List<JobCommitInfo>();
        if (raw.TryGetProperty("commits", out var commitsEl) && commitsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in commitsEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var parsed = JsonSerializer.Deserialize<JobCommitInfo>(item.GetRawText(), JobJsonFile.ReadOpts);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Sha)) chain.Add(parsed);
            }
        }
        JobCommitInfo? legacy = null;
        if (raw.TryGetProperty("commit", out var commitEl) && commitEl.ValueKind == JsonValueKind.Object)
        {
            legacy = JsonSerializer.Deserialize<JobCommitInfo>(commitEl.GetRawText(), JobJsonFile.ReadOpts);
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
        var jobJsonPath = Path.Combine(jobDir, "job.json");
        if (!File.Exists(jobJsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var raw = JsonSerializer.Deserialize<JsonElement>(json, JobJsonFile.ReadOpts);
            if (!raw.TryGetProperty("contextUsage", out var contextUsage) || contextUsage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ContextUsageSnapshot>(contextUsage.GetRawText(), JobJsonFile.ReadOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read contextUsage from {JobDir}", jobDir);
            return null;
        }
    }

    public string? ReadJobFile(string jobId, string fileName, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var allowed = new[] { "prompt.md", "status.md", "job.json" };
        if (!allowed.Contains(fileName)) return null;

        return ReadFileOrNull(Path.Combine(info.FolderPath, fileName));
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

    private static List<JobLogEntry> BuildLog(string dir)
    {
        var entries = new List<JobLogEntry>();

        var jobJson = Path.Combine(dir, "job.json");
        if (File.Exists(jobJson))
        {
            entries.Add(new JobLogEntry
            {
                Timestamp = File.GetCreationTime(jobJson),
                Event = "Job created"
            });
        }

        var promptMd = Path.Combine(dir, "prompt.md");
        if (File.Exists(promptMd))
        {
            entries.Add(new JobLogEntry
            {
                Timestamp = File.GetLastWriteTime(promptMd),
                Event = "Prompt written"
            });
        }

        var statusMd = Path.Combine(dir, "status.md");
        if (File.Exists(statusMd))
        {
            entries.Add(new JobLogEntry
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
                entries.Add(new JobLogEntry
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
