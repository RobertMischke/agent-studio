using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public class JobScannerService
{
    private readonly IConfiguration _config;
    private readonly ILogger<JobScannerService> _logger;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public JobScannerService(IConfiguration config, ILogger<JobScannerService> logger)
    {
        _config = config;
        _logger = logger;
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
            var raw = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);

            var lastActivity = GetLastActivityTime(jobDir);
            var totalSize = GetDirectorySize(jobDir);
            var resolvedId = raw.TryGetProperty("id", out var id) ? id.GetString() ?? Path.GetFileName(jobDir) : Path.GetFileName(jobDir);

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
                    ? JsonSerializer.Deserialize<SessionUsage>(lu.GetRawText(), JsonOpts)
                    : null
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
            _logger.LogWarning("Ambiguous job lookup for {JobId} without unique watch path context", jobId);
        }

        return null;
    }

    public JobDetail? GetJobDetail(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return null;

        var dir = info.FolderPath;
        return new JobDetail
        {
            Info = info,
            PromptMarkdown = ReadFileOrNull(Path.Combine(dir, "prompt.md")),
            StatusMarkdown = ReadFileOrNull(Path.Combine(dir, "status.md")),
            ContextUsage = ReadContextUsage(dir),
            Log = BuildLog(dir)
        };
    }

    public bool MoveJob(string jobId, string targetState, string? watchPath = null)
    {
        if (!JobStates.All.Contains(targetState)) return false;

        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        if (info.State == targetState) return true;

        var jobFolderName = Path.GetFileName(info.FolderPath);
        var targetDir = Path.Combine(info.WatchPath, targetState, jobFolderName);

        try
        {
            Directory.Move(info.FolderPath, targetDir);
            UpdateStateInJobJson(targetDir, targetState);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move job {JobId} to {State}", jobId, targetState);
            return false;
        }
    }

    public bool ChangeProject(string jobId, string targetWatchPath, string? watchPath = null)
    {
        var entries = GetWatchPaths();
        var targetEntry = entries.FirstOrDefault(e => e.Path == targetWatchPath);
        if (targetEntry == null) return false;

        var info = FindJob(jobId, watchPath);
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

    public void EnsureStateFoldersAndMigrate()
    {
        foreach (var entry in GetWatchPaths())
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
                    var raw = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
                    var oldState = raw.TryGetProperty("state", out var s) ? s.GetString() ?? "draft" : "draft";
                    var newState = JobStates.MapLegacyState(oldState);

                    var targetDir = Path.Combine(watchPath, newState, dirName);
                    Directory.Move(jobDir, targetDir);
                    UpdateStateInJobJson(targetDir, newState);
                    _logger.LogInformation("Migrated job {Job} from {Old} to {New}", dirName, oldState, newState);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to migrate job folder {Dir}", dirName);
                }
            }
        }
    }

    private void UpdateStateInJobJson(string jobDir, string newState)
    {
        UpdateJobJsonField(jobDir, "state", newState);
    }

    public bool SetJobSessionName(string jobId, string sessionName, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "sessionName", sessionName);
        return true;
    }

    public bool UpdateLastUsage(string jobId, SessionUsage usage, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "lastUsage", usage);
        return true;
    }

    public bool UpdateContextUsage(string jobId, ContextUsageSnapshot snapshot, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "contextUsage", snapshot);
        return true;
    }

    /// <summary>
    /// Reads <c>job.json</c>, replaces or adds a single top-level field, writes back preserving
    /// the existing field order.
    /// </summary>
    private void UpdateJobJsonField(string jobDir, string fieldName, object value)
    {
        var jobJsonPath = Path.Combine(jobDir, "job.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts)
                      ?? new Dictionary<string, JsonElement>();

            var updated = new Dictionary<string, object>();
            var inserted = false;
            foreach (var kv in doc)
            {
                if (kv.Key == fieldName)
                {
                    updated[fieldName] = value;
                    inserted = true;
                }
                else
                {
                    updated[kv.Key] = kv.Value;
                }
            }
            if (!inserted) updated[fieldName] = value;

            File.WriteAllText(jobJsonPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update field {Field} in job.json at {Dir}", fieldName, jobDir);
        }
    }

    private ContextUsageSnapshot? ReadContextUsage(string jobDir)
    {
        var jobJsonPath = Path.Combine(jobDir, "job.json");
        if (!File.Exists(jobJsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var raw = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);
            if (!raw.TryGetProperty("contextUsage", out var contextUsage) || contextUsage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return JsonSerializer.Deserialize<ContextUsageSnapshot>(contextUsage.GetRawText(), JsonOpts);
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

    public string? CreateJob(CreateJobRequest req)
    {
        var watchPaths = GetWatchPaths();
        var entry = string.IsNullOrEmpty(req.WatchPath)
            ? watchPaths.FirstOrDefault()
            : watchPaths.FirstOrDefault(w => w.Path == req.WatchPath);

        if (entry == null) return null;

        // Sanitize ID: lowercase, replace spaces with dashes, only allow safe chars
        var jobId = string.IsNullOrWhiteSpace(req.Id)
            ? req.Title.ToLowerInvariant().Replace(' ', '-')
            : req.Id;
        jobId = System.Text.RegularExpressions.Regex.Replace(jobId, @"[^a-z0-9\-]", "");
        if (string.IsNullOrEmpty(jobId)) return null;

        var jobDir = Path.Combine(entry.Path, JobStates.Preparation, jobId);
        if (Directory.Exists(jobDir)) return null; // already exists

        Directory.CreateDirectory(jobDir);

        var jobJson = new
        {
            id = jobId,
            title = req.Title,
            createdAt = DateTime.UtcNow.ToString("o"),
            state = JobStates.Preparation,
            order = req.Order,
            agent = req.Agent
        };
        File.WriteAllText(Path.Combine(jobDir, "job.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));

        if (!string.IsNullOrWhiteSpace(req.PromptMarkdown))
            File.WriteAllText(Path.Combine(jobDir, "prompt.md"), req.PromptMarkdown);

        File.WriteAllText(Path.Combine(jobDir, "status.md"),
            $"# Status\n\n- State: Preparation\n- Created: {DateTime.UtcNow:yyyy-MM-dd HH:mm}\n");

        return jobId;
    }

    public bool UpdateJobFile(string jobId, string fileName, string content, string? watchPath = null)
    {
        var allowed = new[] { "prompt.md", "status.md" };
        if (!allowed.Contains(fileName)) return false;

        var info = FindJob(jobId, watchPath);
        if (info == null) return false;

        // Don't allow editing while in progress
        if (info.State == JobStates.Progress) return false;

        var filePath = Path.Combine(info.FolderPath, fileName);
        File.WriteAllText(filePath, content);
        return true;
    }

    public bool ReorderJobs(List<JobOrderItem> jobs)
    {
        for (int i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            var info = FindJob(job.JobId, job.WatchPath);
            if (info == null) continue;
            UpdateOrderInJobJson(info.FolderPath, i + 1);
        }
        return true;
    }

    private void UpdateOrderInJobJson(string jobDir, int order)
    {
        var jobJsonPath = Path.Combine(jobDir, "job.json");
        if (!File.Exists(jobJsonPath)) return;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts)
                      ?? new Dictionary<string, JsonElement>();

            var updated = new Dictionary<string, object>();
            foreach (var kv in doc)
            {
                if (kv.Key == "order") updated["order"] = order;
                else if (kv.Key == "priority") continue; // drop legacy priority
                else updated[kv.Key] = kv.Value;
            }
            if (!updated.ContainsKey("order")) updated["order"] = order;

            File.WriteAllText(jobJsonPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update order in job.json at {Dir}", jobDir);
        }
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
