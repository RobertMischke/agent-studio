using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ProjectRegistry _registry;
    private readonly ILogger<ProjectDocsService> _logger;

    private const string SecurityRel = "docs/operations/security";
    private const string SecurityStateFile = "state.json";
    private const string AdrRel = "docs/system/architecture/decisions/adr-archive.md";
    internal const string WikiRel = "docs";

    // The code-contract area under docs/. Everything below docs/app/ is a
    // machine contract (JSON schemas, in-app help bodies, wiki config) whose
    // path and format only change alongside code - it is NOT knowledge content,
    // so it is hidden from every reading surface (tree, folder, search, pulse,
    // grading) exactly like a config file. Direct-path serving (schema loader,
    // help resolver, home config) still reaches into it by explicit path.
    internal const string WikiAppRel = "app";

    private const string WikiHomeRel = "app/config/home.json";

    // Stored display order of sibling category folders, keyed by parent folder
    // rel path ("" = docs root). Lives in the code-contract area under docs/app/
    // (moved out of the dot-prefixed root file in the 2026-07 app/ migration);
    // reserved via IsWikiConfigFile and hidden with the rest of docs/app/.
    internal const string WikiFolderOrderRel = "app/config/wiki-order.json";

    // The wiki tree is the physical docs/ hierarchy itself - folders are nodes,
    // files are pages - so there is no virtual organisation layer to maintain.
    // These are the document extensions the tree surfaces and the content /
    // history endpoints serve: markdown, optional HTML concept pages, and JSON
    // metadata pages (HTML renders inside a sandboxed iframe; JSON as source).
    private static readonly HashSet<string> WikiDocExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".html", ".htm", ".json" };

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

    // ---- Wiki performance caches (AGT-2013) ----
    //
    // Building the wiki tree opens one file per doc-node to sniff its title and
    // parses every companion sidecar - O(N) file reads with no memoization, so
    // every navigation re-read the whole docs/ tree from disk. Two cache layers
    // fix that: a per-file title memo so an unchanged doc is never re-opened, and
    // a whole-tree memo validated by a cheap enumerate-only signature so a warm
    // request that finds nothing changed returns the assembled tree (and its
    // ETag) without opening a single file. Both key on the project name; the
    // signature is what makes them self-invalidating on any docs/ change.

    // path -> (mtimeTicks, size, sniffed title). Survives across tree rebuilds so
    // a single-file edit re-reads only that one file, not all N.
    private readonly ConcurrentDictionary<string, (long Mtime, long Size, string? Title)> _titleCache =
        new(StringComparer.OrdinalIgnoreCase);

    // projectName -> (docs signature, assembled tree, ETag). A signature hit means
    // the docs/ tree is provably unchanged, so the cached tree is served verbatim.
    private readonly ConcurrentDictionary<string, (string Signature, WikiTree Tree, string ETag)> _treeCache =
        new(StringComparer.Ordinal);

    public ProjectDocsService(TaskScannerService scanner, ProjectRegistry registry, ILogger<ProjectDocsService> logger)
    {
        _scanner = scanner;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Drops the in-memory wiki title + tree caches. Tests that mutate a fixture
    /// docs/ tree in place (without an mtime-visible change, or faster than the
    /// filesystem timestamp resolution) call this to force a cold rebuild;
    /// production relies on the docs signature to self-invalidate.
    /// </summary>
    internal void InvalidateWikiTreeCache()
    {
        _titleCache.Clear();
        _treeCache.Clear();
    }

    /// <summary>
    /// Repository checkout root for a project: registry record first, legacy
    /// WatchPaths config second, storage-layout derivation last (see
    /// <see cref="ProjectRepoResolver"/>). Null when the project is unknown
    /// in both sources or has no repository.
    /// </summary>
    private string? ResolveBaseDir(string projectName)
        => ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);

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
        var baseDir = ResolveBaseDir(projectName);
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
        var baseDir = ResolveBaseDir(projectName);
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
        var baseDir = ResolveBaseDir(projectName);
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
        var baseDir = ResolveBaseDir(projectName);
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
    /// supported document file (recursive), relative to the docs root, so the UI
    /// can render the navigation card, the domain documents, JSON metadata, and
    /// the accumulated learnings from the wiki post-processing step.
    /// </summary>
    public WikiOverview? GetWikiOverview(string projectName)
    {
        var baseDir = ResolveBaseDir(projectName);
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
        GitProcessTelemetry.RecordFileRead();
        return new WikiFileContent(relPath.Replace('\\', '/'), File.ReadAllText(full));
    }

    public WikiSaveResult WriteWikiFile(string projectName, string relPath, string content)
    {
        var full = ResolveWikiPath(projectName, relPath, requireDoc: true);
        if (full == null) return WikiSaveResult.Fail("Invalid path.");
        if (!File.Exists(full)) return WikiSaveResult.Fail("File not found.");
        var before = File.ReadAllText(full);
        if (string.Equals(before, content, StringComparison.Ordinal))
            return WikiSaveResult.Ok(full, changed: false);
        File.WriteAllText(full, content);
        return WikiSaveResult.Ok(full, changed: true);
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
    /// (<c>.md</c> / <c>.html</c> / <c>.htm</c> / <c>.json</c>) pass.
    /// </summary>
    private string? ResolveWikiPath(string projectName, string relPath, bool requireDoc)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        if (relPath.Contains("..", StringComparison.Ordinal)) return null;
        if (Path.IsPathRooted(relPath)) return null;
        if (requireDoc && !WikiDocExtensions.Contains(Path.GetExtension(relPath)))
            return null;

        var baseDir = ResolveBaseDir(projectName);
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
    /// document nodes (<c>.md</c>, <c>.html</c>, and <c>.json</c>). Siblings are
    /// sorted folders first, then files; an optional leading <c>NN-</c> numeric
    /// prefix on a name controls ordering and is stripped from the displayed
    /// title. No git is invoked here, and a warm cache serves the whole tree
    /// without opening a file (see <see cref="GetWikiTreeResult"/>).
    /// </summary>
    public WikiTree? GetWikiTree(string projectName) => GetWikiTreeResult(projectName)?.Tree;

    /// <summary>
    /// <see cref="GetWikiTree"/> plus the tree's ETag, and the caching that makes
    /// both cheap. Cold, the tree is built by opening one file per doc-node to
    /// sniff its title; that per-file title read is memoized. Warm, a cheap
    /// enumerate-only signature over docs/ (every file's path + mtime + size, no
    /// content reads) is compared against the last build: an unchanged signature
    /// returns the cached tree and ETag with zero file reads, which is what keeps
    /// the wiki entry under target. Any add / remove / rename / edit bumps the
    /// signature and triggers a rebuild that re-reads only the changed files.
    /// The whole call is measured under a <see cref="GitProcessTelemetry"/> scope
    /// so the rollup shows how many files a request actually opened.
    /// </summary>
    public WikiTreeResult? GetWikiTreeResult(string projectName)
    {
        var baseDir = ResolveBaseDir(projectName);
        if (baseDir == null) return null;

        using var _t = GitProcessTelemetry.BeginRequest("wiki/tree", _logger);

        var wikiDir = Path.Combine(baseDir, WikiRel);
        var exists = Directory.Exists(wikiDir);
        if (!exists)
            return new WikiTreeResult(new WikiTree(projectName, wikiDir, false, []), FormatETag("wiki-tree-empty"));

        var fullWikiDir = Path.GetFullPath(wikiDir);
        var signature = ComputeDocsSignature(fullWikiDir);

        if (signature != null
            && _treeCache.TryGetValue(projectName, out var cached)
            && cached.Signature == signature)
        {
            return new WikiTreeResult(cached.Tree, cached.ETag);
        }

        var root = BuildTreeNodes(
            new DirectoryInfo(wikiDir),
            fullWikiDir,
            LoadWikiMetadataIndex(wikiDir),
            _titleCache,
            LoadWikiFolderOrder(fullWikiDir));
        var tree = new WikiTree(projectName, wikiDir, true, root);
        var etag = FormatETag("wiki-tree-" + (signature ?? "nosig"));

        if (signature != null)
            _treeCache[projectName] = (signature, tree, etag);

        return new WikiTreeResult(tree, etag);
    }

    /// <summary>
    /// Enumerate-only fingerprint of the docs/ tree: every file's full path,
    /// last-write time, and size, hashed. Deliberately reads no file contents -
    /// it is the cheap "did anything change" probe that gates the expensive
    /// title-sniffing rebuild. A doc edit bumps its mtime; an add / remove /
    /// rename changes the set; either way the hash changes. Returns null if the
    /// tree can't be enumerated, which the caller treats as "always rebuild".
    /// </summary>
    private static string? ComputeDocsSignature(string fullWikiDir)
    {
        try
        {
            var di = new DirectoryInfo(fullWikiDir);
            var sb = new StringBuilder();
            foreach (var f in di.EnumerateFiles("*", SearchOption.AllDirectories)
                                .OrderBy(f => f.FullName, StringComparer.Ordinal))
            {
                sb.Append(f.FullName).Append('')
                  .Append(f.LastWriteTimeUtc.Ticks).Append('')
                  .Append(f.Length).Append('\n');
            }
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: docs signature enumeration failed; rebuilding tree uncached.");
            return null;
        }
    }

    /// <summary>Formats a cache token as a quoted strong HTTP entity tag.</summary>
    internal static string FormatETag(string token) => "\"" + token + "\"";

    /// <summary>
    /// The most-recently-edited wiki documents for the dashboard "recent edits"
    /// list: page (rel path + title), git author, and timestamp, newest first,
    /// one entry per document. Author + time come from git history (ground
    /// truth) rather than any app-internal edit log, mirroring the per-doc
    /// history panel. Companion sidecars, non-document files, and paths that no
    /// longer exist on disk (deletions in the log) are dropped. Returns an empty
    /// list - never null payload fields - when the project, base dir, or repo
    /// can't be resolved so the surface degrades to "no recent edits".
    /// </summary>
    public WikiRecentEdits? GetWikiRecentEdits(string projectName, GitService git, int limit = 12)
        => GetWikiRecentEditsResult(projectName, git, limit)?.Edits;

    /// <summary>
    /// <see cref="GetWikiRecentEdits"/> plus its ETag, and the HEAD-keyed caching
    /// that makes it cheap. The recent-edits list is a view over committed git
    /// history, so it is invariant while HEAD does not move: the whole assembled
    /// payload (including the per-row title reads) is memoized keyed by the wiki
    /// branch HEAD sha. A new commit moves HEAD and refreshes it; until then a
    /// warm request pays only the cheap HEAD probe (itself briefly TTL-cached)
    /// instead of the multi-hundred-ms <c>git log</c> walk that dominated the
    /// dashboard landing. Measured under a <see cref="GitProcessTelemetry"/> scope.
    /// </summary>
    public WikiRecentEditsResult? GetWikiRecentEditsResult(string projectName, GitService git, int limit = 12)
    {
        var baseDir = ResolveBaseDir(projectName);
        if (baseDir == null) return null;
        if (limit <= 0) limit = 12;

        using var _t = GitProcessTelemetry.BeginRequest("wiki/recent", _logger);

        var wikiDir = Path.GetFullPath(Path.Combine(baseDir, WikiRel));
        var exists = Directory.Exists(wikiDir);
        if (!exists)
            return new WikiRecentEditsResult(
                new WikiRecentEdits(projectName, wikiDir, false, []), FormatETag("wiki-recent-nodir"));

        var repoRoot = git.ResolveRepoRootForProject(projectName);
        if (string.IsNullOrWhiteSpace(repoRoot))
            return new WikiRecentEditsResult(
                new WikiRecentEdits(projectName, wikiDir, true, []), FormatETag("wiki-recent-norepo"));

        var head = git.GetHeadShaCached(repoRoot);
        var key = string.Join('', "wiki-recent-payload", repoRoot, wikiDir, limit);
        var payload = git.MemoizeByHead(repoRoot, key,
            () => BuildRecentEdits(projectName, git, repoRoot, wikiDir, limit));
        var etag = FormatETag("wiki-recent-" + (head ?? "nohead") + "-" + limit);
        return new WikiRecentEditsResult(payload, etag);
    }

    /// <summary>
    /// Assembles the recent-edits payload from a fresh <c>git log</c> walk under
    /// docs/ and per-row title sniffs. Split out so <see cref="MemoizeByHead"/>
    /// only invokes it on a HEAD-miss; the loop, filtering, and title reads are
    /// unchanged from the original inline implementation.
    /// </summary>
    private WikiRecentEdits BuildRecentEdits(
        string projectName, GitService git, string repoRoot, string wikiDir, int limit)
    {
        var docsRepoRel = Path.GetRelativePath(repoRoot, wikiDir).Replace('\\', '/');
        // Ask git for more distinct files than we need: some will be filtered
        // out as companions, deletions, or non-doc files below.
        var raw = git.GetRecentEditsUnderPath(repoRoot, docsRepoRel, Math.Min(limit * 4, 200));

        var results = new List<WikiRecentEdit>();
        foreach (var e in raw)
        {
            var full = Path.GetFullPath(Path.Combine(repoRoot, e.RepoRelPath));
            if (!full.StartsWith(wikiDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(wikiDir, StringComparison.OrdinalIgnoreCase))
                continue;
            var docsRel = Path.GetRelativePath(wikiDir, full).Replace('\\', '/');
            var ext = Path.GetExtension(full);
            if (!WikiDocExtensions.Contains(ext)) continue;
            if (IsHiddenWikiPath(docsRel) || IsWikiCompanionFile(docsRel) || IsWikiConfigFile(docsRel)) continue;
            if (!File.Exists(full)) continue; // a deletion in the log

            var title = ExtractDocTitle(full, ext)
                ?? StripOrderPrefix(Path.GetFileNameWithoutExtension(full));
            results.Add(new WikiRecentEdit(
                RelPath: docsRel,
                Title: title,
                Author: e.Author,
                AuthorDateUtc: e.AuthorDateUtc,
                Sha: e.Sha,
                ShortSha: e.ShortSha,
                Subject: e.Subject));
            if (results.Count >= limit) break;
        }

        return new WikiRecentEdits(projectName, wikiDir, true, results);
    }

    // -------- Wiki Pulse (generated wiki landing view) --------

    // Top-level repo directories that are code (not knowledge) but are never a
    // "code root" for the drift heuristic: the wiki root itself plus build
    // output / tooling folders whose churn is not a system-behaviour signal.
    // Everything else at the repo root is treated as a code root.
    private static readonly HashSet<string> CodeRootDenyList =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "node_modules", "dist", "build", "bin", "obj", "out", "target",
            "coverage", "test-results", "playwright-report", "packages-lock",
            ".git", ".vs", ".vscode", ".idea", ".github",
        };

    // Conventional wiki landing files that legitimately sit at the docs root, so
    // they are not flagged as unfiled "inbox" fragments.
    private static readonly HashSet<string> RootIndexNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "README.md", "index.md", "index.html", "index.htm", "home.md",
        };

    // A task key stamped into a commit subject / page frontmatter, e.g. AGT-2014.
    private static readonly Regex TaskKeyRegex =
        new(@"\b([A-Z][A-Z0-9]{1,9}-\d{1,6})\b", RegexOptions.Compiled);

    /// <summary>
    /// The wiki Pulse landing payload (PULSE-1): the generated entry view shown
    /// when the wiki opens, composed from three deterministic sources so it never
    /// multiplies the slow per-doc git calls (two <c>git log</c> spawns total):
    /// <list type="number">
    ///   <item><b>Change feed</b> - the recently-edited pages (git author + when),
    ///   each enriched with its top-level docs-folder badge and a task key parsed
    ///   from the page frontmatter or the commit subject.</item>
    ///   <item><b>Inbox</b> - loose / unfiled knowledge pages that sit at the wiki
    ///   root; an empty inbox is the healthy state.</item>
    ///   <item><b>Drift grading v1</b> - per top-level docs folder with pages, how
    ///   many commits landed under the code roots since each page was last
    ///   updated, banded Fresh (0-9) / Aging (10-49) / Stale (50+); the folder
    ///   grade is its worst page.</item>
    /// </list>
    /// Each section degrades to an "unavailable" state carrying a reason rather
    /// than failing, so a missing docs folder or repository never blank-screens
    /// the view. Returns <c>null</c> only when the project itself is unknown.
    /// </summary>
    public WikiPulse? GetWikiPulse(string projectName, GitService git, int feedLimit = 12)
    {
        var baseDir = ResolveBaseDir(projectName);
        if (baseDir == null) return null;
        feedLimit = Math.Clamp(feedLimit, 1, 50);

        var wikiDir = Path.GetFullPath(Path.Combine(baseDir, WikiRel));
        var generatedAt = DateTime.UtcNow.ToString("o");

        if (!Directory.Exists(wikiDir))
        {
            const string reason = "No docs/ folder for this project yet.";
            return new WikiPulse(projectName, wikiDir, false, generatedAt,
                new WikiPulseFeed(false, reason, []),
                new WikiPulseInbox(false, reason, 0, []),
                WikiPulseDrift.Unavailable(reason),
                WikiPulseCritical.Unavailable(reason),
                WikiPulseWarnings.Unavailable(reason),
                WikiPulseActivity.Unavailable(reason));
        }

        // Inbox + the LLM critical-pages list are pure filesystem reads (no git),
        // so they work even when the project has no resolvable repository. The
        // critical list is the wiki-grading verdict surfaced in Pulse (AGT-2051),
        // supplementing the deterministic drift bar below.
        var allDocs = ListWikiDocs(wikiDir);
        var inbox = BuildPulseInbox(allDocs);
        var critical = BuildPulseCritical(allDocs, LoadWikiMetadataIndex(wikiDir));
        var warnings = BuildPulseWarnings(wikiDir, allDocs);
        var activity = BuildPulseActivity(projectName, git);

        var repoRoot = git.ResolveRepoRootForProject(projectName);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            const string reason = "No git repository resolved for this project.";
            return new WikiPulse(projectName, wikiDir, true, generatedAt,
                new WikiPulseFeed(false, reason, []),
                inbox,
                WikiPulseDrift.Unavailable(reason),
                critical,
                warnings,
                activity);
        }

        var docsRepoRel = Path.GetRelativePath(repoRoot, wikiDir).Replace('\\', '/');
        // One git walk backs BOTH the feed (top N, newest first) and the drift
        // heuristic's per-page "last updated" map, so Pulse costs one docs log.
        var rawRecent = git.GetRecentEditsUnderPath(repoRoot, docsRepoRel, limit: 2000, commitScan: 1500);

        var feedItems = new List<WikiPulseFeedItem>();
        var lastUpdateByRel = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in rawRecent)
        {
            var full = Path.GetFullPath(Path.Combine(repoRoot, e.RepoRelPath));
            if (!full.StartsWith(wikiDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(wikiDir, StringComparison.OrdinalIgnoreCase))
                continue;
            var docsRel = Path.GetRelativePath(wikiDir, full).Replace('\\', '/');
            var ext = Path.GetExtension(full);
            if (!WikiDocExtensions.Contains(ext)) continue;
            if (IsHiddenWikiPath(docsRel) || IsWikiCompanionFile(docsRel) || IsWikiConfigFile(docsRel)) continue;
            if (!File.Exists(full)) continue; // a deletion in the log

            lastUpdateByRel[docsRel] = e.AuthorDateUtc;

            if (feedItems.Count < feedLimit)
            {
                var title = ExtractDocTitle(full, ext)
                    ?? StripOrderPrefix(Path.GetFileNameWithoutExtension(full));
                var areaSlug = TopFolderForPath(docsRel);
                feedItems.Add(new WikiPulseFeedItem(
                    RelPath: docsRel,
                    Title: title,
                    Author: e.Author,
                    AuthorDateUtc: e.AuthorDateUtc,
                    Sha: e.Sha,
                    ShortSha: e.ShortSha,
                    Subject: e.Subject,
                    AreaSlug: areaSlug,
                    AreaTitle: areaSlug == null ? null : StripOrderPrefix(areaSlug),
                    TaskKey: ExtractPulseTaskKey(full, ext, e.Subject)));
            }
        }

        var feed = new WikiPulseFeed(true, feedItems.Count == 0 ? "No recent edits in git history." : null, feedItems);

        var codeRoots = ResolveCodeRoots(repoRoot);
        var drift = BuildPulseDrift(
            allDocs, lastUpdateByRel, repoRoot, codeRoots, git,
            BuildFolderOrderIndex(LoadWikiFolderOrder(wikiDir), parentRel: string.Empty));

        return new WikiPulse(projectName, wikiDir, true, generatedAt, feed, inbox, drift, critical, warnings, activity);
    }

    private static readonly Regex MarkdownLinkRegex =
        new(@"!?\[[^\]]*\]\((?<target>[^)\s]+)(?:\s+[^)]*)?\)", RegexOptions.Compiled);
    private static readonly Regex HtmlLinkRegex =
        new(@"(?:href|src)\s*=\s*[""'](?<target>[^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// PULSE-2 warnings from deterministic docs integrity checks. The
    /// <c>human-action</c> signal is a folder-independent frontmatter convention
    /// (see <c>docs/system/contracts/wiki-tree.md</c>): any page carrying a
    /// <c>human-action</c> value whose <c>status</c> is <c>observed</c> or
    /// <c>active</c> raises a Pulse warning, wherever it lives.
    /// </summary>
    private static WikiPulseWarnings BuildPulseWarnings(string wikiDir, IReadOnlyList<WikiFileEntry> docs)
    {
        var items = new List<WikiPulseWarningItem>();
        foreach (var doc in docs)
        {
            var full = Path.Combine(wikiDir, doc.RelPath.Replace('/', Path.DirectorySeparatorChar));
            string text;
            try { text = File.ReadAllText(full); }
            catch { continue; }

            var action = FrontmatterScalar(text, "human-action");
            var status = FrontmatterScalar(text, "status");
            if (!string.IsNullOrWhiteSpace(action)
                && status is not null
                && (status.Equals("observed", StringComparison.OrdinalIgnoreCase)
                    || status.Equals("active", StringComparison.OrdinalIgnoreCase)))
            {
                items.Add(new("human-action", doc.Title,
                    $"Development signal is {status.ToLowerInvariant()}.", action, doc.RelPath, status.ToLowerInvariant()));
            }

            // NOTE (Welle 2 review): an "unclassified page" nudge was emitted here
            // for every knowledge page whose sidecar carries no classification and
            // whose folder has no default type. The born-classified stamp
            // (WriteCreationClassification) only covers pages created *after* the
            // 2026-07 convention, so the pre-existing corpus (161 of 375 pages in
            // stable) is not backfilled and the panel shipped flooded with ~160
            // low-signal nudges that buried the actionable dead-link / human-action
            // warnings. The nudge was removed until a real backfill classifies the
            // existing corpus; re-introducing it requires that backfill first (a
            // blind stamp of status=aktuell/analyzedAt=today would fabricate an
            // "analyzed" signal the grading/drift surfaces trust).

            foreach (Match match in (Path.GetExtension(full).Equals(".md", StringComparison.OrdinalIgnoreCase)
                         ? MarkdownLinkRegex.Matches(text)
                         : HtmlLinkRegex.Matches(text)))
            {
                var target = match.Groups["target"].Value.Trim();
                if (!IsInternalLink(target) || InternalLinkExists(wikiDir, doc.RelPath, target)) continue;
                items.Add(new("dead-link", $"Dead link in {doc.Title}", target,
                    "Repair or remove this internal link.", doc.RelPath, null));
            }
        }

        return new(true, items.Count == 0 ? "No warnings need human attention." : null, items.Count, items);
    }

    private WikiPulseActivity BuildPulseActivity(string projectName, GitService git)
    {
        var runs = new List<WikiPulseLiveRun>();
        foreach (var task in _scanner.ScanAllJobs().Where(t =>
                     string.Equals(t.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(t.State, TaskStates.Progress, StringComparison.Ordinal)))
        {
            var status = git.GetStatus(task.Id, task.WatchPath, preferRunLocation: true);
            if (!status.IsRepo || !status.Files.Any(f => f.Path.Replace('\\', '/').StartsWith("docs/", StringComparison.OrdinalIgnoreCase))) continue;
            var taskKey = !string.IsNullOrWhiteSpace(task.Key) ? task.Key
                : !string.IsNullOrWhiteSpace(task.TaskKey) ? task.TaskKey : task.Id;
            runs.Add(new(taskKey, task.State, task.EnteredLaneAt,
                status.Files.Count(f => f.Path.Replace('\\', '/').StartsWith("docs/", StringComparison.OrdinalIgnoreCase))));
        }

        return new(true, runs.Count == 0 ? "No live run currently touches docs/." : null, runs);
    }

    private static string? FrontmatterScalar(string text, string key)
    {
        var frontmatter = WikiFrontmatterRegex.Match(text);
        if (!frontmatter.Success) return null;
        var match = Regex.Match(frontmatter.Groups["body"].Value, $@"(?im)^{Regex.Escape(key)}:\s*(?<value>[^\r\n]+)$");
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"', '\'') : null;
    }

    private static bool IsInternalLink(string target) =>
        !string.IsNullOrWhiteSpace(target) && !target.StartsWith('#') && !target.StartsWith('/')
        && !Regex.IsMatch(target, @"^[a-z][a-z0-9+.-]*:", RegexOptions.IgnoreCase);

    private static bool InternalLinkExists(string wikiDir, string sourceRel, string target)
    {
        var clean = Uri.UnescapeDataString(target.Split('#', '?')[0]).Replace('/', Path.DirectorySeparatorChar);
        var sourceDir = Path.GetDirectoryName(Path.Combine(wikiDir, sourceRel.Replace('/', Path.DirectorySeparatorChar))) ?? wikiDir;
        string full;
        try { full = Path.GetFullPath(Path.Combine(sourceDir, clean)); }
        catch { return false; }
        if (!full.StartsWith(Path.GetFullPath(wikiDir), StringComparison.OrdinalIgnoreCase)) return false;
        return File.Exists(full) || Directory.Exists(full)
            || File.Exists(full + ".md") || File.Exists(Path.Combine(full, "README.md")) || File.Exists(Path.Combine(full, "index.html"));
    }

    /// <summary>
    /// The Pulse "critical pages" section (AGT-2051): every wiki page a
    /// wiki-grading run scored <c>C</c> or <c>D</c>, worst first, read from the
    /// companion <c>grading</c> blocks. This is the LLM verdict surfaced in Pulse
    /// - it supplements the deterministic drift bar (commit-count staleness) with
    /// a strong-model judgement of page health. Always available (a filesystem
    /// read); an ungraded wiki reads as a healthy empty state with a hint, and a
    /// wiki with only B-or-better grades reads as "no critical pages".
    /// </summary>
    private static WikiPulseCritical BuildPulseCritical(
        IReadOnlyList<WikiFileEntry> docs,
        IReadOnlyDictionary<string, WikiTreeMetadata> metaIndex)
    {
        var items = new List<WikiPulseCriticalItem>();
        var gradedCount = 0;
        foreach (var doc in docs)
        {
            if (!metaIndex.TryGetValue(doc.RelPath, out var meta)) continue;
            var grade = meta.GradingGrade?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(grade) || grade == "UNKNOWN") continue;
            gradedCount++;
            if (grade is "C" or "D")
            {
                items.Add(new WikiPulseCriticalItem(
                    RelPath: doc.RelPath,
                    Title: doc.Title,
                    Grade: grade,
                    Assessment: meta.GradingAssessment,
                    GradedAt: meta.GradedAt,
                    Model: meta.GradingModel,
                    ReportPath: meta.ReportPath,
                    AreaTitle: TopFolderForPath(doc.RelPath) is { } top ? StripOrderPrefix(top) : null));
            }
        }

        // Worst first: D before C, then a stable path order.
        items.Sort((a, b) =>
        {
            var byGrade = CriticalGradeRank(b.Grade).CompareTo(CriticalGradeRank(a.Grade));
            return byGrade != 0 ? byGrade : string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase);
        });

        var reason = gradedCount == 0
            ? "No pages have been graded yet. Run a grading pass from the trigger above."
            : items.Count == 0
                ? "No critical pages: every graded page is B or better."
                : null;
        var overall = items.Count == 0 ? "none" : items[0].Grade;
        return new WikiPulseCritical(true, reason, items.Count, overall, items);
    }

    /// <summary>Ordinal severity for the critical list sort (D worst).</summary>
    private static int CriticalGradeRank(string grade) => grade switch
    {
        "D" => 2,
        "C" => 1,
        _ => 0,
    };

    /// <summary>
    /// Task key for a Pulse feed row: the page's own frontmatter <c>task-key</c>
    /// wins (authoritative for markdown pages), else a key parsed from the commit
    /// subject. Null when neither carries one.
    /// </summary>
    private static string? ExtractPulseTaskKey(string fullPath, string ext, string? subject)
    {
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var fromMeta = ParseWikiMetadata(File.ReadAllText(fullPath)).TaskKey;
                if (!string.IsNullOrWhiteSpace(fromMeta)) return fromMeta.Trim();
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "ProjectDocsService: unreadable page for Pulse task-key; falling back to subject.");
            }
        }
        return ExtractTaskKeyFromSubject(subject);
    }

    /// <summary>Parses the first task key (e.g. <c>AGT-2014</c>) from a commit subject.</summary>
    internal static string? ExtractTaskKeyFromSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject)) return null;
        var m = TaskKeyRegex.Match(subject);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Loose / unfiled knowledge pages for the Pulse inbox: a knowledge doc that
    /// sits directly at the wiki root and is not a conventional landing file.
    /// That is a "needs sorting" signal; an empty list is the healthy state.
    /// </summary>
    private static WikiPulseInbox BuildPulseInbox(IReadOnlyList<WikiFileEntry> docs)
    {
        var items = new List<WikiPulseInboxItem>();
        foreach (var doc in docs)
        {
            var rel = doc.RelPath;
            if (rel.Contains('/', StringComparison.Ordinal) || RootIndexNames.Contains(rel)) continue;
            items.Add(new WikiPulseInboxItem(rel, doc.Title, TypeFromExtension(rel),
                "Loose page at the wiki root - not filed under a category."));
        }
        items.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
        return new WikiPulseInbox(true, null, items.Count, items);
    }

    /// <summary>
    /// The top-level docs folder a page lives under (the first path segment), or
    /// <c>null</c> for a page directly at the wiki root. Backs the Pulse
    /// change-feed area badge and the per-folder drift grade bar.
    /// </summary>
    internal static string? TopFolderForPath(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        var rel = relPath.Replace('\\', '/').Trim().Trim('/');
        var idx = rel.IndexOf('/', StringComparison.Ordinal);
        return idx <= 0 ? null : rel[..idx];
    }

    /// <summary>
    /// Deterministic drift grade bar (PULSE-1, no LLM): for each top-level docs
    /// folder that actually holds pages, count how many commits under the code
    /// roots landed after each page's last update, band each page Fresh / Aging /
    /// Stale, and grade the folder by its worst page. Folders without pages do
    /// not appear; ordering follows the saved wiki folder order
    /// (<c>docs/app/config/wiki-order.json</c>), unlisted folders behind in the tree's
    /// default order (numeric <c>NN-</c> prefix, then name). A
    /// page whose last-update timestamp is unknown (outside the git scan window)
    /// is left Unknown and excluded from the counts.
    /// </summary>
    private static WikiPulseDrift BuildPulseDrift(
        IReadOnlyList<WikiFileEntry> docs,
        IReadOnlyDictionary<string, DateTime> lastUpdateByRel,
        string repoRoot,
        IReadOnlyList<string> codeRoots,
        GitService git,
        IReadOnlyDictionary<string, int> rootFolderOrderIndex)
    {
        if (codeRoots.Count == 0)
            return WikiPulseDrift.Unavailable("No code roots found to grade drift against.");

        // Descending author dates of recent code commits; counting how many are
        // newer than a page's last update is the deterministic drift score.
        var codeTimes = git.GetCommitAuthorDatesUnderPaths(repoRoot, codeRoots, maxCommits: 500);

        // Drift groups are the real top-level docs folders that hold pages.
        var folders = docs
            .GroupBy(d => TopFolderForPath(d.RelPath), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key != null)
            .OrderBy(g => rootFolderOrderIndex.TryGetValue(g.Key!, out var pos) ? pos : int.MaxValue)
            .ThenBy(g => OrderPrefixValue(g.Key!))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var areas = new List<WikiPulseDriftArea>();
        int totalFresh = 0, totalAging = 0, totalStale = 0, totalGraded = 0;
        var worstOverall = DriftGradeRank("Empty");
        string overall = "Empty";

        foreach (var folder in folders)
        {
            var pages = folder.ToList();

            int fresh = 0, aging = 0, stale = 0, worstCount = 0;
            var areaWorst = DriftGradeRank("Empty");
            string areaGrade = "Empty";
            foreach (var page in pages)
            {
                if (!lastUpdateByRel.TryGetValue(page.RelPath, out var lastUpdate)) continue; // Unknown
                var since = codeTimes.Count(t => t > lastUpdate);
                if (since > worstCount) worstCount = since;
                var band = DriftBand(since);
                switch (band)
                {
                    case "Fresh": fresh++; break;
                    case "Aging": aging++; break;
                    case "Stale": stale++; break;
                }
                if (DriftGradeRank(band) > areaWorst)
                {
                    areaWorst = DriftGradeRank(band);
                    areaGrade = band;
                }
            }

            var graded = fresh + aging + stale;
            totalFresh += fresh; totalAging += aging; totalStale += stale; totalGraded += graded;
            if (areaWorst > worstOverall)
            {
                worstOverall = areaWorst;
                overall = areaGrade;
            }

            areas.Add(new WikiPulseDriftArea(
                Slug: folder.Key!,
                Title: StripOrderPrefix(folder.Key!),
                Grade: areaGrade,
                PageCount: pages.Count,
                GradedPageCount: graded,
                WorstCommitCount: worstCount,
                FreshCount: fresh,
                AgingCount: aging,
                StaleCount: stale));
        }

        var reason = totalGraded == 0
            ? (areas.Count == 0
                ? "No knowledge pages in any top-level docs folder yet."
                : "No page has an update inside the recent git scan window to grade drift against.")
            : null;
        return new WikiPulseDrift(true, reason, overall, areas,
            new WikiPulseDriftCounts(totalFresh, totalAging, totalStale, totalGraded));
    }

    /// <summary>Drift band for a code-commits-since-update count (0-9 / 10-49 / 50+).</summary>
    internal static string DriftBand(int commitsSinceUpdate) =>
        commitsSinceUpdate >= 50 ? "Stale" : commitsSinceUpdate >= 10 ? "Aging" : "Fresh";

    /// <summary>Ordinal severity so the worst page/area can be selected (higher = worse).</summary>
    private static int DriftGradeRank(string grade) => grade switch
    {
        "Stale" => 3,
        "Aging" => 2,
        "Fresh" => 1,
        _ => 0, // Empty / Unknown
    };

    /// <summary>
    /// The repo-root-relative directories treated as "code roots" for the drift
    /// heuristic: every top-level directory except the wiki root, build output,
    /// and tooling folders (see <see cref="CodeRootDenyList"/>). Deterministic and
    /// project-agnostic - it discovers whatever source folders a repo actually has.
    /// </summary>
    private static List<string> ResolveCodeRoots(string repoRoot)
    {
        var roots = new List<string>();
        if (!Directory.Exists(repoRoot)) return roots;
        var wikiTop = WikiRel.Split('/', '\\')[0]; // "docs"
        foreach (var dir in Directory.EnumerateDirectories(repoRoot))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
            if (name.Equals(wikiTop, StringComparison.OrdinalIgnoreCase)) continue;
            if (CodeRootDenyList.Contains(name)) continue;
            roots.Add(name);
        }
        roots.Sort(StringComparer.OrdinalIgnoreCase);
        return roots;
    }

    private static string TypeFromExtension(string relPath)
    {
        var ext = Path.GetExtension(relPath);
        if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase)) return "html";
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase)) return "json";
        return "md";
    }

    /// <summary>
    /// Per-doc history + provenance payload for the wiki history panel, with its
    /// ETag, and the HEAD-keyed caching behind it. The file's commit history and
    /// last-touching model are folded into one HEAD-keyed git memo (see
    /// <see cref="GitService.GetWikiDocGitInfoCached"/>); the frontmatter is parsed
    /// from the live on-disk file (one read, so the ETag folds in the file mtime).
    /// Returns null when the doc can't be resolved or is missing, matching the
    /// endpoint's 404 contract. Measured under a telemetry scope.
    /// </summary>
    public WikiHistoryResult? GetWikiHistory(string projectName, string relPath, GitService git)
    {
        using var _t = GitProcessTelemetry.BeginRequest("wiki/history", _logger);

        var full = ResolveWikiDocFullPath(projectName, relPath);
        if (full == null || !File.Exists(full)) return null;

        var repoRoot = git.ResolveRepoRootForProject(projectName);
        List<GitCommitInfo> commits = [];
        string? trailerModel = null;
        string? head = null;
        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            head = git.GetHeadShaCached(repoRoot);
            var repoRel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
            var info = git.GetWikiDocGitInfoCached(repoRoot, repoRel, 50);
            commits = info.Commits;
            trailerModel = info.Model;
        }

        GitProcessTelemetry.RecordFileRead();
        var meta = ParseWikiMetadata(File.ReadAllText(full));
        var model = !string.IsNullOrWhiteSpace(meta.Model) ? meta.Model : trailerModel;
        var relatedTasks = ReadRelatedTasks(full + ".meta.json");
        var knownKeys = _scanner.ScanAllJobsWithArchive()
            .Select(t => t.Key ?? t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        relatedTasks = relatedTasks
            .Select(t => t with { Exists = knownKeys.Contains(t.Key) })
            .ToList();
        var payload = new WikiFileHistory(relPath.Replace('\\', '/'), model, meta, commits, relatedTasks);

        // History depends on HEAD (the git side) and the live file's frontmatter
        // (the model/why can change with an uncommitted edit before HEAD moves),
        // so the validator folds both HEAD and the file's mtime.
        var mtime = File.GetLastWriteTimeUtc(full).Ticks;
        var etag = FormatETag("wiki-hist-" + (head ?? "nohead") + "-" + mtime);
        return new WikiHistoryResult(payload, etag);
    }

    private static List<RelatedTask> ReadRelatedTasks(string sidecarPath)
    {
        if (!File.Exists(sidecarPath)) return [];
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            if (!doc.RootElement.TryGetProperty("relatedTasks", out var refs) || refs.ValueKind != JsonValueKind.Array)
                return [];
            return JsonSerializer.Deserialize<List<RelatedTask>>(refs.GetRawText(), TaskJsonFile.ReadOpts) ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Content of a wiki doc as it existed at an earlier commit, plus its ETag.
    /// The bytes are content-addressed (a concrete sha + path never change), so
    /// the read is served from a permanent cache and the ETag is simply the sha.
    /// Returns null when the doc, repo, or revision can't be resolved. Measured
    /// under a telemetry scope.
    /// </summary>
    public WikiRevisionResult? GetWikiRevision(string projectName, string sha, string relPath, GitService git)
    {
        using var _t = GitProcessTelemetry.BeginRequest("wiki/revision", _logger);

        var full = ResolveWikiDocFullPath(projectName, relPath);
        if (full == null) return null;
        var repoRoot = git.ResolveRepoRootForProject(projectName);
        if (string.IsNullOrWhiteSpace(repoRoot)) return null;

        var repoRel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
        var content = git.GetFileAtCommitCached(repoRoot, sha, repoRel);
        if (content == null) return null;

        var payload = new WikiRevisionContent(relPath.Replace('\\', '/'), sha, content);
        return new WikiRevisionResult(payload, FormatETag("wiki-rev-" + sha));
    }

    /// <summary>
    /// Recursively maps a docs directory into wiki tree nodes. Folders with no
    /// document descendants are dropped so the tree only surfaces navigable
    /// content. Hidden entries (dot-prefixed) are skipped.
    /// </summary>
    private static List<WikiTreeNode> BuildTreeNodes(
        DirectoryInfo dir,
        string docsRoot,
        IReadOnlyDictionary<string, WikiTreeMetadata> metadataByRelPath,
        ConcurrentDictionary<string, (long Mtime, long Size, string? Title)> titleCache,
        IReadOnlyDictionary<string, IReadOnlyList<string>> folderOrderByParent)
    {
        var nodes = new List<WikiTreeNode>();

        foreach (var sub in dir.GetDirectories())
        {
            if (sub.Name.StartsWith('.')) continue;
            var subRel = Path.GetRelativePath(docsRoot, sub.FullName).Replace('\\', '/');
            if (IsWikiAppPath(subRel)) continue; // docs/app/ is code contract, not a wiki page
            var children = BuildTreeNodes(sub, docsRoot, metadataByRelPath, titleCache, folderOrderByParent);
            if (children.Count == 0) continue; // prune empty folders
            var rel = Path.GetRelativePath(docsRoot, sub.FullName).Replace('\\', '/');
            nodes.Add(new WikiTreeNode(
                sub.Name,
                StripOrderPrefix(sub.Name),
                rel, "folder", children, null));
        }

        foreach (var file in dir.GetFiles())
        {
            if (file.Name.StartsWith('.')) continue;
            var ext = file.Extension;
            var rel = Path.GetRelativePath(docsRoot, file.FullName).Replace('\\', '/');
            if (!WikiDocExtensions.Contains(ext) || IsWikiCompanionFile(rel) || IsWikiConfigFile(rel)) continue;
            var type = ext.Equals(".md", StringComparison.OrdinalIgnoreCase)
                ? "md"
                : ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    ? "json"
                    : "html";
            var title = ResolveDocTitleCached(titleCache, file, ext)
                ?? StripOrderPrefix(Path.GetFileNameWithoutExtension(file.Name));
            metadataByRelPath.TryGetValue(rel, out var metadata);
            nodes.Add(new WikiTreeNode(
                file.Name, title, rel, type, [], metadata,
                BuildClassification(
                    rel,
                    metadata?.ClassificationStatus,
                    metadata?.ClassificationSupersededBy,
                    metadata?.ClassificationType,
                    metadata?.ClassificationAnalyzedAt)));
        }

        var dirRel = Path.GetRelativePath(docsRoot, dir.FullName).Replace('\\', '/');
        if (dirRel == ".") dirRel = string.Empty;
        var orderIndex = BuildFolderOrderIndex(folderOrderByParent, dirRel);
        nodes.Sort((a, b) => CompareTreeNodes(a, b, orderIndex));
        return nodes;
    }

    /// <summary>
    /// Sniffs a doc's title, memoized by (path, mtime, size). A cache hit returns
    /// the previously-sniffed title without opening the file - the read that
    /// dominated tree building. The actual read (on a miss) happens inside
    /// <see cref="ExtractDocTitle"/>, which is where the file-read is recorded
    /// against the ambient telemetry scope, so the rollup's file count reflects
    /// only genuine disk work.
    /// </summary>
    private static string? ResolveDocTitleCached(
        ConcurrentDictionary<string, (long Mtime, long Size, string? Title)> cache,
        FileInfo file, string ext)
    {
        var mtime = file.LastWriteTimeUtc.Ticks;
        var size = file.Length;
        if (cache.TryGetValue(file.FullName, out var e) && e.Mtime == mtime && e.Size == size)
            return e.Title;
        var title = ExtractDocTitle(file.FullName, ext);
        cache[file.FullName] = (mtime, size, title);
        return title;
    }

    /// <summary>
    /// Reads adjacent companion sidecars (<c>source.md.meta.json</c>) and
    /// indexes them by the source document they describe. Companion files are
    /// physical files beside the document but are not rendered as separate
    /// navigation rows; their compact summary enriches the source row.
    /// </summary>
    private static IReadOnlyDictionary<string, WikiTreeMetadata> LoadWikiMetadataIndex(string wikiDir)
    {
        var index = new Dictionary<string, WikiTreeMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(wikiDir)) return index;

        foreach (var file in Directory.EnumerateFiles(wikiDir, "*.meta.json", SearchOption.AllDirectories))
        {
            try
            {
                var companionRel = Path.GetRelativePath(wikiDir, file).Replace('\\', '/');
                GitProcessTelemetry.RecordFileRead();
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                TryJsonObject(root, "source", out var source);
                TryJsonObject(root, "classification", out var classification);
                TryJsonObject(root, "report", out var report);
                TryJsonObject(root, "drift", out var drift);
                TryJsonObject(root, "duplicates", out var duplicates);
                TryJsonObject(root, "grading", out var grading);

                var sourceRel = NormalizeMetadataSourcePath(JsonString(source, "path") ?? JsonString(root, "sourcePath"));
                if (sourceRel == null) continue;
                if (!string.Equals(companionRel, sourceRel + ".meta.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                var sourceFull = Path.GetFullPath(Path.Combine(wikiDir, sourceRel));
                if (!File.Exists(sourceFull)) continue;

                var metadata = new WikiTreeMetadata(
                    DocumentMode: JsonString(classification, "documentMode") ?? JsonString(root, "documentMode"),
                    TemporalState: JsonString(classification, "temporalState") ?? JsonString(root, "temporalState"),
                    ImplementationState: JsonString(classification, "implementationState") ?? JsonString(root, "implementationState"),
                    DriftGrade: JsonString(drift, "grade"),
                    HasDrift: JsonBool(drift, "hasDrift"),
                    DriftScore: JsonDouble(drift, "score"),
                    Quality: ExtractQuality(root),
                    DuplicateSuspected: JsonBool(duplicates, "suspected"),
                    DuplicateGroupSize: JsonInt(duplicates, "groupSize"),
                    ReportPath: NormalizeMetadataSourcePath(JsonString(report, "path") ?? JsonString(root, "reportPath"))
                        ?? sourceRel + ".report.html",
                    Summary: JsonString(drift, "summary") ?? JsonString(root, "summary"),
                    CompanionPath: companionRel,
                    SourceChangedSinceReview: SourceChangedSinceReview(root, sourceFull),
                    FindingsCount: JsonArrayLength(root, "findings"),
                    GradingGrade: JsonString(grading, "grade"),
                    GradingAssessment: JsonString(grading, "assessment"),
                    GradedAt: JsonString(grading, "gradedAt"),
                    GradingModel: JsonString(grading, "model"),
                    ClassificationStatus: JsonString(classification, "status"),
                    ClassificationSupersededBy: JsonString(classification, "supersededBy"),
                    ClassificationType: JsonString(classification, "type"),
                    ClassificationAnalyzedAt: JsonString(classification, "analyzedAt"));
                index[sourceRel] = metadata;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "ProjectDocsService: unreadable wiki metadata record ignored.");
            }
        }

        return index;
    }

    /// <summary>
    /// Hidden wiki paths - any dot-prefixed path segment (e.g. <c>.curator/…</c>,
    /// <c>.obsidian/…</c>, <c>.gitkeep</c>) - are config/tooling sidecars, not
    /// pages. The tree builder skips them, so every other doc-listing surface
    /// (pulse, feed, grading input) must skip them too or Pulse would count
    /// pages the tree never shows.
    /// </summary>
    private static bool IsHiddenWikiPath(string relPath)
    {
        foreach (var segment in relPath.Split('/'))
            if (segment.StartsWith('.')) return true;
        return false;
    }

    private static bool IsWikiCompanionFile(string relPath) =>
        relPath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
        || relPath.EndsWith(".report.html", StringComparison.OrdinalIgnoreCase)
        || relPath.EndsWith(".report.htm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The docs/app/ code-contract subtree - JSON schemas, in-app help bodies,
    /// and wiki config (<c>app/config/home.json</c>, <c>app/config/wiki-order.json</c>)
    /// - is machine contract, not knowledge content. Every reading surface (tree,
    /// folder, search, pulse, grading) skips it the same way it skips dot-prefixed
    /// and companion files; direct-path serving still reaches it by explicit path.
    /// </summary>
    internal static bool IsWikiAppPath(string relPath)
    {
        var rel = relPath.Replace('\\', '/');
        return rel.Equals(WikiAppRel, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(WikiAppRel + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Wiki configuration and code-contract paths, not pages: everything under
    /// <c>docs/app/</c> (which includes the curated home config and the saved
    /// category order). Hidden from every reading surface the same way companion
    /// sidecars are hidden.
    /// </summary>
    private static bool IsWikiConfigFile(string relPath) =>
        IsWikiAppPath(relPath)
        || relPath.Equals(WikiHomeRel, StringComparison.OrdinalIgnoreCase)
        || relPath.Equals(WikiFolderOrderRel, StringComparison.OrdinalIgnoreCase)
        // Per-folder workbench registrations are machinery (read by the
        // workbench catalogue), not reading documents - keep them out of the
        // tree like the meta companions.
        || Path.GetFileName(relPath).Equals("workbench.json", StringComparison.OrdinalIgnoreCase);

    // ---- Wiki page classification (consolidation-analysis metadata) ----

    /// <summary>
    /// Projects the sidecar classification fields onto a page node, falling back
    /// to the per-folder default type when the sidecar carries no classification.
    /// A page with neither sidecar fields nor a folder default has no
    /// classification (null), so the UI renders nothing rather than noise.
    /// </summary>
    internal static WikiClassification? BuildClassification(
        string relPath, string? status, string? supersededBy, string? type, string? analyzedAt)
    {
        status = NormalizeClassificationValue(status);
        supersededBy = NormalizeClassificationValue(supersededBy);
        type = NormalizeClassificationValue(type) ?? DefaultClassificationType(relPath);
        analyzedAt = NormalizeClassificationValue(analyzedAt);
        if (status == null && supersededBy == null && type == null && analyzedAt == null) return null;
        return new WikiClassification(status, supersededBy, type, analyzedAt);
    }

    /// <summary>
    /// Default document type for pages without a sidecar classification, derived
    /// from the docs folder they live in. Covers the uniform generated families
    /// (common-problems, proposals, ...) so they never need per-page sidecars;
    /// folders without an agreed default return null.
    /// </summary>
    internal static string? DefaultClassificationType(string relPath)
    {
        var rel = relPath.Replace('\\', '/').TrimStart('/');
        // The docs tree is organised by theme (start/system/concepts/operations/
        // quality), so the uniform generated families are matched by the folder
        // segment they carry anywhere in the path, not by the top-level folder.
        if (rel.Contains("architecture/decisions/", StringComparison.OrdinalIgnoreCase)) return "adr";
        if (rel.Contains("common-problems/", StringComparison.OrdinalIgnoreCase)) return "generiert";
        if (rel.Contains("proposals/", StringComparison.OrdinalIgnoreCase)) return "proposal";
        if (rel.Contains("system/domains/", StringComparison.OrdinalIgnoreCase)) return "domain-map";
        if (rel.Contains("system/contracts/", StringComparison.OrdinalIgnoreCase)) return "contract";
        // Standalone mockups live under docs/concepts/mockups/; the active
        // families were promoted to top-level docs/concepts/<family>/ folders in
        // the 2026-07 migration and no longer carry a "/mockups/" path segment.
        if (rel.Contains("/mockups/", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("concepts/project-urls/", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("concepts/project-overview-dashboard/", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("concepts/task-processing-pipeline/", StringComparison.OrdinalIgnoreCase)
            || rel.Contains("concepts/task-detail-header-state-actions/", StringComparison.OrdinalIgnoreCase))
            return "mockup";
        return null;
    }

    private static string? NormalizeClassificationValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Classification for one folder-overview page row: read from the page's own
    /// adjacent companion (a single file probe, no recursive sidecar scan), with
    /// the same folder-default fallback as the tree.
    /// </summary>
    private static WikiClassification? ReadPageClassification(string pageFullPath, string relPath)
    {
        string? status = null, supersededBy = null, type = null, analyzedAt = null;
        var companion = pageFullPath + ".meta.json";
        if (File.Exists(companion))
        {
            try
            {
                GitProcessTelemetry.RecordFileRead();
                using var doc = JsonDocument.Parse(File.ReadAllText(companion));
                if (TryJsonObject(doc.RootElement, "classification", out var classification))
                {
                    status = JsonString(classification, "status");
                    supersededBy = JsonString(classification, "supersededBy");
                    type = JsonString(classification, "type");
                    analyzedAt = JsonString(classification, "analyzedAt");
                }
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "ProjectDocsService: unreadable companion during folder classification read.");
            }
        }
        return BuildClassification(relPath, status, supersededBy, type, analyzedAt);
    }

    private static string? NormalizeMetadataSourcePath(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        var rel = sourcePath.Trim().Replace('\\', '/');
        while (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];
        if (rel.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) rel = rel[5..];
        if (rel.Length == 0 || rel.Contains("..", StringComparison.Ordinal) || rel.StartsWith("/", StringComparison.Ordinal))
            return null;
        return rel;
    }

    private static bool TryJsonObject(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? JsonString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool? JsonBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? JsonInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    private static double? JsonDouble(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;
    }

    private static int? JsonArrayLength(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : null;
    }

    private static bool? SourceChangedSinceReview(JsonElement root, string sourceFull)
    {
        var expectedHash = ExtractReviewSourceHash(root);
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return TryJsonObject(root, "review", out var review)
                ? JsonBool(review, "sourceChangedSinceReview")
                : null;
        }

        try
        {
            GitProcessTelemetry.RecordFileRead();
            using var stream = File.OpenRead(sourceFull);
            var currentHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return !string.Equals(currentHash, expectedHash.Trim().ToLowerInvariant(), StringComparison.Ordinal);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: unable to compare wiki companion source fingerprint.");
            return null;
        }
    }

    private static string? ExtractReviewSourceHash(JsonElement root)
    {
        if (TryJsonObject(root, "review", out var review)
            && TryJsonObject(review, "sourceFingerprint", out var reviewFingerprint))
        {
            var reviewHash = JsonString(reviewFingerprint, "hash");
            if (!string.IsNullOrWhiteSpace(reviewHash)) return reviewHash;
        }

        if (TryJsonObject(root, "source", out var source)
            && TryJsonObject(source, "fingerprint", out var sourceFingerprint))
        {
            return JsonString(sourceFingerprint, "hash");
        }

        return null;
    }

    private static string? ExtractQuality(JsonElement root)
    {
        var explicitQuality = JsonString(root, "quality");
        if (!string.IsNullOrWhiteSpace(explicitQuality)) return explicitQuality;
        if (!TryJsonObject(root, "axes", out var axes)) return null;

        var values = axes.EnumerateObject()
            .Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim().ToLowerInvariant())
            .ToList();
        if (values.Count == 0) return null;
        if (values.Any(v => v is "low" or "poor")) return "low";
        if (values.Count(v => v is "high" or "strong") >= values.Count - 1) return "high";
        return "medium";
    }

    /// <summary>
    /// Folders before files; folders in the saved drag-order when one is stored
    /// for this sibling group (unknown folders behind); then by numeric order
    /// prefix; then name.
    /// </summary>
    private static int CompareTreeNodes(
        WikiTreeNode a, WikiTreeNode b, IReadOnlyDictionary<string, int> folderOrderIndex)
    {
        var aFolder = a.Type == "folder";
        var bFolder = b.Type == "folder";
        if (aFolder != bFolder) return aFolder ? -1 : 1;

        if (aFolder)
        {
            var cmp = CompareBySavedFolderOrder(a.Name, b.Name, folderOrderIndex);
            if (cmp != 0) return cmp;
        }

        var ao = OrderPrefixValue(a.Name);
        var bo = OrderPrefixValue(b.Name);
        if (ao != bo) return ao.CompareTo(bo);

        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Saved drag-order comparison for two sibling folder names: listed folders
    /// sort by their stored position, unlisted folders behind (falling through
    /// to the caller's prefix/name ordering).
    /// </summary>
    private static int CompareBySavedFolderOrder(
        string aName, string bName, IReadOnlyDictionary<string, int> folderOrderIndex)
    {
        if (folderOrderIndex.Count == 0) return 0;
        var ai = folderOrderIndex.TryGetValue(aName, out var av) ? av : int.MaxValue;
        var bi = folderOrderIndex.TryGetValue(bName, out var bv) ? bv : int.MaxValue;
        return ai.CompareTo(bi);
    }

    // -------- Wiki folder order (saved category drag-order) --------

    private static readonly IReadOnlyDictionary<string, int> EmptyFolderOrderIndex =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads <c>docs/app/config/wiki-order.json</c>: the persisted display order of sibling
    /// category folders, keyed by parent folder rel path ("" = docs root).
    /// Missing or malformed files degrade to an empty map (= the default
    /// prefix/name ordering), mirroring how <c>home.json</c> fails open.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadWikiFolderOrder(string wikiDir)
    {
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(wikiDir, WikiFolderOrderRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return empty;
        try
        {
            GitProcessTelemetry.RecordFileRead();
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return empty;
            if (!doc.RootElement.TryGetProperty("folderOrder", out var map)
                || map.ValueKind != JsonValueKind.Object)
                return empty;

            var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in map.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Array) continue;
                var names = prop.Value.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => (e.GetString() ?? string.Empty).Trim())
                    .Where(n => n.Length > 0)
                    .ToList();
                var key = prop.Name.Replace('\\', '/').Trim().Trim('/');
                result[key] = names;
            }
            return result;
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: unreadable wiki folder-order file ignored; default ordering applies.");
            return empty;
        }
    }

    /// <summary>Name → saved position for one sibling group; empty when no order is stored.</summary>
    private static IReadOnlyDictionary<string, int> BuildFolderOrderIndex(
        IReadOnlyDictionary<string, IReadOnlyList<string>> folderOrderByParent,
        string parentRel)
    {
        if (!folderOrderByParent.TryGetValue(parentRel, out var names) || names.Count == 0)
            return EmptyFolderOrderIndex;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Count; i++) index.TryAdd(names[i], i);
        return index;
    }

    /// <summary>
    /// Persists the display order of the category folders directly under
    /// <paramref name="parentRelPath"/> ("" = the docs root) into
    /// <c>docs/app/config/wiki-order.json</c>, beside the other wiki metadata. Orders for
    /// other parents are preserved. The endpoint commits the file like every
    /// other wiki mutation; folders missing from the stored list keep sorting
    /// behind the listed ones in the default prefix/name order. Returns the
    /// order file's absolute path for that commit.
    /// </summary>
    public WikiMutationResult SetWikiFolderOrder(
        string projectName, string? parentRelPath, IReadOnlyList<string> orderedNames)
    {
        var baseDir = ResolveBaseDir(projectName);
        if (baseDir == null) return WikiMutationResult.Fail("Unknown project.");

        var wikiDir = Path.GetFullPath(Path.Combine(baseDir, WikiRel));
        var parent = (parentRelPath ?? string.Empty).Replace('\\', '/').Trim().Trim('/');
        if (parent.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(parent))
            return WikiMutationResult.Fail("Invalid parent path.");

        var parentFull = parent.Length == 0 ? wikiDir : Path.GetFullPath(Path.Combine(wikiDir, parent));
        var rootWithSep = wikiDir.EndsWith(Path.DirectorySeparatorChar) ? wikiDir : wikiDir + Path.DirectorySeparatorChar;
        if (parent.Length > 0 && !parentFull.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            return WikiMutationResult.Fail("Invalid parent path.");
        if (!Directory.Exists(parentFull))
            return WikiMutationResult.Fail("Parent folder not found.");
        if (orderedNames.Count > 500)
            return WikiMutationResult.Fail("Too many folder names.");

        var cleaned = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in orderedNames)
        {
            var name = (raw ?? string.Empty).Trim();
            if (name.Length == 0)
                return WikiMutationResult.Fail("Folder names must not be empty.");
            if (name.Contains('/') || name.Contains('\\') || name.StartsWith('.'))
                return WikiMutationResult.Fail($"'{name}' is not a valid folder name.");
            if (seen.Add(name)) cleaned.Add(name);
        }

        var merged = new Dictionary<string, IReadOnlyList<string>>(
            LoadWikiFolderOrder(wikiDir), StringComparer.OrdinalIgnoreCase)
        {
            [parent] = cleaned,
        };
        var payload = new Dictionary<string, object>
        {
            ["schemaVersion"] = "wiki-folder-order/v1",
            ["folderOrder"] = merged
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };

        var path = Path.Combine(wikiDir, WikiFolderOrderRel.Replace('/', Path.DirectorySeparatorChar));
        // The order file now lives under docs/app/config/; ensure that
        // code-contract folder exists before the first write in a fresh repo.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        // The docs signature covers the order file, but timestamp resolution can
        // hide a same-tick rewrite - drop the memo so the next tree read rebuilds.
        InvalidateWikiTreeCache();
        return WikiMutationResult.Ok(path);
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
            return WikiMutationResult.Fail("Only .md, .html, or .json pages are allowed.");
        var full = ResolveWikiPath(projectName, relPath, requireDoc: true);
        if (full == null) return WikiMutationResult.Fail("Invalid path.");
        if (File.Exists(full)) return WikiMutationResult.Fail("A page with that name already exists.");

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var seed = content ?? DefaultPageSeed(relPath, ext);
        File.WriteAllText(full, seed);

        // Metadata convention (2026-07): stamp a minimal classification sidecar at
        // creation so a hand-authored page is born classified (type = folder
        // default, status = aktuell, analyzedAt = today) rather than surfacing as
        // an "unclassified page" warning on the pulse dashboard.
        var wikiDir = Path.GetFullPath(Path.Combine(ResolveBaseDir(projectName)!, WikiRel));
        var docsRel = Path.GetRelativePath(wikiDir, full).Replace('\\', '/');
        var title = StripOrderPrefix(Path.GetFileNameWithoutExtension(relPath));
        var companion = new WikiCompanionStore().WriteCreationClassification(
            wikiDir, docsRel, title, seed, DefaultClassificationType(docsRel), DateTime.UtcNow);

        return WikiMutationResult.Ok(full, new[] { companion.CompanionAbsPath });
    }

    private static string DefaultPageSeed(string relPath, string ext)
    {
        var title = StripOrderPrefix(Path.GetFileNameWithoutExtension(relPath));
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase))
            return $"# {title}\n\n";
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return "{\n"
                + $"  \"title\": \"{title}\",\n"
                + "  \"summary\": \"\",\n"
                + "  \"drift\": { \"grade\": \"unknown\", \"hasDrift\": false }\n"
                + "}\n";
        return $"<!doctype html>\n<html>\n<head><meta charset=\"utf-8\"><title>{title}</title></head>\n<body>\n<h1>{title}</h1>\n</body>\n</html>\n";
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
        foreach (var f in Directory.EnumerateFiles(wikiDir, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(f);
            if (!WikiDocExtensions.Contains(ext)) continue;
            var fi = new FileInfo(f);
            var rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (IsHiddenWikiPath(rel) || IsWikiCompanionFile(rel) || IsWikiConfigFile(rel)) continue;
            results.Add(new WikiFileEntry(
                Name: Path.GetFileName(f),
                RelPath: rel,
                Title: ExtractDocTitle(f, ext) ?? Path.GetFileNameWithoutExtension(f),
                UpdatedAt: fi.LastWriteTimeUtc,
                Size: fi.Length));
        }
        results.Sort((a, b) => string.Compare(a.RelPath, b.RelPath, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    /// <summary>
    /// Cheap label sniff for a nicer title than the file name. Opens the file, so
    /// it records one read against the ambient telemetry scope; the tree builder
    /// only calls it on a title-cache miss, so the recorded count reflects genuine
    /// disk work rather than every doc-node on every request.
    /// </summary>
    private static string? ExtractDocTitle(string path, string extension)
    {
        GitProcessTelemetry.RecordFileRead();
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            return ExtractJsonTitle(path);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            return ExtractHtmlTitle(path);
        return ExtractFirstHeading(path);
    }

    /// <summary>
    /// Cheap first-H1 sniff for Markdown/HTML labels. Reads at most the first
    /// handful of lines so listing a large tree stays fast.
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

    private static string? ExtractJsonTitle(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString();
            if (doc.RootElement.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
                return label.GetString();
            if (doc.RootElement.TryGetProperty("document", out var document)
                && document.ValueKind == JsonValueKind.Object
                && document.TryGetProperty("title", out var docTitle)
                && docTitle.ValueKind == JsonValueKind.String)
                return docTitle.GetString();
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: Unreadable JSON title: fall back to the file name in the caller.");
        }

        return null;
    }

    private static string? ExtractHtmlTitle(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            var h1 = Regex.Match(text, @"<h1[^>]*>(?<title>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (h1.Success) return StripHtml(h1.Groups["title"].Value);
            var title = Regex.Match(text, @"<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (title.Success) return StripHtml(title.Groups["title"].Value);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: Unreadable HTML title: fall back to the file name in the caller.");
        }

        return null;
    }

    // Entities must decode after tag-stripping: a heading like
    // "Probleme &amp; Maßnahmen" otherwise shows its raw entity in the tree.
    private static string StripHtml(string text) =>
        System.Net.WebUtility.HtmlDecode(
            Regex.Replace(text, "<.*?>", "", RegexOptions.Singleline)).Trim();

    // -------- Wiki folder view (one directory level for the folder overview) --------

    // Page extensions the folder overview lists: markdown plus HTML concept
    // pages. JSON metadata pages are deliberately omitted here - the folder
    // card surface is a reading surface and its DTO contract only knows
    // fileType "md" | "html" (null for folders).
    private static readonly HashSet<string> WikiFolderPageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".md", ".html", ".htm" };

    /// <summary>
    /// One directory level of the wiki for the folder-overview surface: the
    /// folder's own identity plus every direct child, folders first then pages,
    /// each alphabetical. Pages carry a sniffed title (first H1 / frontmatter
    /// title for markdown, <c>&lt;title&gt;</c> for HTML, file name fallback)
    /// and a plain-text summary (first text paragraph, markup stripped, max 240
    /// chars); folders carry a non-recursive child count instead. An empty
    /// <paramref name="relPath"/> lists the wiki root. Returns null when the
    /// project is unknown, the path is unsafe (same traversal guard as the
    /// file endpoints), or the folder does not exist.
    /// </summary>
    public WikiFolderView? GetWikiFolder(string projectName, string? relPath)
    {
        var baseDir = ResolveBaseDir(projectName);
        if (baseDir == null) return null;

        using var _t = GitProcessTelemetry.BeginRequest("wiki/folder", _logger);

        var root = Path.GetFullPath(Path.Combine(baseDir, WikiRel));
        var rel = (relPath ?? string.Empty).Replace('\\', '/').Trim().Trim('/');

        string full;
        if (rel.Length == 0)
        {
            full = root;
        }
        else
        {
            if (rel.Contains("..", StringComparison.Ordinal)) return null;
            if (Path.IsPathRooted(rel)) return null;
            full = Path.GetFullPath(Path.Combine(root, rel));
            // Append a separator to the root so "docs-other/" can't satisfy the prefix.
            var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)) return null;
        }
        if (!Directory.Exists(full)) return null;
        if (IsWikiAppPath(rel)) return null; // docs/app/ is code contract, never a wiki folder

        var dir = new DirectoryInfo(full);
        var children = new List<WikiFolderChild>();

        foreach (var sub in dir.GetDirectories())
        {
            if (sub.Name.StartsWith('.')) continue;
            var subRel = Path.GetRelativePath(root, sub.FullName).Replace('\\', '/');
            if (IsWikiAppPath(subRel)) continue; // hide docs/app/ from the folder overview
            if (!HasWikiPageDescendant(sub)) continue; // prune empty folders, like the tree
            children.Add(new WikiFolderChild(
                Name: sub.Name,
                RelPath: subRel,
                Kind: "folder",
                FileType: null,
                Title: StripOrderPrefix(sub.Name),
                Summary: null,
                UpdatedAt: sub.LastWriteTimeUtc,
                Size: null,
                ChildCount: CountDirectFolderChildren(sub)));
        }

        foreach (var file in dir.GetFiles())
        {
            if (!IsWikiFolderPage(file)) continue;
            var fileRel = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
            children.Add(new WikiFolderChild(
                Name: file.Name,
                RelPath: fileRel,
                Kind: "page",
                FileType: file.Extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ? "md" : "html",
                Title: ExtractWikiPageTitle(file.FullName, file.Extension)
                    ?? StripOrderPrefix(Path.GetFileNameWithoutExtension(file.Name)),
                Summary: ExtractWikiPageSummary(file.FullName, file.Extension),
                UpdatedAt: file.LastWriteTimeUtc,
                Size: file.Length,
                ChildCount: null,
                Classification: ReadPageClassification(file.FullName, fileRel)));
        }

        // Same saved category drag-order as the tree; unlisted folders keep the
        // alphabetical fallback behind the listed ones. Pages stay alphabetical.
        var orderIndex = BuildFolderOrderIndex(LoadWikiFolderOrder(root), rel);
        children.Sort((a, b) =>
        {
            var aFolder = a.Kind == "folder";
            var bFolder = b.Kind == "folder";
            if (aFolder != bFolder) return aFolder ? -1 : 1;
            if (aFolder)
            {
                var cmp = CompareBySavedFolderOrder(a.Name, b.Name, orderIndex);
                if (cmp != 0) return cmp;
            }
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new WikiFolderView(rel, dir.Name, children);
    }

    private static bool IsWikiFolderPage(FileInfo file) =>
        !file.Name.StartsWith('.')
        && WikiFolderPageExtensions.Contains(file.Extension)
        && !IsWikiCompanionFile(file.Name);

    /// <summary>Any page anywhere below this folder? Used to prune folders with no navigable content.</summary>
    private static bool HasWikiPageDescendant(DirectoryInfo dir)
    {
        try
        {
            return dir.EnumerateFiles("*", SearchOption.AllDirectories).Any(IsWikiFolderPage);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: unreadable folder while probing for wiki pages; treating as empty.");
            return false;
        }
    }

    /// <summary>Direct pages + direct non-empty subfolders. Deliberately not recursive.</summary>
    private static int CountDirectFolderChildren(DirectoryInfo dir)
    {
        var count = dir.GetFiles().Count(IsWikiFolderPage);
        count += dir.GetDirectories().Count(d => !d.Name.StartsWith('.') && HasWikiPageDescendant(d));
        return count;
    }

    /// <summary>
    /// Display title for a wiki page in the folder overview. Markdown: first
    /// <c># </c> heading outside the frontmatter, else the frontmatter
    /// <c>title:</c>. HTML: the <c>&lt;title&gt;</c> tag, else the first
    /// <c>&lt;h1&gt;</c>. Null (caller falls back to the file name) when the
    /// file is unreadable or carries neither.
    /// </summary>
    internal static string? ExtractWikiPageTitle(string path, string extension)
    {
        try
        {
            GitProcessTelemetry.RecordFileRead();
            var text = File.ReadAllText(path);
            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in StripWikiFrontmatter(text).Split('\n'))
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("# ", StringComparison.Ordinal))
                        return trimmed[2..].Trim();
                }
                return FrontmatterScalar(text, "title");
            }

            var title = Regex.Match(text, @"<title[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (title.Success)
            {
                var t = System.Net.WebUtility.HtmlDecode(StripHtml(title.Groups["title"].Value));
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
            var h1 = Regex.Match(text, @"<h1[^>]*>(?<title>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (h1.Success)
            {
                var t = System.Net.WebUtility.HtmlDecode(StripHtml(h1.Groups["title"].Value));
                if (!string.IsNullOrWhiteSpace(t)) return t;
            }
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: unreadable page title: fall back to the file name in the caller.");
        }
        return null;
    }

    /// <summary>
    /// First text paragraph of a wiki page as a plain-text summary: markdown
    /// syntax / HTML tags stripped, whitespace collapsed, hard-capped at 240
    /// characters. Null when the page has no prose (or is unreadable).
    /// </summary>
    private static string? ExtractWikiPageSummary(string path, string extension)
    {
        try
        {
            GitProcessTelemetry.RecordFileRead();
            var text = File.ReadAllText(path);
            var summary = extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
                ? FirstMarkdownParagraph(text)
                : FirstHtmlParagraph(text);
            return TruncateSummary(summary);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: unreadable page summary; omitting it.");
            return null;
        }
    }

    /// <summary>Body of a markdown doc without its leading YAML frontmatter block.</summary>
    internal static string StripWikiFrontmatter(string text)
    {
        var m = WikiFrontmatterRegex.Match(text);
        return m.Success ? text[(m.Index + m.Length)..] : text;
    }

    /// <summary>
    /// First contiguous run of prose lines in a markdown body - headings, code
    /// fences, tables, and HTML comments are skipped; inline markdown (links,
    /// images, emphasis, inline code) is stripped down to its text.
    /// </summary>
    private static string? FirstMarkdownParagraph(string text)
    {
        var lines = StripWikiFrontmatter(text).Split('\n');
        var paragraph = new List<string>();
        var inFence = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence) continue;
            var isProse = trimmed.Length > 0
                && !trimmed.StartsWith('#')
                && !trimmed.StartsWith('|')
                && !trimmed.StartsWith("<!--", StringComparison.Ordinal)
                && !trimmed.StartsWith("![", StringComparison.Ordinal)
                && !trimmed.StartsWith("---", StringComparison.Ordinal);
            if (isProse)
            {
                paragraph.Add(trimmed.TrimStart('>', ' ').TrimStart('-', '*', ' '));
            }
            else if (paragraph.Count > 0)
            {
                break; // paragraph ended
            }
        }
        if (paragraph.Count == 0) return null;

        var joined = string.Join(" ", paragraph);
        joined = Regex.Replace(joined, @"!\[[^\]]*\]\([^)]*\)", "");        // images
        joined = Regex.Replace(joined, @"\[([^\]]*)\]\([^)]*\)", "$1");      // links -> text
        joined = Regex.Replace(joined, @"`([^`]*)`", "$1");                  // inline code
        joined = Regex.Replace(joined, @"(\*\*|__|\*|~~)", "");              // emphasis markers
        joined = StripHtml(joined);                                          // inline HTML
        return joined;
    }

    /// <summary>First <c>&lt;p&gt;</c> with text, else the tag-stripped body text.</summary>
    private static string? FirstHtmlParagraph(string text)
    {
        var cleaned = Regex.Replace(text, @"<script[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"<style[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"<head[^>]*>.*?</head>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        foreach (Match p in Regex.Matches(cleaned, @"<p[^>]*>(?<body>.*?)</p>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var candidate = System.Net.WebUtility.HtmlDecode(StripHtml(p.Groups["body"].Value));
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;
        }

        var fallback = System.Net.WebUtility.HtmlDecode(StripHtml(cleaned));
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    /// <summary>Collapses whitespace and hard-caps the summary at 240 characters.</summary>
    private static string? TruncateSummary(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return null;
        var collapsed = Regex.Replace(summary, @"\s+", " ").Trim();
        if (collapsed.Length == 0) return null;
        return collapsed.Length <= 240 ? collapsed : collapsed[..240].TrimEnd();
    }

    // -------- Wiki home (curated landing sections from docs/app/config/home.json) --------

    /// <summary>
    /// The curated wiki home sections read from <c>docs/app/config/home.json</c>.
    /// Every configured link is kept and annotated with an <c>exists</c> flag
    /// (checked against the docs tree with the standard traversal guard) so the
    /// UI can render a dead link visibly instead of silently dropping it. A
    /// missing or malformed <c>home.json</c> degrades to empty sections, never
    /// an error; null only when the project itself is unknown.
    /// </summary>
    public WikiHomeView? GetWikiHome(string projectName)
    {
        var baseDir = ResolveBaseDir(projectName);
        if (baseDir == null) return null;

        var homePath = Path.Combine(baseDir, WikiRel, WikiHomeRel.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(homePath)) return new WikiHomeView([]);

        try
        {
            GitProcessTelemetry.RecordFileRead();
            using var doc = JsonDocument.Parse(File.ReadAllText(homePath));
            var sections = new List<WikiHomeSection>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sections", out var rawSections)
                && rawSections.ValueKind == JsonValueKind.Array)
            {
                foreach (var rawSection in rawSections.EnumerateArray())
                {
                    if (rawSection.ValueKind != JsonValueKind.Object) continue;
                    var links = new List<WikiHomeLink>();
                    if (rawSection.TryGetProperty("links", out var rawLinks) && rawLinks.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rawLink in rawLinks.EnumerateArray())
                        {
                            if (rawLink.ValueKind != JsonValueKind.Object) continue;
                            var rel = JsonString(rawLink, "relPath")?.Replace('\\', '/').Trim().TrimStart('/');
                            if (string.IsNullOrWhiteSpace(rel)) continue;
                            var target = ResolveWikiPath(projectName, rel, requireDoc: false);
                            links.Add(new WikiHomeLink(
                                RelPath: rel,
                                Label: JsonString(rawLink, "label") ?? rel,
                                Note: JsonString(rawLink, "note"),
                                Exists: target != null && File.Exists(target)));
                        }
                    }
                    sections.Add(new WikiHomeSection(JsonString(rawSection, "title") ?? "", links));
                }
            }
            return new WikiHomeView(sections);
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "ProjectDocsService: malformed wiki home.json; serving empty sections.");
            return new WikiHomeView([]);
        }
    }

    // -------- Architecture decisions --------

    public ArchitectureOverview? GetArchitectureOverview(string projectName)
    {
        var baseDir = ResolveBaseDir(projectName);
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
/// document node (<c>type</c> = <c>md</c>, <c>html</c>, or <c>json</c>) is a leaf. <see
/// cref="RelPath"/> is the docs-root-relative path; <see cref="Title"/> is the
/// display label (first H1 for docs, order-prefix-stripped name otherwise).
/// <see cref="Metadata"/> is an optional compact summary read from visible
/// adjacent <c>*.meta.json</c> companion records.
/// </summary>
public record WikiTreeMetadata(
    string? DocumentMode,
    string? TemporalState,
    string? ImplementationState,
    string? DriftGrade,
    bool? HasDrift,
    double? DriftScore,
    string? Quality,
    bool? DuplicateSuspected,
    int? DuplicateGroupSize,
    string? ReportPath,
    string? Summary,
    string? CompanionPath,
    bool? SourceChangedSinceReview,
    int? FindingsCount,
    // LLM grading verdict written by a wiki-grading maintenance run (AGT-2051).
    // Kept separate from the deterministic drift fields above.
    string? GradingGrade = null,
    string? GradingAssessment = null,
    string? GradedAt = null,
    string? GradingModel = null,
    // Curated consolidation classification (konsolidierung-analyse 2026-07-18):
    // the status / successor / doc-type fields of the companion's
    // `classification` block. All optional - older sidecars carry none.
    string? ClassificationStatus = null,
    string? ClassificationSupersededBy = null,
    string? ClassificationType = null,
    string? ClassificationAnalyzedAt = null);

/// <summary>
/// Curation classification of one wiki page, projected onto tree and folder
/// rows. <see cref="Status"/> is <c>aktuell</c> / <c>veraltet</c> /
/// <c>ueberholt</c> (null = unclassified); <see cref="SupersededBy"/> is the
/// docs-relative path of the successor page for <c>ueberholt</c> pages;
/// <see cref="Type"/> is the document kind (<c>konzept</c> / <c>adr</c> /
/// <c>contract</c> / <c>domain-map</c> / <c>analyse</c> / <c>runbook</c> /
/// <c>workbench</c> / <c>mockup</c> / <c>proposal</c> / <c>generiert</c> /
/// <c>index</c>), either from the sidecar or from the per-folder default;
/// <see cref="AnalyzedAt"/> is the consolidation-analysis date (ISO date).
/// </summary>
public record WikiClassification(
    string? Status,
    string? SupersededBy,
    string? Type,
    string? AnalyzedAt);

public record WikiTreeNode(
    string Name,
    string Title,
    string? RelPath,
    string Type,
    List<WikiTreeNode> Children,
    WikiTreeMetadata? Metadata,
    // Page nodes only: the curated classification (sidecar first, folder-default
    // type as fallback); null for folders and unclassified pages.
    WikiClassification? Classification = null);

/// <summary>The physical docs/ folder tree exposed to the wiki UI.</summary>
public record WikiTree(string ProjectName, string BaseDir, bool Exists, List<WikiTreeNode> Root);

/// <summary>
/// A cached wiki payload plus the HTTP entity tag the GET endpoint uses to
/// answer <c>If-None-Match</c> with a 304 when the client already holds the
/// current version (AGT-2013). The <see cref="ETag"/> is a strong, content- or
/// HEAD-derived validator, so a matching one guarantees the payload is current.
/// </summary>
public record WikiTreeResult(WikiTree Tree, string ETag);
public record WikiRecentEditsResult(WikiRecentEdits Edits, string ETag);
public record WikiHistoryResult(WikiFileHistory History, string ETag);
public record WikiRevisionResult(WikiRevisionContent Revision, string ETag);

/// <summary>Content of a wiki doc as it existed at an earlier commit.</summary>
public record WikiRevisionContent(string RelPath, string Sha, string Content);

/// <summary>Outcome of a wiki filesystem mutation (create/move/delete).</summary>
public record WikiMutationResult(bool Success, string? FullPath, string? Error, IReadOnlyList<string>? ExtraPaths = null)
{
    public static WikiMutationResult Ok(string fullPath) => new(true, fullPath, null);
    public static WikiMutationResult Ok(string fullPath, IReadOnlyList<string> extraPaths) => new(true, fullPath, null, extraPaths);
    public static WikiMutationResult Fail(string error) => new(false, null, error);
}

public record WikiSaveResult(bool Success, string? FullPath, bool Changed, string? Error)
{
    public static WikiSaveResult Ok(string fullPath, bool changed) => new(true, fullPath, changed, null);
    public static WikiSaveResult Fail(string error) => new(false, null, false, error);
}

/// <summary>History + provenance payload for one wiki doc.</summary>
public record WikiFileHistory(
    string RelPath,
    string? Model,
    WikiDocMetadata Metadata,
    List<GitCommitInfo> Commits,
    List<RelatedTask> RelatedTasks);

/// <summary>One row in the wiki dashboard's "recent edits" list.</summary>
public record WikiRecentEdit(
    string RelPath,
    string Title,
    string Author,
    DateTime AuthorDateUtc,
    string Sha,
    string ShortSha,
    string Subject);

/// <summary>Recent-edits payload backing the wiki dashboard landing surface.</summary>
public record WikiRecentEdits(string ProjectName, string BaseDir, bool Exists, List<WikiRecentEdit> Edits);

// ---- Wiki Pulse (PULSE-1: the generated wiki landing view) ----

/// <summary>
/// The generated wiki Pulse landing view (PULSE-1): a read-only, non-editable
/// composition of the change feed, the sort-needed inbox, and the deterministic
/// drift grade bar. Each section carries its own availability + reason so a
/// missing docs folder or repository degrades to an empty state, never an error.
/// </summary>
public record WikiPulse(
    string ProjectName,
    string BaseDir,
    bool Exists,
    string GeneratedAtUtc,
    WikiPulseFeed Feed,
    WikiPulseInbox Inbox,
    WikiPulseDrift Drift,
    WikiPulseCritical Critical,
    WikiPulseWarnings Warnings,
    WikiPulseActivity Activity)
{
    /// <summary>Open experiment questions projected into Pulse as a thinking inbox.</summary>
    public WorkbenchCatalogue? Workbenches { get; init; }
}

public record WikiPulseWarnings(bool Available, string? Reason, int Count, List<WikiPulseWarningItem> Items)
{
    public static WikiPulseWarnings Unavailable(string reason) => new(false, reason, 0, []);
}

public record WikiPulseWarningItem(string Kind, string Title, string Detail, string HumanAction, string? RelPath, string? Status);

public record WikiPulseActivity(
    bool Available,
    string? Reason,
    List<WikiPulseLiveRun> Runs)
{
    public static WikiPulseActivity Unavailable(string reason) => new(false, reason, []);
}

public record WikiPulseLiveRun(string TaskKey, string Lane, DateTime StartedAtUtc, int DocsFilesChanged);

/// <summary>
/// Critical-pages section (AGT-2051): pages a wiki-grading run scored C or D,
/// worst first, read from companion <c>grading</c> blocks. The LLM grade
/// supplements the deterministic drift bar. Always available (filesystem read);
/// <see cref="OverallGrade"/> is the worst listed grade or <c>none</c>.
/// </summary>
public record WikiPulseCritical(
    bool Available,
    string? Reason,
    int Count,
    string OverallGrade,
    List<WikiPulseCriticalItem> Items)
{
    public static WikiPulseCritical Unavailable(string reason) =>
        new(false, reason, 0, "none", []);
}

/// <summary>One badly-graded page: its grade, the one-line assessment, the
/// grading model, and a link to its companion report.</summary>
public record WikiPulseCriticalItem(
    string RelPath,
    string Title,
    string Grade,
    string? Assessment,
    string? GradedAt,
    string? Model,
    string? ReportPath,
    string? AreaTitle);

/// <summary>Change-feed section: recently-edited pages, newest first.</summary>
public record WikiPulseFeed(bool Available, string? Reason, List<WikiPulseFeedItem> Items);

/// <summary>
/// One change-feed row: a recently-edited page plus its owning top-level docs
/// folder (the badge) and a task key parsed from frontmatter or the commit
/// subject.
/// </summary>
public record WikiPulseFeedItem(
    string RelPath,
    string Title,
    string Author,
    DateTime AuthorDateUtc,
    string Sha,
    string ShortSha,
    string Subject,
    string? AreaSlug,
    string? AreaTitle,
    string? TaskKey);

/// <summary>Inbox section: loose / unfiled pages that need sorting.</summary>
public record WikiPulseInbox(bool Available, string? Reason, int Count, List<WikiPulseInboxItem> Items);

/// <summary>One unfiled page plus the reason it landed in the inbox.</summary>
public record WikiPulseInboxItem(string RelPath, string Title, string Type, string Reason);

/// <summary>
/// Drift-grading section: the per-top-folder grade bar plus roll-up counts.
/// <see cref="OverallGrade"/> is the worst folder grade (Fresh / Aging / Stale /
/// Empty).
/// </summary>
public record WikiPulseDrift(
    bool Available,
    string? Reason,
    string OverallGrade,
    List<WikiPulseDriftArea> Areas,
    WikiPulseDriftCounts Counts)
{
    public static WikiPulseDrift Unavailable(string reason) =>
        new(false, reason, "Empty", [], new WikiPulseDriftCounts(0, 0, 0, 0));
}

/// <summary>
/// One top-level docs folder's drift grade. <see cref="Grade"/> is the worst
/// page's band; <see cref="WorstCommitCount"/> is that page's
/// code-commits-since-update count.
/// </summary>
public record WikiPulseDriftArea(
    string Slug,
    string Title,
    string Grade,
    int PageCount,
    int GradedPageCount,
    int WorstCommitCount,
    int FreshCount,
    int AgingCount,
    int StaleCount);

/// <summary>Roll-up of how many graded pages fall in each drift band.</summary>
public record WikiPulseDriftCounts(int Fresh, int Aging, int Stale, int Graded);

// ---- Wiki folder view (one directory level) ----

/// <summary>
/// One wiki directory level for the folder-overview surface. <see cref="Path"/>
/// is the docs-root-relative folder path (empty string for the wiki root);
/// children are sorted folders first, then pages, each alphabetical.
/// </summary>
public record WikiFolderView(string Path, string Name, List<WikiFolderChild> Children);

/// <summary>
/// One direct child of a wiki folder. <c>Kind</c> is <c>folder</c> or
/// <c>page</c>; <c>FileType</c> is <c>md</c> / <c>html</c> for pages and null
/// for folders. <c>Summary</c> (pages only) is the first text paragraph,
/// markup-stripped, max 240 chars. <c>ChildCount</c> (folders only) counts
/// direct pages + direct non-empty subfolders, not recursive.
/// </summary>
public record WikiFolderChild(
    string Name,
    string RelPath,
    string Kind,
    string? FileType,
    string Title,
    string? Summary,
    DateTime UpdatedAt,
    long? Size,
    int? ChildCount,
    // Page rows only: the curated classification (sidecar first, folder-default
    // type as fallback); null for folders and unclassified pages.
    WikiClassification? Classification = null);

// ---- Wiki home (curated landing sections) ----

/// <summary>Curated wiki home payload backed by <c>docs/app/config/home.json</c>.</summary>
public record WikiHomeView(List<WikiHomeSection> Sections);
public record WikiHomeSection(string Title, List<WikiHomeLink> Links);

/// <summary>One curated link. <see cref="Exists"/> flags whether the target
/// page is actually on disk so the UI can render dead links visibly.</summary>
public record WikiHomeLink(string RelPath, string Label, string? Note, bool Exists);

public record SecurityMeta(string? LastReviewDate, string? Rating, string? Summary);
public record SecurityFileEntry(string Name, string RelPath, DateTime UpdatedAt, long Size);
public record SecurityOverview(string ProjectName, string BaseDir, bool Exists, SecurityMeta Meta, List<SecurityFileEntry> Files);

public record ArchitectureDecisionSummary(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureDecisionDetail(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureOverview(string ProjectName, string SourceFile, bool Exists, string Preamble, List<ArchitectureDecisionSummary> Decisions);
