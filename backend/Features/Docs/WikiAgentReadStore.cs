using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentStudio.Docs;

/// <summary>One retained item in a wiki page's agent-read history.</summary>
public sealed record WikiAgentReadRecent(DateTime At, string TaskKey);

/// <summary>Observed agent-read totals projected onto a wiki page.</summary>
public sealed record WikiAgentReads(int Total, DateTime? LastReadAt, IReadOnlyList<WikiAgentReadRecent> Recent);

/// <summary>Outcome of writing one page's runtime-only agent-read state.</summary>
public sealed record WikiAgentReadWriteResult(bool Changed, string StateAbsPath);

/// <summary>
/// Persists observational wiki-read telemetry outside the tracked docs tree.
/// Each page has one atomic runtime sidecar below
/// <c>&lt;repo&gt;/.orchestrator/wiki-agent-reads/</c>. Adjacent
/// <c>*.meta.json</c> companions remain a read-only legacy source until their
/// next content-metadata write migrates the block.
/// </summary>
public sealed class WikiAgentReadStore
{
    public const int MaxRecent = 20;

    private const string StateSchema = "wiki-agent-reads/v1";
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> WriteGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Runtime state path for one docs-relative page.</summary>
    public static string StatePathFor(string wikiDir, string docsRelPath)
    {
        var wikiRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(wikiDir));
        var repoRoot = Path.GetDirectoryName(wikiRoot)
            ?? throw new ArgumentException("The wiki directory must have a repository parent.", nameof(wikiDir));
        var stateRoot = Path.GetFullPath(Path.Combine(repoRoot, ".orchestrator", "wiki-agent-reads"));
        var rel = NormalizeRelativePath(docsRelPath);
        var path = Path.GetFullPath(Path.Combine(
            stateRoot,
            rel.Replace('/', Path.DirectorySeparatorChar) + ".json"));
        var rootWithSeparator = stateRoot.EndsWith(Path.DirectorySeparatorChar)
            ? stateRoot
            : stateRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The docs-relative path leaves the wiki root.", nameof(docsRelPath));
        return path;
    }

    /// <summary>
    /// Reads runtime state first and falls back to a legacy adjacent companion.
    /// </summary>
    public WikiAgentReads? Read(string wikiDir, string docsRelPath, string? legacyCompanionPath = null)
    {
        var statePath = StatePathFor(wikiDir, docsRelPath);
        var runtime = ReadState(statePath);
        if (runtime != null) return runtime;
        return ReadLegacyCompanion(legacyCompanionPath ?? LegacyCompanionPath(wikiDir, docsRelPath));
    }

    /// <summary>
    /// Reads runtime state first and uses an already parsed legacy block as the
    /// fallback. This avoids reading every tracked companion twice during a
    /// full wiki projection.
    /// </summary>
    public WikiAgentReads? Read(string wikiDir, string docsRelPath, JsonElement legacyReads)
    {
        var runtime = ReadState(StatePathFor(wikiDir, docsRelPath));
        return runtime ?? Parse(legacyReads);
    }

    /// <summary>Enumerates runtime state for the tree projection.</summary>
    public IReadOnlyDictionary<string, WikiAgentReads> ReadAll(string wikiDir)
    {
        var stateRoot = Path.GetDirectoryName(StatePathFor(wikiDir, "placeholder"))!;
        var result = new Dictionary<string, WikiAgentReads>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(stateRoot)) return result;

        foreach (var path in Directory.EnumerateFiles(stateRoot, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                var sourcePath = JsonString(root, "sourcePath");
                if (string.IsNullOrWhiteSpace(sourcePath)
                    || !sourcePath.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
                    continue;
                var rel = NormalizeRelativePath(sourcePath["docs/".Length..]);
                var reads = Parse(root);
                if (reads != null) result[rel] = reads;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                SilentCatch.Note(ex, "WikiAgentReadStore: unreadable runtime sidecar ignored during enumeration.");
            }
        }
        return result;
    }

    /// <summary>Adds one observed read without changing the tracked docs tree.</summary>
    public WikiAgentReadWriteResult Increment(
        string wikiDir,
        string docsRelPath,
        DateTime atUtc,
        string taskKey)
    {
        var statePath = StatePathFor(wikiDir, docsRelPath);
        lock (GateFor(statePath))
        {
            var stored = ReadState(statePath)
                ?? ReadLegacyCompanion(LegacyCompanionPath(wikiDir, docsRelPath))
                ?? new WikiAgentReads(0, null, []);
            var at = atUtc.ToUniversalTime();
            var recent = stored.Recent.Append(new WikiAgentReadRecent(at, NormalizeTaskKey(taskKey)));
            var next = Normalize(checked(stored.Total + 1), Max(stored.LastReadAt, at), recent);
            return WriteStateAtomically(statePath, docsRelPath, next);
        }
    }

    /// <summary>
    /// Applies the historical-log baseline monotonically so a repeated or
    /// crash-resumed backfill cannot add the same reads twice.
    /// </summary>
    public WikiAgentReadWriteResult ApplyBackfill(
        string wikiDir,
        string docsRelPath,
        int total,
        IReadOnlyCollection<WikiAgentReadRecent> recent)
    {
        var statePath = StatePathFor(wikiDir, docsRelPath);
        lock (GateFor(statePath))
        {
            var stored = ReadState(statePath)
                ?? ReadLegacyCompanion(LegacyCompanionPath(wikiDir, docsRelPath))
                ?? new WikiAgentReads(0, null, []);
            var reconstructedTotal = Math.Max(0, total);
            // An equal or larger reconstructed inventory replaces history from
            // that source. A larger stored total means live evidence has already
            // extended the baseline, so its retained history wins.
            var history = reconstructedTotal >= stored.Total ? recent : stored.Recent;
            var lastReadAt = history.Count > 0
                ? history.Max(item => item.At).ToUniversalTime()
                : stored.LastReadAt;
            var next = Normalize(Math.Max(stored.Total, reconstructedTotal), lastReadAt, history);
            return WriteStateAtomically(statePath, docsRelPath, next);
        }
    }

    /// <summary>
    /// Moves a legacy <c>agentReads</c> block out of a companion that is already
    /// being changed for content metadata. Telemetry-only writes never call this
    /// method, so an agent read cannot dirty a tracked file.
    /// </summary>
    public bool MoveLegacyFromCompanion(string wikiDir, string docsRelPath, JsonObject companionRoot)
    {
        if (companionRoot["agentReads"] is not JsonObject legacyNode) return false;
        var legacy = Parse(legacyNode);
        if (legacy == null) return false;

        var statePath = StatePathFor(wikiDir, docsRelPath);
        lock (GateFor(statePath))
        {
            if (ReadState(statePath) == null)
                WriteStateAtomically(statePath, docsRelPath, legacy);
            companionRoot.Remove("agentReads");
            return true;
        }
    }

    private static object GateFor(string path) =>
        WriteGates.GetOrAdd(Path.GetFullPath(path), _ => new object());

    private static WikiAgentReadWriteResult WriteStateAtomically(
        string statePath,
        string docsRelPath,
        WikiAgentReads reads)
    {
        var bounded = Normalize(reads.Total, reads.LastReadAt, reads.Recent);
        var recent = new JsonArray();
        foreach (var item in bounded.Recent)
        {
            recent.Add(new JsonObject
            {
                ["at"] = item.At.ToUniversalTime().ToString("o"),
                ["taskKey"] = NormalizeTaskKey(item.TaskKey),
            });
        }
        var root = new JsonObject
        {
            ["schemaVersion"] = StateSchema,
            ["sourcePath"] = "docs/" + NormalizeRelativePath(docsRelPath),
            ["total"] = bounded.Total,
            ["lastReadAt"] = bounded.LastReadAt?.ToUniversalTime().ToString("o"),
            ["recent"] = recent,
        };
        var serialized = root.ToJsonString(WriteOptions) + "\n";
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
            catch (Exception ex) { SilentCatch.Note(ex, "WikiAgentReadStore: temporary sidecar cleanup failed."); }
        }
        return new WikiAgentReadWriteResult(true, statePath);
    }

    private static WikiAgentReads? ReadState(string statePath)
    {
        if (!File.Exists(statePath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            return Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "WikiAgentReadStore: unreadable runtime sidecar; falling back to legacy evidence.");
            return null;
        }
    }

    private static WikiAgentReads? ReadLegacyCompanion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("agentReads", out var reads)
                ? Parse(reads)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            SilentCatch.Note(ex, "WikiAgentReadStore: unreadable legacy companion ignored.");
            return null;
        }
    }

    private static WikiAgentReads? Parse(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            using var document = JsonDocument.Parse(node.ToJsonString());
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static WikiAgentReads? Parse(JsonElement reads)
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

    private static WikiAgentReads Normalize(
        int total,
        DateTime? lastReadAt,
        IEnumerable<WikiAgentReadRecent> recent)
    {
        var bounded = recent
            .OrderByDescending(item => item.At)
            .Take(MaxRecent)
            .Select(item => item with
            {
                At = item.At.ToUniversalTime(),
                TaskKey = NormalizeTaskKey(item.TaskKey),
            })
            .ToList();
        DateTime? newest = bounded.Count == 0 ? null : bounded[0].At;
        return new WikiAgentReads(Math.Max(0, total), Max(lastReadAt, newest), bounded);
    }

    private static string LegacyCompanionPath(string wikiDir, string docsRelPath) =>
        Path.Combine(
            Path.GetFullPath(wikiDir),
            NormalizeRelativePath(docsRelPath).Replace('/', Path.DirectorySeparatorChar) + ".meta.json");

    private static string NormalizeRelativePath(string value)
    {
        var candidate = (value ?? string.Empty).Replace('\\', '/').Trim();
        if (candidate.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(candidate))
            throw new ArgumentException("A safe docs-relative page path is required.", nameof(value));
        var rel = candidate.TrimEnd('/');
        if (rel.Length == 0
            || rel.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("A safe docs-relative page path is required.", nameof(value));
        return rel;
    }

    private static DateTime? Max(DateTime? left, DateTime? right)
    {
        if (left == null) return right?.ToUniversalTime();
        if (right == null) return left.Value.ToUniversalTime();
        return left.Value.ToUniversalTime() >= right.Value.ToUniversalTime()
            ? left.Value.ToUniversalTime()
            : right.Value.ToUniversalTime();
    }

    private static string NormalizeTaskKey(string taskKey) =>
        string.IsNullOrWhiteSpace(taskKey) ? "unknown" : taskKey.Trim();

    private static string? JsonString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? JsonInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }
}
