
namespace AgentStudio.Docs;

/// <summary>
/// Project-level docs surface (prototype): security archive +
/// architecture decision browser + a read-only wiki view over the
/// project's <c>docs/</c> tree. See <see cref="ProjectDocsService"/>
/// for storage layout and resolution rules.
/// </summary>
public static class ProjectDocsEndpoints
{
    public static void MapProjectDocsEndpoints(this WebApplication app)
    {
        // ---- Security ----

        app.MapGet("/api/projects/{projectName}/security", (string projectName, ProjectDocsService docs) =>
        {
            var ov = docs.GetSecurityOverview(projectName);
            return ov == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(ov);
        });

        app.MapGet("/api/projects/{projectName}/security/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs) =>
        {
            var content = docs.ReadSecurityFile(projectName, relPath);
            if (content == null) return Results.NotFound(new { error = "File not found or path rejected" });
            return Results.Ok(new { relPath, content });
        });

        app.MapPut("/api/projects/{projectName}/security/files/{**relPath}", async (string projectName, string relPath, HttpRequest req, ProjectDocsService docs) =>
        {
            using var reader = new StreamReader(req.Body);
            var json = await reader.ReadToEndAsync();
            string? content = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("content", out var c))
                    content = c.GetString();
            }
            catch
            {
                return Results.BadRequest(new { error = "Invalid JSON body" });
            }
            if (content == null) return Results.BadRequest(new { error = "content field required" });
            var ok = docs.WriteSecurityFile(projectName, relPath, content);
            return ok ? Results.Ok(new { relPath, saved = true })
                      : Results.BadRequest(new { error = "Write rejected (path unsafe or project unknown)" });
        });

        app.MapPut("/api/projects/{projectName}/security/meta", (string projectName, SecurityMeta meta, ProjectDocsService docs) =>
        {
            var ok = docs.WriteSecurityMeta(projectName, meta);
            return ok ? Results.Ok(new { saved = true })
                      : Results.NotFound(new { error = $"Unknown project '{projectName}'" });
        });

        // ---- Wiki (docs/ tree) ----

        app.MapGet("/api/projects/{projectName}/wiki", (string projectName, ProjectDocsService docs) =>
        {
            var ov = docs.GetWikiOverview(projectName);
            return ov == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(ov);
        });

        // Repository-owned style-guide family. The same applicability result
        // is consumed by intake prompt enrichment, so the Wiki never advertises
        // a guide that the coding run cannot discover.
        app.MapGet("/api/projects/{projectName}/style-guides", (string projectName, ProjectStyleGuideService guides, bool refresh = false) =>
        {
            var catalogue = guides.GetCatalogue(projectName, refresh);
            return catalogue == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(catalogue);
        });

        // Repository-owned experiment Workbenches. The catalogue is discovered by
        // scanning docs/ recursively for workbench.json descriptors (post-2026-07
        // migration the workbench folders are theme-distributed, e.g. under
        // operations/ and quality/); HTML is returned as data and is never
        // executed by the backend origin.
        app.MapGet("/api/projects/{projectName}/workbenches", (string projectName, bool? history, WorkbenchCatalogueService workbenches) =>
        {
            var catalogue = workbenches.List(projectName, history == true);
            return catalogue == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(catalogue);
        });

        app.MapGet("/api/projects/{projectName}/workbenches/{id}", (string projectName, string id, WorkbenchCatalogueService workbenches) =>
        {
            var document = workbenches.Read(projectName, id);
            return document == null
                ? Results.NotFound(new { error = "Workbench not found, invalid, or path rejected" })
                : Results.Ok(document);
        });

        // The physical docs/ folder hierarchy (folders + .md/.html/.json files)
        // that backs the wiki navigation tree. No git is touched here, and a warm
        // cache serves it without opening a file (AGT-2013); the ETag lets a
        // frontend reload skip the payload entirely with a 304. Per-doc commit
        // metadata is still fetched lazily via /history.
        app.MapGet("/api/projects/{projectName}/wiki/tree", (string projectName, ProjectDocsService docs, HttpContext http) =>
        {
            var res = docs.GetWikiTreeResult(projectName);
            return res == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : ConditionalOk(http, res.ETag, res.Tree);
        });

        // Recently-edited wiki pages (page / git author / timestamp), newest
        // first, for the dashboard landing surface. Touches git (one log walk),
        // memoized on the wiki branch HEAD so a warm request skips the walk
        // (AGT-2013); `limit` is clamped server-side. Sits before the /files
        // catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/recent", (string projectName, ProjectDocsService docs, GitService git, HttpContext http, int? limit) =>
        {
            var n = Math.Clamp(limit ?? 12, 1, 50);
            var res = docs.GetWikiRecentEditsResult(projectName, git, n);
            return res == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : ConditionalOk(http, res.ETag, res.Edits);
        });

        // The generated wiki Pulse landing view: change feed + inbox + drift,
        // PULSE-2 warnings/live docs work, and maintenance-run summaries. It is
        // composed server-side so the landing
        // surface costs two git walks instead of the tree + recent + per-doc
        // history fan-out. Sits before the /files catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/pulse", (string projectName, ProjectDocsService docs, GitService git, WorkbenchCatalogueService workbenches, int? feedLimit) =>
        {
            var pulse = docs.GetWikiPulse(projectName, git, feedLimit ?? 12);
            // Pulse is the lifecycle overview, so it deliberately includes
            // settled Workbenches. Explorer keeps its current-only default.
            var catalogue = workbenches.List(projectName, includeHistory: true);
            return pulse == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(pulse with
                {
                    Workbenches = catalogue,
                    Lifecycle = ProjectDocsService.MergeWorkbenchLifecycle(pulse.Lifecycle, catalogue),
                });
        });

        // One directory level of the wiki for the folder-overview surface:
        // direct children (folders first, then pages, each alphabetical) with
        // sniffed titles, plain-text summaries, and folder child counts. An
        // empty relPath lists the wiki root. Sits before the /files catch-all
        // for path precedence, like its sibling routes.
        app.MapGet("/api/projects/{projectName}/wiki/folder/{**relPath}", (string projectName, string? relPath, ProjectDocsService docs) =>
        {
            var folder = docs.GetWikiFolder(projectName, relPath);
            return folder == null
                ? Results.NotFound(new { error = "Folder not found or path rejected" })
                : Results.Ok(folder);
        });

        // Lexical wiki search (BM25 over title/headings/body) with an optional
        // fail-open semantic query-expansion layer (semantic=true). The limit
        // is clamped server-side; a blank query is a 400, an unknown project a
        // 404. Sits before the /files catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/search", async (string projectName, string? q, bool? semantic, int? limit, WikiSearchService search, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
                return Results.BadRequest(new { error = "q is required" });
            var res = await search.SearchAsync(projectName, q.Trim(), semantic == true, Math.Clamp(limit ?? 20, 1, 50), ct);
            return res == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(res);
        });

        // Curated wiki home sections from docs/app/config/home.json. Missing or
        // malformed file degrades to empty sections; configured links are kept
        // and annotated with an exists flag instead of being dropped. Sits
        // before the /files catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/home", (string projectName, ProjectDocsService docs) =>
        {
            var home = docs.GetWikiHome(projectName);
            return home == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(home);
        });

        app.MapGet("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs) =>
        {
            var file = docs.ReadWikiFile(projectName, relPath);
            return file == null
                ? Results.NotFound(new { error = "File not found or path rejected" })
                : Results.Ok(file);
        });

        app.MapPut("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, WikiSaveRequest body, ProjectDocsService docs, GitService git) =>
        {
            var rel = Normalize(relPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            if (body.Content == null) return Results.BadRequest(new { error = "content field required" });

            var result = docs.WriteWikiFile(projectName, rel, body.Content);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });

            var repoRoot = git.ResolveRepoRootForProject(projectName);
            if (string.IsNullOrWhiteSpace(repoRoot))
                return Results.BadRequest(new { error = "Repository not found" });

            var branch = git.GetStatusForRepoRoot(repoRoot).Branch;
            if (!result.Changed)
                return Results.Ok(new { relPath = rel, saved = true, changed = false, sha = (string?)null, branch });

            var repoRel = Path.GetRelativePath(repoRoot, result.FullPath!).Replace('\\', '/');
            var commit = git.CommitPaths(repoRoot, $"wiki: update {rel}", new[] { repoRel });
            return commit.Success
                ? Results.Ok(new { relPath = rel, saved = true, changed = true, sha = commit.Sha, branch })
                : Results.BadRequest(new { error = commit.Error, branch });
        });

        // Serves images/diagrams referenced from wiki docs so relative
        // `![](images/foo.png)` paths render in place. Markdown-only docs go
        // through /wiki/files; binary assets stream here with a content type.
        app.MapGet("/api/projects/{projectName}/wiki/assets/{**relPath}", (string projectName, string relPath, ProjectDocsService docs) =>
        {
            var asset = docs.ReadWikiAsset(projectName, relPath);
            return asset == null
                ? Results.NotFound(new { error = "Asset not found or type not allowed" })
                : Results.File(asset.Value.Path, asset.Value.ContentType);
        });

        // Per-doc provenance + history: which model last touched it (frontmatter
        // `model:` wins, else the latest commit's Co-authored-by trailer), plus
        // the file's git log (when / why / who, newest first). Memoized on HEAD
        // so a re-open costs no git spawn (AGT-2013); the ETag lets a reload 304.
        // `history/` sits before the catch-all so it isn't swallowed by
        // /wiki/files/{**relPath}.
        app.MapGet("/api/projects/{projectName}/wiki/history/{**relPath}", (string projectName, string relPath, ProjectDocsService docs, GitService git, HttpContext http) =>
        {
            var res = docs.GetWikiHistory(projectName, relPath, git);
            return res == null
                ? Results.NotFound(new { error = "File not found or path rejected" })
                : ConditionalOk(http, res.ETag, res.History);
        });

        // Content of a wiki doc as it existed at an earlier commit, so the
        // history panel can preview an old revision. The bytes are content-
        // addressed, so the read is cached permanently and the ETag is the sha
        // (AGT-2013). Sits before the /files catch-all for the same precedence
        // reason as /history.
        app.MapGet("/api/projects/{projectName}/wiki/revisions/{sha}/{**relPath}", (string projectName, string sha, string relPath, ProjectDocsService docs, GitService git, HttpContext http) =>
        {
            var res = docs.GetWikiRevision(projectName, sha, relPath, git);
            return res == null
                ? Results.NotFound(new { error = "Revision not found or path rejected" })
                : ConditionalOk(http, res.ETag, res.Revision);
        });

        // ---- Wiki mutations (commit-backed create / move / delete) ----

        // Create a new wiki page (.md/.html/.json). The file is written to disk
        // then committed into the project repo so it shows up in git history.
        app.MapPost("/api/projects/{projectName}/wiki/pages", (string projectName, WikiCreatePageRequest body, ProjectDocsService docs, GitService git) =>
        {
            var rel = Normalize(body.RelPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            var result = docs.CreateWikiPage(projectName, rel, body.Content);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });
            return CommitWikiChange(git, projectName, result.FullPath!, $"wiki: create {rel}", result.ExtraPaths);
        });

        app.MapPost("/api/projects/{projectName}/wiki/folders", (string projectName, WikiCreateFolderRequest body, ProjectDocsService docs, GitService git) =>
        {
            var rel = Normalize(body.RelPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            var result = docs.CreateWikiFolder(projectName, rel);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });
            return CommitWikiChange(git, projectName, result.FullPath!, $"wiki: create folder {rel}");
        });

        // Move/rename a wiki node (file or folder) via git mv + commit.
        app.MapPost("/api/projects/{projectName}/wiki/move", (string projectName, WikiMoveRequest body, ProjectDocsService docs, GitService git) =>
        {
            if (docs.WikiWriteBlockReason(projectName) is { } blocked)
                return Results.Conflict(new { error = blocked });
            var from = Normalize(body.FromRelPath);
            var to = Normalize(body.ToRelPath);
            if (from == null || to == null) return Results.BadRequest(new { error = "fromRelPath and toRelPath are required" });

            var fromFull = docs.ResolveWikiNodeFullPath(projectName, from);
            var toFull = docs.ResolveWikiNodeFullPath(projectName, to);
            var repoRoot = git.ResolveRepoRootForProject(projectName);
            if (fromFull == null || toFull == null || string.IsNullOrWhiteSpace(repoRoot))
                return Results.BadRequest(new { error = "Invalid path or repository not found" });

            var fromRepoRel = Path.GetRelativePath(repoRoot, fromFull).Replace('\\', '/');
            var toRepoRel = Path.GetRelativePath(repoRoot, toFull).Replace('\\', '/');
            var commit = git.MoveAndCommit(repoRoot, fromRepoRel, toRepoRel, $"wiki: move {from} -> {to}");
            return commit.Success
                ? Results.Ok(new { from, to, sha = commit.Sha })
                : Results.BadRequest(new { error = commit.Error });
        });

        // Persist the sibling display order of category folders (consumed by the
        // wiki tree and the folder overview). Stored beside the other wiki
        // metadata in docs/app/config/wiki-order.json and committed like every other wiki
        // mutation; folders missing from the list sort behind alphabetically.
        app.MapPut("/api/projects/{projectName}/wiki/folder-order", (string projectName, WikiFolderOrderRequest body, ProjectDocsService docs, GitService git) =>
        {
            if (body.OrderedNames == null)
                return Results.BadRequest(new { error = "orderedNames field required" });
            var parent = Normalize(body.ParentRelPath) ?? string.Empty;
            var result = docs.SetWikiFolderOrder(projectName, parent, body.OrderedNames);
            if (!result.Success) return Results.BadRequest(new { error = result.Error });
            return CommitWikiChange(git, projectName, result.FullPath!,
                $"wiki: reorder categories under {(parent.Length == 0 ? "root" : parent)}");
        });

        // Delete a wiki node (file or folder) via git rm + commit.
        app.MapDelete("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs, GitService git) =>
        {
            if (docs.WikiWriteBlockReason(projectName) is { } blocked)
                return Results.Conflict(new { error = blocked });
            var rel = Normalize(relPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            var full = docs.ResolveWikiNodeFullPath(projectName, rel);
            var repoRoot = git.ResolveRepoRootForProject(projectName);
            if (full == null || string.IsNullOrWhiteSpace(repoRoot))
                return Results.BadRequest(new { error = "Invalid path or repository not found" });

            var repoRel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
            var commit = git.RemoveAndCommit(repoRoot, repoRel, $"wiki: delete {rel}");
            return commit.Success
                ? Results.Ok(new { relPath = rel, sha = commit.Sha })
                : Results.BadRequest(new { error = commit.Error });
        });

        // ---- Architecture decisions ----

        app.MapGet("/api/projects/{projectName}/architecture", (string projectName, ProjectDocsService docs) =>
        {
            var ov = docs.GetArchitectureOverview(projectName);
            return ov == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(ov);
        });

        app.MapGet("/api/projects/{projectName}/architecture/decisions/{id}", (string projectName, string id, ProjectDocsService docs) =>
        {
            var d = docs.GetArchitectureDecision(projectName, id);
            return d == null ? Results.NotFound(new { error = "Decision not found" }) : Results.Ok(d);
        });
    }

    /// <summary>
    /// Emits an <c>ETag</c> + <c>Cache-Control: no-cache</c> response, honouring a
    /// matching <c>If-None-Match</c> with <c>304 Not Modified</c> so a frontend
    /// reload of an unchanged wiki payload skips the body entirely (AGT-2013).
    /// The ETag is a strong validator derived from the docs signature / HEAD sha /
    /// commit sha, so a match provably means the client already holds the current
    /// version. <c>no-cache</c> tells the browser to store the response but always
    /// revalidate, which is what turns the next reload into a conditional GET.
    /// </summary>
    internal static IResult ConditionalOk(HttpContext http, string etag, object payload)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "no-cache";

        foreach (var candidate in http.Request.Headers.IfNoneMatch)
        {
            if (candidate == "*" || candidate == etag)
                return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        return Results.Ok(payload);
    }

    /// <summary>Trims and forward-slashes a client path; null when blank.</summary>
    private static string? Normalize(string? relPath)
    {
        if (string.IsNullOrWhiteSpace(relPath)) return null;
        return relPath.Replace('\\', '/').Trim().TrimStart('/');
    }

    /// <summary>
    /// Commits a freshly created wiki file/folder into the project repo and maps
    /// the git outcome to an HTTP result. Resolving the repo root or a failed
    /// commit both surface as a 400 so the UI can show the reason.
    /// </summary>
    private static IResult CommitWikiChange(
        GitService git, string projectName, string fullPath, string message, IReadOnlyList<string>? extraPaths = null)
    {
        var repoRoot = git.ResolveRepoRootForProject(projectName);
        if (string.IsNullOrWhiteSpace(repoRoot))
            return Results.BadRequest(new { error = "Repository not found" });

        var repoRel = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        var paths = new List<string> { repoRel };
        if (extraPaths != null)
            foreach (var extra in extraPaths)
                paths.Add(Path.GetRelativePath(repoRoot, extra).Replace('\\', '/'));
        var commit = git.CommitPaths(repoRoot, message, paths);
        return commit.Success
            ? Results.Ok(new { relPath = repoRel, sha = commit.Sha })
            : Results.BadRequest(new { error = commit.Error });
    }
}

public record WikiCreatePageRequest(string RelPath, string? Content);
public record WikiCreateFolderRequest(string RelPath);
public record WikiMoveRequest(string FromRelPath, string ToRelPath);
public record WikiFolderOrderRequest(string? ParentRelPath, List<string>? OrderedNames);
public record WikiSaveRequest(string? Content);
