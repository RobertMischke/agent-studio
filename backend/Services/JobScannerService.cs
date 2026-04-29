using System.Globalization;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public class JobScannerService
{
    private readonly IConfiguration _config;
    private readonly ILogger<JobScannerService> _logger;
    private readonly SummaryGenerationService _summaryService;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

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
            var raw = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);

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
                UpdateJobJsonField(jobDir, "id", folderId);
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
                    ? JsonSerializer.Deserialize<SessionUsage>(lu.GetRawText(), JsonOpts)
                    : null,
                Model = raw.TryGetProperty("model", out var md) ? md.GetString() : null,
                CliType = raw.TryGetProperty("cliType", out var ct) ? ct.GetString() : null,
                UseOwnSession = raw.TryGetProperty("useOwnSession", out var uos) && uos.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? uos.GetBoolean()
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
            StatusMarkdown = statusMd,
            ContextUsage = ReadContextUsage(dir),
            Log = BuildLog(dir),
            SummaryState = ResolveSummaryState(info.JobKey, statusMd)
        };
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

    public MoveJobOutcome MoveJob(string jobId, string targetState, string? watchPath = null)
    {
        if (!JobStates.All.Contains(targetState))
            return new MoveJobOutcome(MoveJobStatus.Failure, $"Invalid state: {targetState}");

        var info = FindJob(jobId, watchPath);
        if (info == null) return new MoveJobOutcome(MoveJobStatus.NotFound);
        if (info.State == targetState) return new MoveJobOutcome(MoveJobStatus.Success);

        var jobFolderName = Path.GetFileName(info.FolderPath);
        var targetDir = Path.Combine(info.WatchPath, targetState, jobFolderName);

        // A pre-existing target folder almost always means a stale duplicate of the same
        // slug was left behind in another state — Directory.Move would throw a generic
        // IOException and the user would see a 404. Detect it up front and surface a
        // clear message so they know what to clean up.
        if (Directory.Exists(targetDir))
        {
            _logger.LogWarning(
                "Cannot move {JobId} to {State}: target folder already exists at {Target}",
                jobId, targetState, targetDir);
            return new MoveJobOutcome(
                MoveJobStatus.TargetFolderExists,
                $"A job folder named '{jobFolderName}' already exists in {targetState}. " +
                "This usually means a stale duplicate was left behind; remove or rename one of the folders and retry.");
        }

        try
        {
            Directory.Move(info.FolderPath, targetDir);
            UpdateStateInJobJson(targetDir, targetState);
            return new MoveJobOutcome(MoveJobStatus.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move job {JobId} to {State}", jobId, targetState);
            return new MoveJobOutcome(MoveJobStatus.Failure, ex.Message);
        }
    }

    public bool DeleteJob(string jobId, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;

        try
        {
            Directory.Delete(info.FolderPath, true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete job {JobId}", jobId);
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

    public bool SetJobSessionName(string jobId, string? sessionName, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "sessionName", sessionName ?? "");
        return true;
    }

    public bool UpdateLastUsage(string jobId, SessionUsage usage, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "lastUsage", usage);
        return true;
    }

    public bool SetJobModel(string jobId, string? model, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "model", model ?? "");
        return true;
    }

    public bool SetJobCliType(string jobId, string cliType, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        var normalized = CliTypes.Normalize(cliType);
        UpdateJobJsonField(info.FolderPath, "cliType", normalized);
        // Switching CLI invalidates the previous session — clear it so the next run mints a new one.
        if (!string.Equals(normalized, info.CliType, StringComparison.OrdinalIgnoreCase))
        {
            UpdateJobJsonField(info.FolderPath, "sessionName", "");
        }
        return true;
    }

    public bool SetJobUseOwnSession(string jobId, bool useOwn, string? watchPath = null)
    {
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "useOwnSession", useOwn);
        return true;
    }

    public bool SetJobTitle(string jobId, string title, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var info = FindJob(jobId, watchPath);
        if (info == null) return false;
        UpdateJobJsonField(info.FolderPath, "title", title.Trim());
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

        var targetState = req.TargetState switch
        {
            JobStates.Preparation => JobStates.Preparation,
            JobStates.Ready => JobStates.Ready,
            _ => JobStates.Preparation
        };

        // Sanitize ID: transliterate umlauts, lowercase, replace spaces with dashes, only allow safe chars
        var jobId = string.IsNullOrWhiteSpace(req.Id)
            ? ToSlug(req.Title)
            : req.Id;
        if (string.IsNullOrEmpty(jobId)) return null;

        var jobDir = Path.Combine(entry.Path, targetState, jobId);
        if (Directory.Exists(jobDir)) return null; // already exists

        Directory.CreateDirectory(jobDir);

        var jobJson = new Dictionary<string, object?>
        {
            ["id"] = jobId,
            ["title"] = req.Title,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["state"] = targetState,
            ["order"] = req.Order,
            ["agent"] = req.Agent
        };
        if (!string.IsNullOrWhiteSpace(req.Model))
            jobJson["model"] = req.Model;
        if (!string.IsNullOrWhiteSpace(req.CliType))
            jobJson["cliType"] = CliTypes.Normalize(req.CliType);

        File.WriteAllText(Path.Combine(jobDir, "job.json"),
            JsonSerializer.Serialize(jobJson, new JsonSerializerOptions { WriteIndented = true }));

        if (!string.IsNullOrWhiteSpace(req.PromptMarkdown))
            File.WriteAllText(Path.Combine(jobDir, "prompt.md"), req.PromptMarkdown);

        return jobId;
    }

    public bool UpdateJobFile(string jobId, string fileName, string content, string? watchPath = null)
    {
        var allowed = new[] { "prompt.md" };
        if (!allowed.Contains(fileName)) return false;

        var info = FindJob(jobId, watchPath);
        if (info == null) return false;

        // Liveness (is a CLI actually running?) is checked by the endpoint via
        // TaskRunnerService.IsJobLive — the "3-progress" folder alone is not a
        // reliable signal because jobs stay there after stop / crash / restart.

        var filePath = Path.Combine(info.FolderPath, fileName);
        WriteAllTextWithRetry(filePath, content);
        return true;
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
        var info = FindJob(jobId, watchPath);
        if (info == null) return (null, "Job not found");
        if (content.Length == 0) return (null, "Empty file");
        if (content.Length > 10 * 1024 * 1024) return (null, "File too large (max 10 MB)");

        var ext = ResolveImageExtension(originalFileName, contentType);
        if (ext == null) return (null, "Unsupported file type — only png, jpg, gif, webp allowed");

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

    public (string? Path, string? ContentType) ResolveAttachment(string jobId, string fileName, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            return (null, null);

        var info = FindJob(jobId, watchPath);
        if (info == null) return (null, null);

        var attachmentsDir = Path.Combine(info.FolderPath, "attachments");
        var fullPath = Path.Combine(attachmentsDir, fileName);
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
    /// stays visible as part of the task description. <c>status.md</c> is intentionally not touched —
    /// it is owned by the post-run summary generator.
    /// </summary>
    public bool AppendContinuationNote(string jobId, string followupPrompt, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(followupPrompt)) return false;

        var info = FindJob(jobId, watchPath);
        if (info == null) return false;

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var block = $"\n\n---\n\n## Continuous Session Nachtrag — {timestamp}\n\n{followupPrompt.TrimEnd()}\n";

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
            // Best-effort append — failure to persist the addendum should not block the CLI resume.
        }
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
