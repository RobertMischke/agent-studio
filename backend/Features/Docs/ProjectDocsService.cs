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
    private const string AdrRel = "docs/architecture/decisions/adr-archive.md";
    private const string WikiRel = "docs";

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
        if (EngineeringWorkstreamFrame.IsContentLocked(relPath))
            return WikiSaveResult.Fail(FrameLockMessage(relPath, "overwritten"));
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
            _titleCache);
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
            if (IsWikiCompanionFile(docsRel)) continue;
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
        var payload = new WikiFileHistory(relPath.Replace('\\', '/'), model, meta, commits);

        // History depends on HEAD (the git side) and the live file's frontmatter
        // (the model/why can change with an uncommitted edit before HEAD moves),
        // so the validator folds both HEAD and the file's mtime.
        var mtime = File.GetLastWriteTimeUtc(full).Ticks;
        var etag = FormatETag("wiki-hist-" + (head ?? "nohead") + "-" + mtime);
        return new WikiHistoryResult(payload, etag);
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
        ConcurrentDictionary<string, (long Mtime, long Size, string? Title)> titleCache)
    {
        var nodes = new List<WikiTreeNode>();

        foreach (var sub in dir.GetDirectories())
        {
            if (sub.Name.StartsWith('.')) continue;
            var children = BuildTreeNodes(sub, docsRoot, metadataByRelPath, titleCache);
            if (children.Count == 0) continue; // prune empty folders
            var rel = Path.GetRelativePath(docsRoot, sub.FullName).Replace('\\', '/');
            nodes.Add(new WikiTreeNode(
                sub.Name,
                EngineeringWorkstreamFrame.DisplayTitle(rel) ?? StripOrderPrefix(sub.Name),
                rel, "folder", children, null,
                EngineeringWorkstreamFrame.IsStructural(rel)));
        }

        foreach (var file in dir.GetFiles())
        {
            if (file.Name.StartsWith('.')) continue;
            var ext = file.Extension;
            var rel = Path.GetRelativePath(docsRoot, file.FullName).Replace('\\', '/');
            if (!WikiDocExtensions.Contains(ext) || IsWikiCompanionFile(rel)) continue;
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
                EngineeringWorkstreamFrame.IsStructural(rel)));
        }

        nodes.Sort(CompareTreeNodes);
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
                    FindingsCount: JsonArrayLength(root, "findings"));
                index[sourceRel] = metadata;
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "ProjectDocsService: unreadable wiki metadata record ignored.");
            }
        }

        return index;
    }

    private static bool IsWikiCompanionFile(string relPath) =>
        relPath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
        || relPath.EndsWith(".report.html", StringComparison.OrdinalIgnoreCase)
        || relPath.EndsWith(".report.htm", StringComparison.OrdinalIgnoreCase);

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
    /// Workstream frame root first (pinned as the top wiki element); then folders
    /// before files; then by numeric order prefix; then name.
    /// </summary>
    private static int CompareTreeNodes(WikiTreeNode a, WikiTreeNode b)
    {
        // The Workstream frame is pinned to the top of the tree. Only the frame
        // root matches (a top-level node), so nested siblings keep normal order.
        var aPin = EngineeringWorkstreamFrame.IsFrameRoot(a.RelPath) ? 0 : 1;
        var bPin = EngineeringWorkstreamFrame.IsFrameRoot(b.RelPath) ? 0 : 1;
        if (aPin != bPin) return aPin - bPin;

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
    /// Rejection message for a blocked structural or content mutation of a fixed
    /// Engineering Workstream frame node. Kept in one place so the service and the
    /// endpoints phrase the immutability rule identically.
    /// </summary>
    public static string FrameLockMessage(string relPath, string verb) =>
        $"'{relPath}' is part of the fixed Workstream frame and cannot be {verb}. "
        + "Create or edit subpages under an area folder instead.";

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
        return WikiMutationResult.Ok(full);
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
            if (IsWikiCompanionFile(rel)) continue;
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

    private static string StripHtml(string text) =>
        Regex.Replace(text, "<.*?>", "", RegexOptions.Singleline).Trim();

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
    int? FindingsCount);

public record WikiTreeNode(
    string Name,
    string Title,
    string? RelPath,
    string Type,
    List<WikiTreeNode> Children,
    WikiTreeMetadata? Metadata,
    bool Immutable = false);

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
public record WikiMutationResult(bool Success, string? FullPath, string? Error)
{
    public static WikiMutationResult Ok(string fullPath) => new(true, fullPath, null);
    public static WikiMutationResult Fail(string error) => new(false, null, error);
}

public record WikiSaveResult(bool Success, string? FullPath, bool Changed, string? Error)
{
    public static WikiSaveResult Ok(string fullPath, bool changed) => new(true, fullPath, changed, null);
    public static WikiSaveResult Fail(string error) => new(false, null, false, error);
}

/// <summary>History + provenance payload for one wiki doc.</summary>
public record WikiFileHistory(string RelPath, string? Model, WikiDocMetadata Metadata, List<GitCommitInfo> Commits);

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

public record SecurityMeta(string? LastReviewDate, string? Rating, string? Summary);
public record SecurityFileEntry(string Name, string RelPath, DateTime UpdatedAt, long Size);
public record SecurityOverview(string ProjectName, string BaseDir, bool Exists, SecurityMeta Meta, List<SecurityFileEntry> Files);

public record ArchitectureDecisionSummary(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureDecisionDetail(string Id, string Title, string? Date, string Status, string Body);
public record ArchitectureOverview(string ProjectName, string SourceFile, bool Exists, string Preamble, List<ArchitectureDecisionSummary> Decisions);
