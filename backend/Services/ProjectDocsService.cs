using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;

namespace OrchestratorApi.Services;

/// <summary>
/// Project-level docs surface: security archive (free-form MD files +
/// a small <c>state.json</c> meta sidecar) and architecture decisions
/// (parsed from a single <c>architecture-decisions.md</c> file).
///
/// Prototype scope. Files live inside the watched project's repo or
/// root, never inside this app's working tree.
/// </summary>
public class ProjectDocsService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<ProjectDocsService> _logger;

    private const string SecurityRel = "docs/security";
    private const string SecurityStateFile = "state.json";
    private const string AdrRel = "docs/architecture-decisions.md";
    private const string WikiRel = "docs";

    // Image/diagram extensions the wiki asset endpoint is allowed to serve so
    // relative `![](images/foo.png)` references in a doc render in place.
    private static readonly Dictionary<string, string> WikiAssetContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
    };

    public ProjectDocsService(TaskScannerService scanner, ILogger<ProjectDocsService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    private WatchPathEntry? FindProject(string projectName) =>
        _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Repository checkout root. Falls back to the configured RootPath
    /// when no explicit RepositoryPath is set, so projects without a
    /// separate task repo still work.
    /// </summary>
    private static string? ResolveBaseDir(WatchPathEntry entry)
    {
        var b = !string.IsNullOrWhiteSpace(entry.RepositoryPath) ? entry.RepositoryPath : entry.RootPath;
        return string.IsNullOrWhiteSpace(b) ? null : b;
    }

    private static bool IsSafeRelPath(string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return false;
        if (relPath.Contains("..", StringComparison.Ordinal)) return false;
        if (Path.IsPathRooted(relPath)) return false;
        // Only markdown files allowed for the prototype.
        var ext = Path.GetExtension(relPath);
        return string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase);
    }

    // -------- Security --------

    public SecurityOverview? GetSecurityOverview(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;

        var secDir = Path.Combine(baseDir, SecurityRel);
        var meta = ReadMeta(secDir);
        var files = ListFiles(secDir);

        return new SecurityOverview(
            ProjectName: projectName,
            BaseDir: secDir,
            Exists: Directory.Exists(secDir),
            Meta: meta,
            Files: files);
    }

    public string? ReadSecurityFile(string projectName, string relPath)
    {
        if (!IsSafeRelPath(relPath)) return null;
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;
        var full = Path.GetFullPath(Path.Combine(baseDir, SecurityRel, relPath));
        var root = Path.GetFullPath(Path.Combine(baseDir, SecurityRel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(full)) return null;
        return File.ReadAllText(full);
    }

    public bool WriteSecurityFile(string projectName, string relPath, string content)
    {
        if (!IsSafeRelPath(relPath)) return false;
        var entry = FindProject(projectName);
        if (entry == null) return false;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return false;
        var full = Path.GetFullPath(Path.Combine(baseDir, SecurityRel, relPath));
        var root = Path.GetFullPath(Path.Combine(baseDir, SecurityRel));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        return true;
    }

    public bool WriteSecurityMeta(string projectName, SecurityMeta meta)
    {
        var entry = FindProject(projectName);
        if (entry == null) return false;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return false;
        var secDir = Path.Combine(baseDir, SecurityRel);
        Directory.CreateDirectory(secDir);
        var path = Path.Combine(secDir, SecurityStateFile);
        File.WriteAllText(path, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        return true;
    }

    private static SecurityMeta ReadMeta(string secDir)
    {
        var path = Path.Combine(secDir, SecurityStateFile);
        if (!File.Exists(path)) return new SecurityMeta(null, null, null);
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SecurityMeta>(json) ?? new SecurityMeta(null, null, null);
        }
        catch
        {
            return new SecurityMeta(null, null, null);
        }
    }

    private static List<SecurityFileEntry> ListFiles(string secDir)
    {
        if (!Directory.Exists(secDir)) return [];
        var root = Path.GetFullPath(secDir);
        var results = new List<SecurityFileEntry>();
        foreach (var f in Directory.EnumerateFiles(secDir, "*.md", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            results.Add(new SecurityFileEntry(
                Name: Path.GetFileName(f),
                RelPath: rel,
                UpdatedAt: fi.LastWriteTimeUtc,
                Size: fi.Length));
        }
        results.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    // -------- Wiki (docs/ tree) --------

    /// <summary>
    /// Read-only browse surface over the project's <c>docs/</c> tree: every
    /// <c>.md</c> file (recursive), relative to the docs root, so the UI can
    /// render the navigation card, the domain documents, and the accumulated
    /// learnings from the wiki post-processing step. Title is the first H1
    /// heading when present, otherwise the file name.
    /// </summary>
    public WikiOverview? GetWikiOverview(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;

        var wikiDir = Path.Combine(baseDir, WikiRel);
        return new WikiOverview(
            ProjectName: projectName,
            BaseDir: wikiDir,
            Exists: Directory.Exists(wikiDir),
            Files: ListWikiDocs(wikiDir));
    }

    public WikiFileContent? ReadWikiFile(string projectName, string relPath)
    {
        var full = ResolveWikiPath(projectName, relPath, requireMarkdown: true);
        if (full == null || !File.Exists(full)) return null;
        return new WikiFileContent(relPath.Replace('\\', '/'), File.ReadAllText(full));
    }

    /// <summary>
    /// Resolves a non-markdown asset (image/diagram) referenced from a wiki
    /// doc. Returns the absolute file path plus a content type, or null when
    /// the path is unsafe, the extension is not an allowed image type, or the
    /// file is missing. Bytes are streamed by the endpoint via Results.File.
    /// </summary>
    public (string Path, string ContentType)? ReadWikiAsset(string projectName, string relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        var ext = Path.GetExtension(relPath);
        if (!WikiAssetContentTypes.TryGetValue(ext, out var contentType)) return null;
        var full = ResolveWikiPath(projectName, relPath, requireMarkdown: false);
        if (full == null || !File.Exists(full)) return null;
        return (full, contentType);
    }

    /// <summary>
    /// Joins a caller-supplied relative path onto the docs root and confirms
    /// the resolved absolute path stays inside it (traversal guard). When
    /// <paramref name="requireMarkdown"/> is set, only <c>.md</c> files pass.
    /// </summary>
    private string? ResolveWikiPath(string projectName, string relPath, bool requireMarkdown)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        if (relPath.Contains("..", StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(relPath)) return null;
        if (requireMarkdown && !string.Equals(Path.GetExtension(relPath), ".md", StringComparison.OrdinalIgnoreCase))
            return null;

        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;

        var root = Path.GetFullPath(Path.Combine(baseDir, WikiRel));
        var full = Path.GetFullPath(Path.Combine(root, relPath));
        // Append a separator to the root so "docs-other/" can't satisfy the prefix.
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static List<WikiFileEntry> ListWikiDocs(string wikiDir)
    {
        if (!Directory.Exists(wikiDir)) return [];
        var root = Path.GetFullPath(wikiDir);
        var results = new List<WikiFileEntry>();
        foreach (var f in Directory.EnumerateFiles(wikiDir, "*.md", SearchOption.AllDirectories))
        {
            var fi = new FileInfo(f);
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            results.Add(new WikiFileEntry(
                Name: Path.GetFileName(f),
                RelPath: rel,
                Title: ExtractFirstHeading(f) ?? Path.GetFileNameWithoutExtension(f),
                UpdatedAt: fi.LastWriteTimeUtc,
                Size: fi.Length));
        }
        results.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>
    /// Cheap first-H1 sniff for a nicer label than the file name. Reads at
    /// most the first handful of lines so listing a large tree stays fast.
    /// </summary>
    private static string? ExtractFirstHeading(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            for (int i = 0; i < 40; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                    return trimmed[2..].Trim();
            }
        }
        catch
        {
            // Unreadable file: fall back to the file name in the caller.
        }
        return null;
    }

    // -------- Architecture decisions --------

    public ArchitectureOverview? GetArchitectureOverview(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;

        var adrPath = Path.Combine(baseDir, AdrRel);
        if (!File.Exists(adrPath))
        {
            return new ArchitectureOverview(
                ProjectName: projectName,
                SourceFile: adrPath,
                Exists: false,
                Preamble: "",
                Decisions: []);
        }

        var (preamble, decisions) = ParseAdrFile(File.ReadAllText(adrPath));
        return new ArchitectureOverview(
            ProjectName: projectName,
            SourceFile: adrPath,
            Exists: true,
            Preamble: preamble,
            Decisions: decisions);
    }

    public ArchitectureDecisionDetail? GetArchitectureDecision(string projectName, string id)
    {
        var overview = GetArchitectureOverview(projectName);
        if (overview == null || !overview.Exists) return null;
        var summary = overview.Decisions.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (summary == null) return null;

        // Re-parse to retrieve the body content. The parser kept body
        // separately so we can return Markdown without the heading.
        var (_, all) = ParseAdrFile(File.ReadAllText(overview.SourceFile));
        var body = all.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))?.Body ?? "";
        return new ArchitectureDecisionDetail(
            Id: summary.Id,
            Title: summary.Title,
            Date: summary.Date,
            Status: summary.Status,
            Body: body);
    }

    /// <summary>
    /// Splits the ADR file on level-2 headings of the shape
    /// <c>## ADR-NNNN - Title (YYYY-MM-DD)</c>. Anything before the
    /// first heading is returned as preamble. Everything between two
    /// headings (and the trailing chunk) becomes one decision body.
    /// Body text is returned verbatim, including any trailing
    /// <c>---</c> separator that the source file uses.
    /// </summary>
    private static (string Preamble, List<ArchitectureDecisionSummary> Decisions) ParseAdrFile(string md)
    {
        var headingRegex = new Regex(@"^##\s+(ADR-\d+)\s*[-–—]\s*(.+?)(?:\s*\((\d{4}-\d{2}-\d{2})\))?\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);
        var matches = headingRegex.Matches(md);
        if (matches.Count == 0)
        {
            return (md, []);
        }

        var preamble = md[..matches[0].Index].TrimEnd();
        var list = new List<ArchitectureDecisionSummary>();
        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var bodyStart = m.Index + m.Length;
            var bodyEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : md.Length;
            var body = md[bodyStart..bodyEnd].Trim();
            // Strip trailing horizontal rule used as separator between ADRs.
            if (body.EndsWith("---", StringComparison.Ordinal))
            {
                body = body[..^3].TrimEnd();
            }

            var status = ExtractStatus(body) ?? "Unknown";
            list.Add(new ArchitectureDecisionSummary(
                Id: m.Groups[1].Value,
                Title: m.Groups[2].Value.Trim(),
                Date: m.Groups[3].Success ? m.Groups[3].Value : null,
                Status: status,
                Body: body));
        }
        return (preamble, list);
    }

    private static readonly Regex StatusRegex = new(@"\*\*Status\.\*\*\s*([^\n\r]+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static string? ExtractStatus(string body)
    {
        var m = StatusRegex.Match(body);
        if (!m.Success) return null;
        var s = m.Groups[1].Value.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}

public record WikiFileEntry(string Name, string RelPath, string Title, DateTime UpdatedAt, long Size);
public record WikiOverview(string ProjectName, string BaseDir, bool Exists, List<WikiFileEntry> Files);
public record WikiFileContent(string RelPath, string Content);

public record SecurityMeta(string? LastReviewDate, string? Rating, string? Summary);
public record SecurityFileEntry(string Name, string RelPath, DateTime UpdatedAt, long Size);
public record SecurityOverview(string ProjectName, string BaseDir, bool Exists, SecurityMeta Meta, List<SecurityFileEntry> Files);

public record ArchitectureDecisionSummary(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureDecisionDetail(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureOverview(string ProjectName, string SourceFile, bool Exists, string Preamble, List<ArchitectureDecisionSummary> Decisions);
