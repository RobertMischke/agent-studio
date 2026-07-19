using System.Text;
using System.Text.Json;

namespace AgentStudio.Docs;

/// <summary>
/// Read-only repository discovery for experiment Workbenches. Canonical items
/// are folders that carry a <c>workbench.json</c> descriptor and live anywhere
/// under docs/ (each Workbench sits with its own theme, e.g.
/// docs/operations/&lt;id&gt;/ or docs/quality/&lt;id&gt;/); the recursive scan
/// skips dot-directories and node_modules-like folders. The small legacy list
/// is an explicit migration bridge for named, already-existing artifacts, never
/// a heuristic scan of arbitrary HTML.
/// </summary>
public sealed class WorkbenchCatalogueService
{
    private const long MaxHtmlBytes = 20L * 1024 * 1024;

    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly GitService _git;

    private static readonly HashSet<string> CurrentStatuses = new(StringComparer.Ordinal)
        { "active", "decision-pending" };
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
        { "active", "decision-pending", "decided", "archived" };
    private static readonly HashSet<string> AllowedPhases = new(StringComparer.Ordinal)
        { "shaping", "testing", "decision-ready" };

    private sealed record LegacyWorkbench(
        string Id, string Title, string Summary, string RepoRelPath, string Phase,
        string[] SourceTaskKeys);

    private static readonly LegacyWorkbench[] LegacyPilot =
    [
        new("pipeline-workbench", "Pipeline workbench",
            "Inspect the pipeline contract and its current implementation signals.",
            "docs/system/domains/pipeline.md.report.html", "testing", ["AGT-2091"]),
        new("workbench-mockup-family", "Workbench mockup family",
            "Shape the Workbench host, list, viewer, and later decision surfaces.",
            "docs/concepts/mockups/experimentier-workbench.html", "testing", ["AGT-2122"]),
        new("app-survey", "Application survey",
            "Understand the current product surfaces through the visual survey findings.",
            "docs/quality/design/app-survey-2026-07-11.html", "decision-ready", []),
        new("decoupled-lifecycles", "Decoupled lifecycles",
            "Understand and separate task, run, pipeline, and delivery lifecycles.",
            "docs/concepts/mockups/decoupled-lifecycles.html", "shaping", ["AGT-2091", "AGT-2122"]),
    ];

    public WorkbenchCatalogueService(TaskScannerService scanner, ProjectRegistry registry, GitService git)
    {
        _scanner = scanner;
        _registry = registry;
        _git = git;
    }

    public WorkbenchCatalogue? List(string projectName, bool includeHistory = false)
    {
        var root = ResolveRoot(projectName);
        if (root == null) return null;

        var items = DiscoverCanonical(root);
        var ids = items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var legacy in LegacyPilot)
        {
            if (ids.Contains(legacy.Id)) continue;
            var full = ContainedPath(root, legacy.RepoRelPath);
            if (full == null || !File.Exists(full)) continue;
            if (!IsHtmlWithinLimit(full))
            {
                items.Add(new WorkbenchListItem(
                    legacy.Id, legacy.Title, legacy.Summary, "invalid", legacy.Phase,
                    File.GetLastWriteTimeUtc(full), legacy.RepoRelPath, false,
                    $"HTML exceeds the {MaxHtmlBytes / (1024 * 1024)} MiB Workbench limit.",
                    legacy.SourceTaskKeys));
                continue;
            }
            items.Add(new WorkbenchListItem(
                legacy.Id, legacy.Title, legacy.Summary, "active", legacy.Phase,
                File.GetLastWriteTimeUtc(full), legacy.RepoRelPath, true, null,
                legacy.SourceTaskKeys));
        }

        var visible = items
            .Where(x => !x.Valid || includeHistory || CurrentStatuses.Contains(x.Status))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new WorkbenchCatalogue(projectName, includeHistory, visible.Count, visible);
    }

    public WorkbenchDocument? Read(string projectName, string id)
    {
        if (!SafeId(id)) return null;
        var root = ResolveRoot(projectName);
        if (root == null) return null;
        var item = List(projectName, includeHistory: true)?.Items.FirstOrDefault(x => x.Id == id);
        if (item is not { Valid: true }) return null;
        var full = ContainedPath(root, item.EntryPath);
        if (full == null || !File.Exists(full)) return null;
        var html = ReadHtmlWithinLimit(full);
        if (html == null) return null;
        var status = _git.GetStatusForRepoRoot(root);
        var provenancePaths = new List<string> { item.EntryPath };
        var entryDir = Path.GetDirectoryName(item.EntryPath.Replace('\\', '/'))?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(entryDir))
            provenancePaths.Add(entryDir + "/workbench.json");
        var workingTreeModified = status.IsRepo && status.Files.Any(change =>
            provenancePaths.Any(path => ChangeTouchesPath(change.Path, path)));
        var revision = status.IsRepo && status.Error == null && !workingTreeModified
            ? _git.GetHeadShaCached(root)
            : null;
        return new WorkbenchDocument(item, html, status.Branch, revision, workingTreeModified);
    }

    private List<WorkbenchListItem> DiscoverCanonical(string root)
    {
        var result = new List<WorkbenchListItem>();
        var docsRoot = ContainedPath(root, "docs");
        if (docsRoot == null || !Directory.Exists(docsRoot)) return result;
        foreach (var found in EnumerateWorkbenchDescriptors(docsRoot))
        {
            var dir = Path.GetDirectoryName(found)!;
            var folder = Path.GetFileName(dir);
            var descriptorRel = Path.GetRelativePath(root, found).Replace('\\', '/');
            var safeDir = ContainedPath(root, Path.GetRelativePath(root, dir));
            if (safeDir == null)
            {
                result.Add(Invalid(folder, "Workbench folder is a symbolic link or reparse point.", descriptorRel));
                continue;
            }
            var descriptor = ContainedPath(root,
                Path.GetRelativePath(root, Path.Combine(safeDir, "workbench.json")));
            if (descriptor == null || !File.Exists(descriptor))
            {
                result.Add(Invalid(folder, descriptor == null
                    ? "workbench.json is a symbolic link or reparse point."
                    : "Missing workbench.json.", descriptorRel));
                continue;
            }
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(descriptor));
                var obj = json.RootElement;
                var schema = RequiredInt(obj, "schemaVersion");
                var id = RequiredString(obj, "id");
                var title = RequiredString(obj, "title");
                var summary = RequiredString(obj, "summary");
                var entrypoint = RequiredString(obj, "entrypoint");
                var status = RequiredString(obj, "status");
                var updatedText = RequiredString(obj, "updatedAt");
                var phase = OptionalString(obj, "phase");
                if (schema != 1) throw new InvalidDataException("schemaVersion must be 1.");
                if (!SafeId(id) || id != folder) throw new InvalidDataException("id must match the containing folder.");
                if (!AllowedStatuses.Contains(status)) throw new InvalidDataException($"Unsupported status '{status}'.");
                if (phase != null && !AllowedPhases.Contains(phase)) throw new InvalidDataException($"Unsupported phase '{phase}'.");
                if (!DateTimeOffset.TryParse(updatedText, out var updated)) throw new InvalidDataException("updatedAt must be an ISO timestamp.");
                var extension = Path.GetExtension(entrypoint);
                if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("entrypoint must be HTML.");
                var full = ContainedPath(safeDir, entrypoint);
                if (full == null || !File.Exists(full)) throw new InvalidDataException("entrypoint is missing or escapes its Workbench folder.");
                if (!IsHtmlWithinLimit(full))
                    throw new InvalidDataException($"HTML exceeds the {MaxHtmlBytes / (1024 * 1024)} MiB Workbench limit.");
                var repoRel = Path.GetRelativePath(root, full).Replace('\\', '/');
                result.Add(new WorkbenchListItem(id, title, summary, status, phase,
                    updated.UtcDateTime, repoRel, true, null, StringArray(obj, "sourceTaskKeys")));
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
            {
                result.Add(Invalid(folder, ex.Message, descriptorRel, descriptor));
            }
        }
        return result;
    }

    /// <summary>
    /// Recursively yields every <c>workbench.json</c> descriptor under docs/,
    /// skipping dot-directories, node_modules-like folders, the shared assets
    /// tree, and reparse points so a Workbench can live with its own theme.
    /// </summary>
    private static IEnumerable<string> EnumerateWorkbenchDescriptors(string docsRoot)
    {
        var stack = new Stack<string>();
        stack.Push(docsRoot);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            string[] subdirs;
            try
            {
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                files = Directory.GetFiles(dir, "workbench.json");
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith(".", StringComparison.Ordinal)
                    || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("assets", StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isReparse;
                try { isReparse = (new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (isReparse)
                {
                    // A symlinked / junctioned Workbench folder must be surfaced
                    // (and later refused by the ContainedPath guard) rather than
                    // silently skipped - never recurse through the reparse point.
                    var linkedDescriptor = Path.Combine(sub, "workbench.json");
                    if (File.Exists(linkedDescriptor)) yield return linkedDescriptor;
                    continue;
                }
                stack.Push(sub);
            }
        }
    }

    private static WorkbenchListItem Invalid(string folder, string error, string entryPath, string? safePath = null) =>
        new(SafeId(folder) ? folder : "invalid-workbench", folder, "Descriptor needs repair.",
            "invalid", null, safePath != null && File.Exists(safePath)
                ? File.GetLastWriteTimeUtc(safePath)
                : DateTime.UtcNow,
            entryPath, false, error, []);

    private string? ResolveRoot(string projectName) =>
        ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);

    private static string? ContainedPath(string root, string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return null;
        try
        {
            var rootFull = Path.GetFullPath(root);
            var rootPrefix = Path.EndsInDirectorySeparator(rootFull)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(rootFull,
                rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(rootPrefix, PathComparison)) return null;

            var current = rootFull;
            var segments = Path.GetRelativePath(rootFull, full)
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!File.Exists(current) && !Directory.Exists(current)) continue;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static bool IsHtmlWithinLimit(string path)
    {
        try { return new FileInfo(path).Length <= MaxHtmlBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string? ReadHtmlWithinLimit(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxHtmlBytes) return null;
            using var buffer = new MemoryStream((int)Math.Min(stream.Length, MaxHtmlBytes));
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > MaxHtmlBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ChangeTouchesPath(string changedPath, string targetPath)
    {
        var normalizedTarget = targetPath.Replace('\\', '/').TrimStart('/');
        var normalizedChange = changedPath.Replace('\\', '/').Trim().Trim('"');
        var renameSeparator = normalizedChange.LastIndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
            normalizedChange = normalizedChange[(renameSeparator + 4)..].Trim().Trim('"');
        return normalizedChange.Equals(normalizedTarget, PathComparison)
            || normalizedChange.EndsWith('/')
                && normalizedTarget.StartsWith(normalizedChange, PathComparison);
    }

    private static bool SafeId(string value) => value.Length is > 0 and <= 80
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string RequiredString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new InvalidDataException($"{name} is required.");
    private static int RequiredInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed)
            ? parsed : throw new InvalidDataException($"{name} is required.");
    private static string? OptionalString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] StringArray(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];
}

public record WorkbenchCatalogue(string ProjectName, bool IncludesHistory, int Count, List<WorkbenchListItem> Items);
public record WorkbenchListItem(string Id, string Title, string Summary, string Status, string? Phase,
    DateTime UpdatedAtUtc, string EntryPath, bool Valid, string? Error, string[] SourceTaskKeys);
public record WorkbenchDocument(WorkbenchListItem Workbench, string Html, string? Branch, string? Revision,
    bool WorkingTreeModified);
