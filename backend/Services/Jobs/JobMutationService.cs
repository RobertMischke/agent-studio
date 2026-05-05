using System.Globalization;
using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Field-level writes against an existing job: the per-field setters
/// (model, cli-type, title, useOwnSession, commit, contextUsage),
/// editing of <c>prompt.md</c>, the create-job flow that mints a new
/// job folder, the binary attachment uploader, and the
/// continuation-note appender that records user follow-ups into
/// <c>prompt.md</c>.
///
/// Pattern: every public method does <see cref="JobScannerService.FindJob"/>
/// → <see cref="JobJsonFile.UpdateField"/>. Splitting this out of the
/// scanner keeps the read surface focused on read and makes the write
/// surface easy to grep when a "where do we touch this field" question
/// comes up.
/// </summary>
public class JobMutationService
{
    private readonly JobScannerService _scanner;
    private readonly ILogger<JobMutationService> _logger;

    public JobMutationService(JobScannerService scanner, ILogger<JobMutationService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    public bool SetJobModel(string jobId, string? model, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        JobJsonFile.UpdateField(info.FolderPath, "model", model ?? "", _logger);
        return true;
    }

    public bool SetJobCliType(string jobId, string cliType, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var normalized = CliTypes.Normalize(cliType);
        JobJsonFile.UpdateField(info.FolderPath, "cliType", normalized, _logger);
        // Switching CLI invalidates the previous session - clear it so the next run mints a new one.
        if (!string.Equals(normalized, info.CliType, StringComparison.OrdinalIgnoreCase))
        {
            JobJsonFile.UpdateField(info.FolderPath, "sessionName", "", _logger);
        }
        return true;
    }

    public bool SetJobUseOwnSession(string jobId, bool useOwn, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        JobJsonFile.UpdateField(info.FolderPath, "useOwnSession", useOwn, _logger);
        return true;
    }

    public bool SetJobCommit(string jobId, JobCommitInfo commit, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        JobJsonFile.UpdateField(info.FolderPath, "commit", commit, _logger);
        return true;
    }

    public bool SetJobCommitOnFolder(string folderPath, JobCommitInfo commit)
    {
        if (!Directory.Exists(folderPath)) return false;
        JobJsonFile.UpdateField(folderPath, "commit", commit, _logger);
        return true;
    }

    /// <summary>
    /// Stamp a UTC progress heartbeat onto the job's <c>job.json</c>. Written
    /// on every CLI-output flush so <see cref="OrchestratorApi.Services.Runner.CrashRecoveryService"/>
    /// can attribute orphan working-tree changes to the most-recently-active
    /// job in <c>3-progress</c> on the next backend boot. ADR-0020.
    /// </summary>
    public bool SetJobLastProgressAt(string folderPath, DateTime utcNow)
    {
        if (!Directory.Exists(folderPath)) return false;
        JobJsonFile.UpdateField(folderPath, "lastProgressAt",
            utcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture), _logger);
        return true;
    }

    public bool SetJobTitle(string jobId, string title, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        JobJsonFile.UpdateField(info.FolderPath, "title", title.Trim(), _logger);
        return true;
    }

    public bool UpdateContextUsage(string jobId, ContextUsageSnapshot snapshot, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        JobJsonFile.UpdateField(info.FolderPath, "contextUsage", snapshot, _logger);
        return true;
    }

    public string? CreateJob(CreateJobRequest req)
    {
        var watchPaths = _scanner.GetWatchPaths();
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

        var jobJson = new Dictionary<string, object?>
        {
            ["id"] = jobId,
            ["title"] = req.Title,
            ["createdAt"] = DateTime.UtcNow.ToString("o"),
            ["state"] = targetState,
            ["order"] = resolvedOrder,
            ["agent"] = req.Agent,
            ["ownerClientId"] = ownerClientId
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

        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;

        // Liveness (is a CLI actually running?) is checked by the endpoint via
        // TaskRunnerService.IsJobLive - the "3-progress" folder alone is not a
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
            var intent = JsonSerializer.Deserialize<PendingIntent>(raw, JobJsonFile.ReadOpts);
            if (intent == null) return null;
            var stash = Path.Combine(jobFolder, "pending-intent.consumed.json");
            if (File.Exists(stash)) File.Delete(stash);
            File.Move(path, stash);
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
