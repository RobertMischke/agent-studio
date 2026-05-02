using System.Text.Json;
using OrchestratorApi.Models;

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

    public JobScannerService(IConfiguration config, ILogger<JobScannerService> logger, SummaryGenerationService summaryService)
    {
        _config = config;
        _logger = logger;
        _summaryService = summaryService;
    }

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

    public List<JobInfo> ScanAllJobs()
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
            var totalSize = GetDirectorySize(jobDir);

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

            return new JobInfo
            {
                Id = resolvedId,
                JobKey = JobIdentity.CreateKey(entry.Path, resolvedId),
                Title = raw.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                State = state,
                Order = raw.TryGetProperty("order", out var ord) && ord.TryGetInt32(out var orderVal) ? orderVal : 999,
                Agent = raw.TryGetProperty("agent", out var agent) ? agent.GetString() ?? "" : "",
                CreatedAt = raw.TryGetProperty("createdAt", out var created) && created.TryGetDateTime(out var dt) ? dt : File.GetCreationTime(jobJsonPath),
                WatchPath = entry.Path,
                ProjectName = entry.Name,
                FolderPath = jobDir,
                LastActivity = lastActivity,
                TotalSizeBytes = totalSize,
                SessionName = raw.TryGetProperty("sessionName", out var sn) ? sn.GetString() : null,
                LastUsage = raw.TryGetProperty("lastUsage", out var lu) && lu.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<SessionUsage>(lu.GetRawText(), JobJsonFile.ReadOpts)
                    : null,
                Model = raw.TryGetProperty("model", out var md) ? md.GetString() : null,
                CliType = raw.TryGetProperty("cliType", out var ct) ? ct.GetString() : null,
                UseOwnSession = raw.TryGetProperty("useOwnSession", out var uos) && uos.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? uos.GetBoolean()
                    : null,
                Commit = raw.TryGetProperty("commit", out var commit) && commit.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<JobCommitInfo>(commit.GetRawText(), JobJsonFile.ReadOpts)
                    : null,
                SessionChain = ReadSessionChain(raw)
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
            StatusMarkdown = statusMd,
            ContextUsage = ReadContextUsage(dir),
            Log = BuildLog(dir),
            SummaryState = ResolveSummaryState(info.JobKey, statusMd)
        };
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

    private static long GetDirectorySize(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }
}
