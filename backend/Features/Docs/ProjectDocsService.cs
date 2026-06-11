using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>
/// Project-level docs surface: security archive (free-form MD files +
/// a small <c>state.json</c> meta sidecar) and architecture decisions
/// (parsed from the ADR archive).
///
/// Prototype scope. Files live inside the watched project's repo or
/// root, never inside this app's working tree.
/// </summary>
public class ProjectDocsService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<ProjectDocsService> _logger;

    private const string SecurityRel = "docs/operations/security";
    private const string SecurityStateFile = "state.json";
    private const string AdrRel = "docs/architecture/decisions/adr-archive.md";
    private const string WikiRel = "docs";

    // The wiki tree is the physical docs/ hierarchy itself - folders are nodes,
    // files are pages - so there is no virtual organisation layer to maintain.
    // These are the document extensions the tree surfaces and the content /
    // history endpoints serve: markdown plus optional HTML concept pages (the
    // UI renders HTML inside a sandboxed iframe).
    private static readonly HashSet<string> WikiDocExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".html", ".htm" };

    // Optional `NN-` (or `NN_` / `NN.`) numeric prefix on a file/folder name:
    // controls sort order in the tree and is hidden from the displayed title.
    private static readonly Regex OrderPrefixRegex =
        new(@"^(?<num>\d+)[-_.\s]+", RegexOptions.Compiled);

    private static readonly Regex WikiFrontmatterRegex =
        new(@"\A---\r?\n(?<body>.*?)\r?\n---", RegexOptions.Singleline | RegexOptions.Compiled);

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
        var full = ResolveWikiPath(projectName, relPath, requireDoc: true);
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
        var full = ResolveWikiPath(projectName, relPath, requireDoc: false);
        if (full == null || !File.Exists(full)) return null;
        return (full, contentType);
    }

    /// <summary>
    /// Joins a caller-supplied relative path onto the docs root and confirms
    /// the resolved absolute path stays inside it (traversal guard). When
    /// <paramref name="requireDoc"/> is set, only wiki document extensions
    /// (<c>.md</c> / <c>.html</c> / <c>.htm</c>) pass.
    /// </summary>
    private string? ResolveWikiPath(string projectName, string relPath, bool requireDoc)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        if (relPath.Contains("..", StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(relPath)) return null;
        if (requireDoc && !WikiDocExtensions.Contains(Path.GetExtension(relPath)))
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

    /// <summary>
    /// Absolute path of a wiki doc (.md/.html) after the same traversal guard as
    /// the read endpoints. Exposed so the history endpoint can hand the path to
    /// <see cref="GitService"/> without re-implementing path resolution.
    /// </summary>
    public string? ResolveWikiDocFullPath(string projectName, string relPath) =>
        ResolveWikiPath(projectName, relPath, requireDoc: true);

    // -------- Wiki tree (physical docs/ folder hierarchy) --------

    /// <summary>
    /// The recursive physical structure under <c>docs/</c>: folder nodes plus
    /// document nodes (<c>.md</c> and <c>.html</c>). Siblings are sorted folders
    /// first, then files; an optional leading <c>NN-</c> numeric prefix on a
    /// name controls ordering and is stripped from the displayed title. No git
    /// is invoked here, so building the tree stays cheap even for a large docs
    /// folder (per-file commit metadata is fetched lazily on open).
    /// </summary>
    public WikiTree? GetWikiTree(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry == null) return null;
        var baseDir = ResolveBaseDir(entry);
        if (baseDir == null) return null;

        var wikiDir = Path.Combine(baseDir, WikiRel);
        var exists = Directory.Exists(wikiDir);
        var root = exists ? BuildTreeNodes(new DirectoryInfo(wikiDir), Path.GetFullPath(wikiDir)) : [];
        return new WikiTree(projectName, wikiDir, exists, root);
    }

    /// <summary>
    /// Recursively maps a docs directory into wiki tree nodes. Folders with no
    /// document descendants are dropped so the tree only surfaces navigable
    /// content. Hidden entries (dot-prefixed) are skipped.
    /// </summary>
    private static List<WikiTreeNode> BuildTreeNodes(DirectoryInfo dir, string docsRoot)
    {
        var nodes = new List<WikiTreeNode>();

        foreach (var sub in dir.GetDirectories())
        {
            if (sub.Name.StartsWith('.')) continue;
            var children = BuildTreeNodes(sub, docsRoot);
            if (children.Count == 0) continue; // prune empty folders
            var rel = Path.GetRelativePath(docsRoot, sub.FullName).Replace('\\', '/');
            nodes.Add(new WikiTreeNode(sub.Name, StripOrderPrefix(sub.Name), rel, "folder", children));
        }

        foreach (var file in dir.GetFiles())
        {
            if (file.Name.StartsWith('.')) continue;
            var ext = file.Extension;
            if (!WikiDocExtensions.Contains(ext)) continue;
            var rel = Path.GetRelativePath(docsRoot, file.FullName).Replace('\\', '/');
            var type = ext.Equals(".md", StringComparison.OrdinalIgnoreCase) ? "md" : "html";
            var title = ExtractFirstHeading(file.FullName)
                ?? StripOrderPrefix(Path.GetFileNameWithoutExtension(file.Name));
            nodes.Add(new WikiTreeNode(file.Name, title, rel, type, []));
        }

        nodes.Sort(CompareTreeNodes);
        return nodes;
    }

    /// <summary>Folders before files; then by numeric order prefix; then name.</summary>
    private static int CompareTreeNodes(WikiTreeNode a, WikiTreeNode b)
    {
        var aFolder = a.Type == "folder";
        var bFolder = b.Type == "folder";
        if (aFolder != bFolder) return aFolder ? -1 : 1;

        var ao = OrderPrefixValue(a.Name);
        var bo = OrderPrefixValue(b.Name);
        if (ao != bo) return ao.CompareTo(bo);

        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Numeric value of a leading <c>NN-</c> prefix, or max when absent.</summary>
    private static int OrderPrefixValue(string name)
    {
        var m = OrderPrefixRegex.Match(name);
        return m.Success && int.TryParse(m.Groups["num"].Value, out var n) ? n : int.MaxValue;
    }

    /// <summary>Drops a leading <c>NN-</c> ordering prefix from a display label.</summary>
    private static string StripOrderPrefix(string name)
    {
        var m = OrderPrefixRegex.Match(name);
        return m.Success ? name[m.Length..] : name;
    }

    /// <summary>
    /// Resolves a wiki node (file or folder) relative path to its absolute path
    /// under <c>docs/</c> with the standard traversal guard. Unlike the doc
    /// resolver this does not require a document extension, so folder targets of
    /// move/delete operations resolve too.
    /// </summary>
    public string? ResolveWikiNodeFullPath(string projectName, string relPath) =>
        ResolveWikiPath(projectName, relPath, requireDoc: false);

    /// <summary>
    /// Creates a new wiki document on disk (seed content optional). Returns the
    /// absolute path so the endpoint can commit it; fails when the path is
    /// unsafe, the extension is not a wiki document type, or the file exists.
    /// </summary>
    public WikiMutationResult CreateWikiPage(string projectName, string relPath, string? content)
    {
        var ext = Path.GetExtension(relPath);
        if (!WikiDocExtensions.Contains(ext))
            return WikiMutationResult.Fail("Only .md or .html pages are allowed.");
        var full = ResolveWikiPath(projectName, relPath, requireDoc: true);
        if (full == null) return WikiMutationResult.Fail("Invalid path.");
        if (File.Exists(full)) return WikiMutationResult.Fail("A page with that name already exists.");

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var seed = content ?? DefaultPageSeed(relPath, ext);
        File.WriteAllText(full, seed);
        return WikiMutationResult.Ok(full);
    }

    private static string DefaultPageSeed(string relPath, string ext)
    {
        var title = StripOrderPrefix(Path.GetFileNameWithoutExtension(relPath));
        return ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
            ? $"# {title}\n\n"
            : $"<!doctype html>\n<html>\n<head><meta charset=\"utf-8\"><title>{title}</title></head>\n<body>\n<h1>{title}</h1>\n</body>\n</html>\n";
    }

    /// <summary>
    /// Creates a new wiki folder. A folder needs a tracked file to survive git,
    /// so a <c>.gitkeep</c> placeholder is seeded inside it; the endpoint commits
    /// that. Fails when the path is unsafe or the folder already exists.
    /// </summary>
    public WikiMutationResult CreateWikiFolder(string projectName, string relPath)
    {
        var full = ResolveWikiPath(projectName, relPath, requireDoc: false);
        if (full == null) return WikiMutationResult.Fail("Invalid path.");
        if (Directory.Exists(full)) return WikiMutationResult.Fail("A folder with that name already exists.");

        Directory.CreateDirectory(full);
        var keep = Path.Combine(full, ".gitkeep");
        if (!File.Exists(keep)) File.WriteAllText(keep, "");
        return WikiMutationResult.Ok(keep);
    }

    /// <summary>
    /// Parses the leading YAML frontmatter block (<c>--- … ---</c>) of a wiki
    /// doc into the provenance fields the history panel surfaces: which model
    /// last edited it, when, and why. Hand-written docs without frontmatter
    /// return <see cref="WikiDocMetadata.HasFrontmatter"/> = false, leaving the
    /// panel to fall back to git history alone. Static + side-effect free for
    /// unit testing.
    /// </summary>
    public static WikiDocMetadata ParseWikiMetadata(string? content)
    {
        var empty = new WikiDocMetadata(null, null, null, null, null, null, false);
        if (string.IsNullOrEmpty(content)) return empty;
        var m = WikiFrontmatterRegex.Match(content);
        if (!m.Success) return empty;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in m.Groups["body"].Value.Split('\n'))
        {
            var raw = line.TrimEnd('\r');
            var idx = raw.IndexOf(':');
            if (idx <= 0) continue;
            var key = raw[..idx].Trim();
            var val = raw[(idx + 1)..].Trim().Trim('"');
            if (key.Length == 0 || val.Length == 0) continue;
            map.TryAdd(key, val);
        }

        string? Get(params string[] keys)
        {
            foreach (var k in keys)
                if (map.TryGetValue(k, out var v)) return v;
            return null;
        }

        return new WikiDocMetadata(
            Model: Get("model", "agent-model"),
            UpdatedAt: Get("last-distilled", "last-updated", "updated", "date"),
            Reason: Get("why", "reason", "summary"),
            TaskKey: Get("task-key"),
            Status: Get("status"),
            RunCount: Get("run-count"),
            HasFrontmatter: true);
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
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: Unreadable file: fall back to the file name in the caller.");
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

/// <summary>Provenance distilled from a wiki doc's YAML frontmatter.</summary>
public record WikiDocMetadata(
    string? Model,
    string? UpdatedAt,
    string? Reason,
    string? TaskKey,
    string? Status,
    string? RunCount,
    bool HasFrontmatter);

/// <summary>
/// One node in the physical wiki tree. A <c>folder</c> carries child nodes; a
/// document node (<c>type</c> = <c>md</c> or <c>html</c>) is a leaf. <see
/// cref="RelPath"/> is the docs-root-relative path; <see cref="Title"/> is the
/// display label (first H1 for docs, order-prefix-stripped name otherwise).
/// </summary>
public record WikiTreeNode(string Name, string Title, string? RelPath, string Type, List<WikiTreeNode> Children);

/// <summary>The physical docs/ folder tree exposed to the wiki UI.</summary>
public record WikiTree(string ProjectName, string BaseDir, bool Exists, List<WikiTreeNode> Root);

/// <summary>Outcome of a wiki filesystem mutation (create/move/delete).</summary>
public record WikiMutationResult(bool Success, string? FullPath, string? Error)
{
    public static WikiMutationResult Ok(string fullPath) => new(true, fullPath, null);
    public static WikiMutationResult Fail(string error) => new(false, null, error);
}

/// <summary>History + provenance payload for one wiki doc.</summary>
public record WikiFileHistory(string RelPath, string? Model, WikiDocMetadata Metadata, List<GitCommitInfo> Commits);

public record SecurityMeta(string? LastReviewDate, string? Rating, string? Summary);
public record SecurityFileEntry(string Name, string RelPath, DateTime UpdatedAt, long Size);
public record SecurityOverview(string ProjectName, string BaseDir, bool Exists, SecurityMeta Meta, List<SecurityFileEntry> Files);

public record ArchitectureDecisionSummary(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureDecisionDetail(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureOverview(string ProjectName, string SourceFile, bool Exists, string Preamble, List<ArchitectureDecisionSummary> Decisions);
