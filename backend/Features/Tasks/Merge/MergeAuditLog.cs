using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Tasks.Merge;

/// <summary>
/// Append-only JSONL audit log for every merge / undo operation. Lives at
/// <c>&lt;TaskRepository&gt;/.audit/merges.jsonl</c> when the configured
/// <c>TaskRepository</c> is set; falls back to the first watch path's
/// parent folder otherwise (workspace-root-equivalent for legacy setups).
///
/// <para>
/// The audit log is the only durable record that authorises a merge undo:
/// the operator (or the FE toast) supplies a <c>restoreToken</c> minted at
/// merge time, the log is the lookup that resolves the token to the
/// archived folder path. Once the undo window closes, the record stays
/// in the log for forensics but no longer authorises a restore.
/// </para>
///
/// <para>
/// Like <see cref="TimelineLog"/> this writer is best-effort: a write
/// failure is logged and the merge call still completes (the in-memory
/// changes already happened), but the operator loses the undo path.
/// </para>
/// </summary>
public sealed class MergeAuditLog
{
    public const string AuditFolderName = ".audit";
    public const string MergeLogFileName = "merges.jsonl";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly ILogger<MergeAuditLog> _logger;
    private readonly object _gate = new();

    public MergeAuditLog(IConfiguration config, TaskScannerService scanner, ILogger<MergeAuditLog> logger)
    {
        _config = config;
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>
    /// Returns the workspace root used to anchor the audit log + archive
    /// folder. Prefers <c>TaskRepository</c> from config; falls back to the
    /// parent of the first configured watch path.
    /// </summary>
    public string? GetWorkspaceRoot()
    {
        var taskRepo = _config["TaskRepository"];
        if (!string.IsNullOrWhiteSpace(taskRepo)) return Path.GetFullPath(taskRepo);

        var first = _scanner.GetWatchPaths().FirstOrDefault();
        if (first == null || string.IsNullOrWhiteSpace(first.Path)) return null;
        // Watch paths look like <root>/projects/<projectKey>; the workspace
        // root is the grandparent. If the layout differs, fall back to the
        // direct parent so the audit log lands somewhere predictable.
        var parent = Path.GetDirectoryName(first.Path);
        var grand = parent != null ? Path.GetDirectoryName(parent) : null;
        return string.IsNullOrWhiteSpace(grand) ? parent : grand;
    }

    public string? GetAuditDir()
    {
        var root = GetWorkspaceRoot();
        return root == null ? null : Path.Combine(root, AuditFolderName);
    }

    public string? GetArchiveMergedDir()
    {
        var root = GetWorkspaceRoot();
        return root == null ? null : Path.Combine(root, ".archive", "merged");
    }

    private string? GetLogFilePath()
    {
        var dir = GetAuditDir();
        return dir == null ? null : Path.Combine(dir, MergeLogFileName);
    }

    /// <summary>
    /// Append one record. Best-effort - returns false on missing root or
    /// IO failure so the caller can surface the gap without crashing the
    /// merge operation.
    /// </summary>
    public bool Append(MergeAuditRecord record)
    {
        var path = GetLogFilePath();
        if (path == null) return false;
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var line = JsonSerializer.Serialize(record, WriteOpts) + Environment.NewLine;
                File.AppendAllText(path, line, Encoding.UTF8);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MergeAuditLog: failed to append record for primary={Primary} secondary={Secondary}",
                record.PrimaryId, record.SecondaryId);
            return false;
        }
    }

    public List<MergeAuditRecord> ReadAll()
    {
        var path = GetLogFilePath();
        if (path == null || !File.Exists(path)) return [];
        var result = new List<MergeAuditRecord>();
        try
        {
            lock (_gate)
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var rec = JsonSerializer.Deserialize<MergeAuditRecord>(line, ReadOpts);
                        if (rec != null) result.Add(rec);
                    }
                    catch (Exception __ex)
                    {
                        SilentCatch.Note(__ex, "MergeAuditLog: Best-effort: skip torn / malformed lines.");
                        // Best-effort: skip torn / malformed lines.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MergeAuditLog: failed to read {Path}", path);
        }
        return result;
    }

    /// <summary>
    /// Resolve a restore token to its (latest, not-yet-undone) record.
    /// Returns null when the token is unknown, already used, or the
    /// 24h undo window has elapsed.
    /// </summary>
    public MergeAuditRecord? FindRestorable(string restoreToken, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(restoreToken)) return null;
        var all = ReadAll();
        var match = all.LastOrDefault(r =>
            string.Equals(r.RestoreToken, restoreToken, StringComparison.Ordinal) &&
            r.UndoneAt == null);
        if (match == null) return null;
        var ageDays = (utcNow - match.At).TotalDays;
        return ageDays <= MergeModes.UndoGraceDays ? match : null;
    }

    /// <summary>
    /// Rewrite the log appending a new record that marks the original as
    /// undone. We never edit existing rows in place - JSONL is append-only -
    /// so an undo writes a fresh record with the same RestoreToken,
    /// <see cref="MergeAuditRecord.UndoneAt"/> set, and the rest of the
    /// fields copied so the chain stays self-describing.
    /// </summary>
    public bool AppendUndo(MergeAuditRecord original, DateTime utcNow, string undoneBy)
    {
        var undone = original with
        {
            At = utcNow,
            UndoneAt = utcNow,
            UndoneBy = string.IsNullOrWhiteSpace(undoneBy) ? "unknown" : undoneBy,
        };
        return Append(undone);
    }
}
