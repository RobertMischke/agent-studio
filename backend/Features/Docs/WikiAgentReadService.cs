using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Cli;
using AgentStudio.Registry;

namespace AgentStudio.Docs;

/// <summary>One retained item in a wiki companion's agent-read history.</summary>
public sealed record WikiAgentReadRecent(DateTime At, string TaskKey);

/// <summary>Observed agent-read totals projected from a wiki companion.</summary>
public sealed record WikiAgentReads(int Total, DateTime? LastReadAt, IReadOnlyList<WikiAgentReadRecent> Recent);

/// <summary>Outcome of the startup-only historical CLI-log initialization.</summary>
public sealed record WikiAgentReadBackfillResult(bool AlreadyCompleted, int LogsScanned, int ReadsApplied, string MarkerPath);

/// <summary>
/// Extracts docs-page reads from rendered activity markers and older raw JSON
/// frames. It intentionally recognizes reads only: edits and writes never count.
/// </summary>
public static partial class WikiAgentReadLogParser
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".html", ".htm", ".json" };

    public static IReadOnlyList<string> ExtractDocsRelativePaths(string? text)
    {
        var value = AnsiText.Strip(text ?? string.Empty).Trim();
        if (value.Length == 0) return [];

        var candidates = new List<string>();
        if (value[0] == '{') ExtractJsonCandidates(value, candidates);

        var marker = ReadMarkerRegex().Match(value);
        if (marker.Success)
        {
            candidates.Add(marker.Groups["argument"].Value);
        }
        else
        {
            var run = RunMarkerRegex().Match(value);
            if (run.Success) candidates.AddRange(ExtractPathTokens(run.Groups["command"].Value));
        }

        // Old logs and shell commands can contain several docs paths in one
        // tool event. Scan the complete line as a fallback, then distinct the
        // normalized page paths so one tool-use counts a page at most once.
        candidates.AddRange(ExtractPathTokens(value));
        return candidates
            .Select(NormalizeDocsRelativePath)
            .Where(path => path != null)
            .Select(path => path!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string? NormalizeDocsRelativePath(string candidate)
    {
        var path = candidate.Trim().Trim('"', '\'', '`').Replace('\\', '/');
        if (path.Length == 0) return null;

        var marker = path.LastIndexOf("/docs/", StringComparison.OrdinalIgnoreCase);
        string rel;
        if (marker >= 0)
            rel = path[(marker + "/docs/".Length)..];
        else if (path.StartsWith("docs/", StringComparison.OrdinalIgnoreCase))
            rel = path["docs/".Length..];
        else
            return null;

        var extension = PageExtensionRegex().Match(rel);
        if (!extension.Success) return null;
        rel = rel[..extension.Index] + extension.Value;
        rel = rel.TrimStart('/');
        if (rel.Length == 0 || rel.Contains("..", StringComparison.Ordinal)) return null;
        if (rel.Equals("app", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith("app/", StringComparison.OrdinalIgnoreCase)) return null;
        if (rel.Split('/').Any(segment => segment.StartsWith('.'))) return null;
        if (rel.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".report.html", StringComparison.OrdinalIgnoreCase)
            || rel.EndsWith(".report.htm", StringComparison.OrdinalIgnoreCase)) return null;
        return SupportedExtensions.Contains(Path.GetExtension(rel)) ? rel : null;
    }

    private static IEnumerable<string> ExtractPathTokens(string text)
    {
        foreach (Match match in DocsPathRegex().Matches(text))
            yield return match.Groups["path"].Value;
    }

    private static void ExtractJsonCandidates(string json, List<string> candidates)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            Visit(doc.RootElement, candidates);
        }
        catch (JsonException)
        {
            // A torn/raw line contributes through the textual path scan only.
        }
    }

    private static void Visit(JsonElement element, List<string> candidates)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) Visit(item, candidates);
            return;
        }
        if (element.ValueKind != JsonValueKind.Object) return;

        var tool = JsonString(element, "name") ?? JsonString(element, "tool_name")
            ?? JsonString(element, "tool") ?? JsonString(element, "type");
        var argumentObject = JsonObject(element, "input") ?? JsonObject(element, "parameters")
            ?? JsonObject(element, "args") ?? element;

        if (tool is not null && IsReadTool(tool))
        {
            AddJsonString(argumentObject, candidates, "file_path", "absolute_path", "path");
        }
        else if (tool is not null && IsShellTool(tool))
        {
            AddJsonString(argumentObject, candidates, "command", "cmd");
        }

        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Visit(property.Value, candidates);
    }

    private static bool IsReadTool(string tool) => tool.Equals("Read", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("ReadFile", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("read_file", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("read-file", StringComparison.OrdinalIgnoreCase);

    private static bool IsShellTool(string tool) => tool.Equals("Bash", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("command_call", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("command_execution", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("local_shell_call", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("run_shell_command", StringComparison.OrdinalIgnoreCase)
        || tool.Equals("shell", StringComparison.OrdinalIgnoreCase);

    private static JsonElement? JsonObject(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? JsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void AddJsonString(JsonElement element, List<string> candidates, params string[] names)
    {
        foreach (var name in names)
            if (JsonString(element, name) is { Length: > 0 } value)
                candidates.Add(value);
    }

    [GeneratedRegex(@"^[●*•]\s*Read\s+(?<argument>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReadMarkerRegex();

    [GeneratedRegex(@"^[●*•]\s*Run\s+(?<command>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex RunMarkerRegex();

    [GeneratedRegex("(?<path>(?:[A-Za-z]:)?[^\\s\\\"'`;|<>]*?(?:^|[/\\\\])docs[/\\\\][^\\s\\\"'`;|<>]+\\.(?:md|html?|json))", RegexOptions.IgnoreCase)]
    private static partial Regex DocsPathRegex();

    [GeneratedRegex(@"\.(?:md|html?|json)", RegexOptions.IgnoreCase)]
    private static partial Regex PageExtensionRegex();
}

/// <summary>
/// Persists observed wiki reads into adjacent companions. Live local CLI output
/// and remote runner ingestion call the same method; startup backfill folds the
/// durable cli-output.log inventory once behind an atomic marker.
/// </summary>
public sealed class WikiAgentReadService
{
    private const string MarkerSchema = "wiki-agent-read-backfill/v1";
    private const string MarkerFileName = "wiki-agent-reads-backfill-v1.json";

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly WikiCompanionStore _companions;
    private readonly ProjectDocsService _docs;
    private readonly IConfiguration _configuration;
    private readonly ILogger<WikiAgentReadService> _logger;
    private readonly object _backfillGate = new();
    private readonly ConcurrentDictionary<string, TaskTarget?> _targetCache = new(StringComparer.OrdinalIgnoreCase);

    public WikiAgentReadService(
        TaskScannerService scanner,
        ProjectRegistry registry,
        WikiCompanionStore companions,
        ProjectDocsService docs,
        IConfiguration configuration,
        ILogger<WikiAgentReadService> logger)
    {
        _scanner = scanner;
        _registry = registry;
        _companions = companions;
        _docs = docs;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Processes one or more already-rendered output lines.</summary>
    public int ProcessOutput(string taskKey, IEnumerable<CliOutputLine> lines)
    {
        var observations = lines
            .SelectMany(line => WikiAgentReadLogParser.ExtractDocsRelativePaths(line.Text)
                .Select(rel => new ReadObservation(rel, NormalizeAt(line.Timestamp))))
            .ToList();
        if (observations.Count == 0) return 0;

        var target = _targetCache.GetOrAdd(taskKey, ResolveTaskTarget);
        if (target == null) return 0;

        var applied = 0;
        foreach (var group in observations.GroupBy(o => $"{o.RelPath}|{o.At:O}", StringComparer.OrdinalIgnoreCase))
        {
            var observation = group.First();
            if (!TryReadPage(target.WikiDir, observation.RelPath, out var fullPath, out var title, out var content)) continue;
            try
            {
                _companions.IncrementAgentRead(
                    target.WikiDir, observation.RelPath, title, content, observation.At, target.TaskKey);
                applied++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "wiki-agent-read-write-failed task={TaskKey} page={Page}", target.TaskKey, observation.RelPath);
            }
        }
        if (applied > 0) _docs.InvalidateWikiTreeCache();
        return applied;
    }

    /// <summary>
    /// Scans every current/archived task log once. A durable marker makes later
    /// startups and repeated calls no-ops; sidecar baseline writes themselves
    /// are monotonic so a crash-resumed scan cannot inflate totals.
    /// </summary>
    public WikiAgentReadBackfillResult EnsureBackfilled()
    {
        lock (_backfillGate)
        {
            var markerPath = MarkerPath();
            if (File.Exists(markerPath)) return new WikiAgentReadBackfillResult(true, 0, 0, markerPath);

            var logsScanned = 0;
            var byPage = new Dictionary<string, BackfillPage>(StringComparer.OrdinalIgnoreCase);
            foreach (var task in _scanner.ScanAllJobs()
                         .Where(task => !string.IsNullOrWhiteSpace(task.FolderPath))
                         .GroupBy(task => Path.GetFullPath(task.FolderPath), StringComparer.OrdinalIgnoreCase)
                         .Select(group => group.First()))
            {
                var logPath = TaskPaths.CliOutputLog(task.FolderPath);
                if (!File.Exists(logPath)) continue;
                logsScanned++;
                var target = ResolveTaskTarget(task);
                if (target == null) continue;

                List<CliOutputLine> lines;
                try { lines = CliOutputLogParser.ParseFile(logPath); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "wiki-agent-read-backfill-log-failed task={TaskKey} log={Log}", target.TaskKey, logPath);
                    continue;
                }

                foreach (var line in lines)
                {
                    var at = NormalizeAt(line.Timestamp);
                    foreach (var rel in WikiAgentReadLogParser.ExtractDocsRelativePaths(line.Text))
                    {
                        if (!TryReadPage(target.WikiDir, rel, out _, out var title, out var content)) continue;
                        var key = target.ProjectName + "|" + rel;
                        if (!byPage.TryGetValue(key, out var page))
                        {
                            page = new BackfillPage(target.WikiDir, rel, title, content);
                            byPage[key] = page;
                        }
                        page.Total++;
                        page.Recent.Add(new WikiAgentReadRecent(at, target.TaskKey));
                    }
                }
            }

            var readsApplied = 0;
            foreach (var page in byPage.Values)
            {
                _companions.ApplyAgentReadBackfill(
                    page.WikiDir, page.RelPath, page.Title, page.Content, page.Total, page.Recent);
                readsApplied += page.Total;
            }

            WriteMarker(markerPath, logsScanned, readsApplied);
            if (readsApplied > 0) _docs.InvalidateWikiTreeCache();
            _logger.LogInformation(
                "wiki-agent-read-backfill-complete logsScanned={LogsScanned} pages={Pages} readsApplied={ReadsApplied} marker={Marker}",
                logsScanned, byPage.Count, readsApplied, markerPath);
            return new WikiAgentReadBackfillResult(false, logsScanned, readsApplied, markerPath);
        }
    }

    private TaskTarget? ResolveTaskTarget(string taskKey)
    {
        var task = _scanner.FindJob(taskKey);
        return task == null ? null : ResolveTaskTarget(task);
    }

    private TaskTarget? ResolveTaskTarget(TaskInfo task)
    {
        var repo = ProjectRepoResolver.ResolveForProject(task.ProjectName, _scanner, _registry);
        if (string.IsNullOrWhiteSpace(repo)) return null;
        var wikiDir = Path.Combine(repo, ProjectDocsService.WikiRel);
        if (!Directory.Exists(wikiDir)) return null;
        var key = !string.IsNullOrWhiteSpace(task.TaskKey) ? task.TaskKey
            : !string.IsNullOrWhiteSpace(task.Key) ? task.Key! : task.Id;
        return new TaskTarget(task.ProjectName, wikiDir, key);
    }

    private static bool TryReadPage(
        string wikiDir, string relPath, out string fullPath, out string title, out string content)
    {
        fullPath = Path.GetFullPath(Path.Combine(wikiDir, relPath.Replace('/', Path.DirectorySeparatorChar)));
        title = Path.GetFileNameWithoutExtension(relPath);
        content = string.Empty;
        var root = Path.GetFullPath(wikiDir);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath)) return false;
        try
        {
            content = File.ReadAllText(fullPath);
            title = ReadTitle(content, relPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ReadTitle(string content, string relPath)
    {
        foreach (var line in content.Split('\n').Take(80))
            if (line.StartsWith("# ", StringComparison.Ordinal)) return line[2..].Trim();
        return Path.GetFileNameWithoutExtension(relPath);
    }

    private string MarkerPath()
    {
        var root = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(root))
            root = _scanner.GetWatchPaths().Select(entry => entry.Path).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        root = string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root;
        return Path.Combine(Path.GetFullPath(root), ".metadata", MarkerFileName);
    }

    private static void WriteMarker(string markerPath, int logsScanned, int readsApplied)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = MarkerSchema,
            completedAt = DateTime.UtcNow,
            logsScanned,
            readsApplied,
        }, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var temp = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, markerPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static DateTime NormalizeAt(DateTime at) =>
        at == default ? DateTime.UtcNow : at.ToUniversalTime();

    private sealed record TaskTarget(string ProjectName, string WikiDir, string TaskKey);
    private sealed record ReadObservation(string RelPath, DateTime At);
    private sealed class BackfillPage(string wikiDir, string relPath, string title, string content)
    {
        public string WikiDir { get; } = wikiDir;
        public string RelPath { get; } = relPath;
        public string Title { get; } = title;
        public string Content { get; } = content;
        public int Total { get; set; }
        public List<WikiAgentReadRecent> Recent { get; } = [];
    }
}
