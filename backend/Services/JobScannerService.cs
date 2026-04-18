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

    public List<string> GetWatchPaths() =>
        _config.GetSection("WatchPaths").Get<List<string>>() ?? [];

    public List<JobInfo> ScanAllJobs()
    {
        var jobs = new List<JobInfo>();
        foreach (var watchPath in GetWatchPaths())
        {
            if (!Directory.Exists(watchPath))
            {
                _logger.LogWarning("Watch path does not exist: {Path}", watchPath);
                continue;
            }

            foreach (var state in JobStates.All)
            {
                var stateDir = Path.Combine(watchPath, state);
                if (!Directory.Exists(stateDir)) continue;

                foreach (var jobDir in Directory.GetDirectories(stateDir))
                {
                    var dirName = Path.GetFileName(jobDir);
                    if (dirName.StartsWith('_')) continue;

                    var job = ScanJobFolder(jobDir, watchPath, state);
                    if (job != null) jobs.Add(job);
                }
            }
        }
        return jobs;
    }

    public JobInfo? ScanJobFolder(string jobDir, string watchPath, string state)
    {
        var jobJsonPath = Path.Combine(jobDir, "job.json");
        if (!File.Exists(jobJsonPath)) return null;

        try
        {
            var json = File.ReadAllText(jobJsonPath);
            var raw = JsonSerializer.Deserialize<JsonElement>(json, JsonOpts);

            var lastActivity = GetLastActivityTime(jobDir);
            var totalSize = GetDirectorySize(jobDir);

            return new JobInfo
            {
                Id = raw.TryGetProperty("id", out var id) ? id.GetString() ?? Path.GetFileName(jobDir) : Path.GetFileName(jobDir),
                Title = raw.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                State = state,
                Priority = raw.TryGetProperty("priority", out var prio) ? prio.GetString() ?? "normal" : "normal",
                Agent = raw.TryGetProperty("agent", out var agent) ? agent.GetString() ?? "" : "",
                CreatedAt = raw.TryGetProperty("createdAt", out var created) && created.TryGetDateTime(out var dt) ? dt : File.GetCreationTime(jobJsonPath),
                WatchPath = watchPath,
                FolderPath = jobDir,
                LastActivity = lastActivity,
                TotalSizeBytes = totalSize
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse job.json in {Dir}", jobDir);
            return null;
        }
    }

    public JobDetail? GetJobDetail(string jobId)
    {
        var info = ScanAllJobs().FirstOrDefault(j => j.Id == jobId);
        if (info == null) return null;

        var dir = info.FolderPath;
        return new JobDetail
        {
            Info = info,
            PromptMarkdown = ReadFileOrNull(Path.Combine(dir, "prompt.md")),
            StatusMarkdown = ReadFileOrNull(Path.Combine(dir, "status.md")),
            ReviewMarkdown = ReadFileOrNull(Path.Combine(dir, "review.md")),
            Metrics = ReadMetrics(Path.Combine(dir, "metrics.json")),
            Artifacts = ListFiles(Path.Combine(dir, "artifacts")),
            Screenshots = ListFiles(Path.Combine(dir, "screenshots")),
            Logs = ListFiles(Path.Combine(dir, "logs")),
            Timeline = BuildTimeline(dir)
        };
    }

    public bool MoveJob(string jobId, string targetState)
    {
        if (!JobStates.All.Contains(targetState)) return false;

        var info = ScanAllJobs().FirstOrDefault(j => j.Id == jobId);
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

    public void EnsureStateFoldersAndMigrate()
    {
        foreach (var watchPath in GetWatchPaths())
        {
            if (!Directory.Exists(watchPath))
            {
                Directory.CreateDirectory(watchPath);
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
                if (kv.Key == "state") updated["state"] = newState;
                else updated[kv.Key] = kv.Value;
            }
            if (!updated.ContainsKey("state")) updated["state"] = newState;

            File.WriteAllText(jobJsonPath, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update state in job.json at {Dir}", jobDir);
        }
    }

    public string? ReadJobFile(string jobId, string fileName)
    {
        var info = ScanAllJobs().FirstOrDefault(j => j.Id == jobId);
        if (info == null) return null;

        var allowed = new[] { "prompt.md", "status.md", "review.md", "metrics.json", "job.json" };
        if (!allowed.Contains(fileName)) return null;

        return ReadFileOrNull(Path.Combine(info.FolderPath, fileName));
    }

    private static string? ReadFileOrNull(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : null;

    private static JobMetrics? ReadMetrics(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<JobMetrics>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }

    private static List<string> ListFiles(string dir)
    {
        if (!Directory.Exists(dir)) return [];
        return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();
    }

    private static List<JobTimelineEntry> BuildTimeline(string dir)
    {
        var entries = new List<JobTimelineEntry>();

        // job.json creation = job created
        var jobJson = Path.Combine(dir, "job.json");
        if (File.Exists(jobJson))
        {
            entries.Add(new JobTimelineEntry
            {
                Timestamp = File.GetCreationTime(jobJson),
                Event = "Job created"
            });
        }

        // status.md last modified
        var statusMd = Path.Combine(dir, "status.md");
        if (File.Exists(statusMd))
        {
            entries.Add(new JobTimelineEntry
            {
                Timestamp = File.GetLastWriteTime(statusMd),
                Event = "Status updated"
            });
        }

        // artifacts
        var artifactsDir = Path.Combine(dir, "artifacts");
        if (Directory.Exists(artifactsDir))
        {
            foreach (var f in Directory.GetFiles(artifactsDir, "*", SearchOption.AllDirectories).Take(20))
            {
                entries.Add(new JobTimelineEntry
                {
                    Timestamp = File.GetLastWriteTime(f),
                    Event = "Artifact produced",
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
