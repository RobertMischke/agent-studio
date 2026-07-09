
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

        // The physical docs/ folder hierarchy (folders + .md/.html/.json files)
        // that backs the wiki navigation tree. No git is touched here so loading
        // it stays cheap; per-doc commit metadata is fetched lazily via /history.
        app.MapGet("/api/projects/{projectName}/wiki/tree", (string projectName, ProjectDocsService docs) =>
        {
            var tree = docs.GetWikiTree(projectName);
            return tree == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(tree);
        });

        // Recently-edited wiki pages (page / git author / timestamp), newest
        // first, for the dashboard landing surface. Touches git (one log walk),
        // so it is a separate call from the cheap /tree. `limit` is clamped
        // server-side. Sits before the /files catch-all for path precedence.
        app.MapGet("/api/projects/{projectName}/wiki/recent", (string projectName, ProjectDocsService docs, GitService git, int? limit) =>
        {
            var n = Math.Clamp(limit ?? 12, 1, 50);
            var recent = docs.GetWikiRecentEdits(projectName, git, n);
            return recent == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(recent);
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
        // the file's git log (when / why / who, newest first). `history/` sits
        // before the catch-all so it isn't swallowed by /wiki/files/{**relPath}.
        app.MapGet("/api/projects/{projectName}/wiki/history/{**relPath}", (string projectName, string relPath, ProjectDocsService docs, GitService git) =>
        {
            var full = docs.ResolveWikiDocFullPath(projectName, relPath);
            if (full == null || !File.Exists(full))
                return Results.NotFound(new { error = "File not found or path rejected" });

            var repoRoot = git.ResolveRepoRootForProject(projectName);
            List<GitCommitInfo> commits = [];
            string? trailerModel = null;
            if (!string.IsNullOrWhiteSpace(repoRoot))
            {
                var repoRel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
                commits = git.GetFileHistory(repoRoot, repoRel, 50);
                trailerModel = git.GetLatestModelForPath(repoRoot, repoRel);
            }

            var meta = ProjectDocsService.ParseWikiMetadata(File.ReadAllText(full));
            var model = !string.IsNullOrWhiteSpace(meta.Model) ? meta.Model : trailerModel;
            return Results.Ok(new WikiFileHistory(relPath.Replace('\\', '/'), model, meta, commits));
        });

        // Content of a wiki doc as it existed at an earlier commit, so the
        // history panel can preview an old revision. Sits before the /files
        // catch-all for the same precedence reason as /history.
        app.MapGet("/api/projects/{projectName}/wiki/revisions/{sha}/{**relPath}", (string projectName, string sha, string relPath, ProjectDocsService docs, GitService git) =>
        {
            var full = docs.ResolveWikiDocFullPath(projectName, relPath);
            if (full == null)
                return Results.NotFound(new { error = "File not found or path rejected" });
            var repoRoot = git.ResolveRepoRootForProject(projectName);
            if (string.IsNullOrWhiteSpace(repoRoot))
                return Results.NotFound(new { error = "Repository not found" });

            var repoRel = Path.GetRelativePath(repoRoot, full).Replace('\\', '/');
            var content = git.GetFileAtCommit(repoRoot, sha, repoRel);
            return content == null
                ? Results.NotFound(new { error = "Revision not found" })
                : Results.Ok(new { relPath = relPath.Replace('\\', '/'), sha, content });
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
            return CommitWikiChange(git, projectName, result.FullPath!, $"wiki: create {rel}");
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
            var from = Normalize(body.FromRelPath);
            var to = Normalize(body.ToRelPath);
            if (from == null || to == null) return Results.BadRequest(new { error = "fromRelPath and toRelPath are required" });

            // The fixed frame's shape is immutable: its folders and landing shells
            // cannot be moved/renamed, nor can a move clobber a frame path.
            if (EngineeringWorkstreamFrame.IsStructural(from))
                return Results.Conflict(new { error = ProjectDocsService.FrameLockMessage(from, "moved or renamed") });
            if (EngineeringWorkstreamFrame.IsStructural(to))
                return Results.Conflict(new { error = ProjectDocsService.FrameLockMessage(to, "used as a move target") });

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

        // Delete a wiki node (file or folder) via git rm + commit.
        app.MapDelete("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs, GitService git) =>
        {
            var rel = Normalize(relPath);
            if (rel == null) return Results.BadRequest(new { error = "relPath is required" });
            // Frame folders and landing shells cannot be deleted, even by agents.
            if (EngineeringWorkstreamFrame.IsStructural(rel))
                return Results.Conflict(new { error = ProjectDocsService.FrameLockMessage(rel, "deleted") });
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
    private static IResult CommitWikiChange(GitService git, string projectName, string fullPath, string message)
    {
        var repoRoot = git.ResolveRepoRootForProject(projectName);
        if (string.IsNullOrWhiteSpace(repoRoot))
            return Results.BadRequest(new { error = "Repository not found" });

        var repoRel = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        var commit = git.CommitPaths(repoRoot, message, new[] { repoRel });
        return commit.Success
            ? Results.Ok(new { relPath = repoRel, sha = commit.Sha })
            : Results.BadRequest(new { error = commit.Error });
    }
}

public record WikiCreatePageRequest(string RelPath, string? Content);
public record WikiCreateFolderRequest(string RelPath);
public record WikiMoveRequest(string FromRelPath, string ToRelPath);
public record WikiSaveRequest(string? Content);
