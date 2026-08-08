using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Docs;

/// <summary>One retained item in a wiki page's agent-read history.</summary>
public sealed record WikiAgentReadRecent(DateTime At, string TaskKey);

/// <summary>Observed agent-read totals projected onto a wiki page.</summary>
public sealed record WikiAgentReads(int Total, DateTime? LastReadAt, IReadOnlyList<WikiAgentReadRecent> Recent);

/// <summary>One atomic write into the runtime-only agent-read state area.</summary>
public sealed record WikiAgentReadWriteResult(bool Changed, string StatePath);

/// <summary>All runtime-only agent-read records for one project and their cache signature.</summary>
public sealed record WikiAgentReadIndex(
    IReadOnlyDictionary<string, WikiAgentReads> ByDocsRelativePath,
    string Signature);

/// <summary>
/// Persists observational wiki-read telemetry outside product repositories.
/// Each page owns one bounded JSON record under
/// <c>&lt;TaskRepository&gt;/.metadata/wiki-agent-reads/&lt;project-id&gt;/</c>.
/// Adjacent tracked companions are read only as a legacy migration baseline.
/// </summary>
public sealed class WikiAgentReadStore
{
    public const int MaxRecentReads = 20;

    private const string StateSchema = "wiki-agent-read-state/v1";
    private const string StateFolderName = "wiki-agent-reads";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> WriteGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly string? _taskRepositoryRoot;

    public WikiAgentReadStore(IConfiguration configuration)
    {
        var configured = configuration["TaskRepository"];
        _taskRepositoryRoot = string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "workspace"))
            : Path.GetFullPath(configured);
    }

    private WikiAgentReadStore()
    {
        _taskRepositoryRoot = null;
    }

    /// <summary>
    /// Empty read-only store for direct unit fixtures that do not provide the
    /// application's configured TaskRepository.
    /// </summary>
    internal static WikiAgentReadStore Unconfigured { get; } = new();

    public WikiAgentReadWriteResult Increment(
        string projectKey,
        string wikiDir,
        string docsRelPath,
        DateTime atUtc,
        string taskKey)
    {
        var rel = NormalizeDocsRelativePath(docsRelPath)
            ?? throw new ArgumentException("A safe docs-relative page path is required.", nameof(docsRelPath));
        var statePath = StatePathFor(projectKey, rel);
        lock (GateFor(statePath))
        {
            var current = ReadCurrent(statePath, wikiDir, rel) ?? EmptyReads;
            var recent = current.Recent.ToList();
            recent.Add(new WikiAgentReadRecent(NormalizeAt(atUtc), NormalizeTaskKey(taskKey)));
            var updated = Normalize(checked(current.Total + 1), current.LastReadAt, recent);
            return WriteAtomically(statePath, rel, updated);
        }
    }

    /// <summary>
    /// Applies the historical-log baseline monotonically. The first write for
    /// a page imports any legacy companion value before comparing totals.
    /// </summary>
    public WikiAgentReadWriteResult ApplyBackfill(
        string projectKey,
        string wikiDir,
        string docsRelPath,
        int total,
        IReadOnlyCollection<WikiAgentReadRecent> recent)
    {
        var rel = NormalizeDocsRelativePath(docsRelPath)
            ?? throw new ArgumentException("A safe docs-relative page path is required.", nameof(docsRelPath));
        var statePath = StatePathFor(projectKey, rel);
        lock (GateFor(statePath))
        {
            var current = ReadCurrent(statePath, wikiDir, rel) ?? EmptyReads;
            var reconstructedTotal = Math.Max(0, total);
            var history = reconstructedTotal >= current.Total ? recent : current.Recent;
            var updated = Normalize(
                Math.Max(current.Total, reconstructedTotal),
                current.LastReadAt,
                history);
            return WriteAtomically(statePath, rel, updated);
        }
    }

    public WikiAgentReadIndex ReadAll(string projectKey)
    {
        if (_taskRepositoryRoot == null)
            return new WikiAgentReadIndex(
                new Dictionary<string, WikiAgentReads>(StringComparer.OrdinalIgnoreCase),
                "unconfigured");

        var stateDir = StateDirectoryFor(projectKey);
        var index = new Dictionary<string, WikiAgentReads>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(stateDir)) return new WikiAgentReadIndex(index, "empty");

        var signatureInput = new StringBuilder();
        foreach (var path in Directory.EnumerateFiles(stateDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var json = File.ReadAllText(path);
                var rel = Path.GetRelativePath(stateDir, path).Replace('\\', '/');
                signatureInput.Append(rel).Append('\u001f').Append(json).Append('\n');
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!string.Equals(JsonString(root, "schemaVersion"), StateSchema, StringComparison.Ordinal)) continue;
                var sourceRel = NormalizeDocsRelativePath(JsonString(root, "sourcePath"));
                var reads = ParseAgentReads(root);
                if (sourceRel == null || reads == null) continue;
                index[sourceRel] = reads;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                SilentCatch.Note(ex, "WikiAgentReadStore: unreadable runtime state record ignored.");
            }
        }

        var signature = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(signatureInput.ToString()))).ToLowerInvariant();
        return new WikiAgentReadIndex(index, signature);
    }

    internal string StatePathFor(string projectKey, string docsRelPath)
    {
        if (_taskRepositoryRoot == null)
            throw new InvalidOperationException("TaskRepository is required for agent-read persistence.");
        var rel = NormalizeDocsRelativePath(docsRelPath)
            ?? throw new ArgumentException("A safe docs-relative page path is required.", nameof(docsRelPath));
        var stateDir = StateDirectoryFor(projectKey);
        var full = Path.GetFullPath(Path.Combine(
            stateDir,
            rel.Replace('/', Path.DirectorySeparatorChar) + ".json"));
        var rootWithSep = stateDir.EndsWith(Path.DirectorySeparatorChar)
            ? stateDir
            : stateDir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, PathComparison))
            throw new ArgumentException("The page path escapes the agent-read state directory.", nameof(docsRelPath));
        return full;
    }

    internal static WikiAgentReads? ParseAgentReads(JsonElement reads)
    {
        if (reads.ValueKind != JsonValueKind.Object) return null;
        var total = JsonInt(reads, "total") ?? 0;
        DateTime? lastReadAt = DateTime.TryParse(JsonString(reads, "lastReadAt"), out var last)
            ? last.ToUniversalTime()
            : null;
        var recent = new List<WikiAgentReadRecent>();
        if (reads.TryGetProperty("recent", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!DateTime.TryParse(JsonString(item, "at"), out var at)) continue;
                var taskKey = JsonString(item, "taskKey");
                if (string.IsNullOrWhiteSpace(taskKey)) continue;
                recent.Add(new WikiAgentReadRecent(at.ToUniversalTime(), NormalizeTaskKey(taskKey)));
            }
        }
        return Normalize(Math.Max(0, total), lastReadAt, recent);
    }

    private string StateDirectoryFor(string projectKey) =>
        Path.GetFullPath(Path.Combine(
            _taskRepositoryRoot!,
            ".metadata",
            StateFolderName,
            ProjectDirectoryName(projectKey)));

    private static string ProjectDirectoryName(string projectKey)
    {
        var normalized = string.IsNullOrWhiteSpace(projectKey)
            ? "UNKNOWN"
            : projectKey.Trim().ToUpperInvariant();
        if (normalized.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            return normalized;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        return "project-" + hash[..16];
    }

    private WikiAgentReads? ReadCurrent(string statePath, string wikiDir, string docsRelPath)
    {
        var state = ReadStateFile(statePath);
        return state ?? ReadLegacyCompanion(wikiDir, docsRelPath);
    }

    private static WikiAgentReads? ReadStateFile(string statePath)
    {
        if (!File.Exists(statePath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
            return string.Equals(JsonString(doc.RootElement, "schemaVersion"), StateSchema, StringComparison.Ordinal)
                ? ParseAgentReads(doc.RootElement)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "WikiAgentReadStore: unreadable runtime state; legacy baseline will be used.");
            return null;
        }
    }

    private static WikiAgentReads? ReadLegacyCompanion(string wikiDir, string docsRelPath)
    {
        var rel = NormalizeDocsRelativePath(docsRelPath);
        if (rel == null) return null;
        var companion = Path.Combine(
            Path.GetFullPath(wikiDir),
            rel.Replace('/', Path.DirectorySeparatorChar) + ".meta.json");
        if (!File.Exists(companion)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(companion));
            return doc.RootElement.TryGetProperty("agentReads", out var reads)
                ? ParseAgentReads(reads)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "WikiAgentReadStore: unreadable legacy companion baseline ignored.");
            return null;
        }
    }

    private static WikiAgentReadWriteResult WriteAtomically(
        string statePath,
        string docsRelPath,
        WikiAgentReads reads)
    {
        var payload = new
        {
            schemaVersion = StateSchema,
            sourcePath = "docs/" + docsRelPath.Replace('\\', '/'),
            total = reads.Total,
            lastReadAt = reads.LastReadAt,
            recent = reads.Recent.Select(item => new { at = item.At, taskKey = item.TaskKey }).ToList(),
        };
        var serialized = JsonSerializer.Serialize(payload, WriteOptions) + "\n";
        var changed = !File.Exists(statePath)
            || !string.Equals(File.ReadAllText(statePath), serialized, StringComparison.Ordinal);
        if (!changed) return new WikiAgentReadWriteResult(false, statePath);

        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temp = statePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, serialized, new UTF8Encoding(false));
            File.Move(temp, statePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { SilentCatch.Note(ex, "WikiAgentReadStore: temporary state cleanup failed."); }
        }
        return new WikiAgentReadWriteResult(true, statePath);
    }

    private static WikiAgentReads Normalize(
        int total,
        DateTime? lastReadAt,
        IEnumerable<WikiAgentReadRecent> recent)
    {
        var bounded = recent
            .Select(item => new WikiAgentReadRecent(NormalizeAt(item.At), NormalizeTaskKey(item.TaskKey)))
            .OrderByDescending(item => item.At)
            .Take(MaxRecentReads)
            .ToList();
        var last = bounded.Count > 0 ? bounded[0].At : lastReadAt?.ToUniversalTime();
        return new WikiAgentReads(Math.Max(0, total), last, bounded);
    }

    private static string? NormalizeDocsRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var rel = value.Trim().Replace('\\', '/');
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        if (rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) rel = rel[5..];
        if (rel.Length == 0 || rel.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        if (rel.Split('/').Any(segment => segment is "" or "." or "..")) return null;
        return rel;
    }

    private static object GateFor(string path) =>
        WriteGates.GetOrAdd(Path.GetFullPath(path), _ => new object());

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? JsonInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : null;

    private static DateTime NormalizeAt(DateTime at) =>
        at == default ? DateTime.UtcNow : at.ToUniversalTime();

    private static string NormalizeTaskKey(string taskKey) =>
        string.IsNullOrWhiteSpace(taskKey) ? "unknown" : taskKey.Trim();

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static WikiAgentReads EmptyReads { get; } = new(0, null, []);
}
