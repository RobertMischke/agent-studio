using OrchestratorApi.Services;

namespace OrchestratorApi.Endpoints;

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

        app.MapGet("/api/projects/{projectName}/wiki/files/{**relPath}", (string projectName, string relPath, ProjectDocsService docs) =>
        {
            var file = docs.ReadWikiFile(projectName, relPath);
            return file == null
                ? Results.NotFound(new { error = "File not found or path rejected" })
                : Results.Ok(file);
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

        // ---- Wiki organisation (user-defined themes + hierarchy) ----

        app.MapGet("/api/projects/{projectName}/wiki/organization", (string projectName, ProjectDocsService docs) =>
        {
            var org = docs.GetWikiOrganization(projectName);
            return org == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(org);
        });

        app.MapPut("/api/projects/{projectName}/wiki/organization", (string projectName, WikiOrganization? org, ProjectDocsService docs) =>
        {
            var ok = docs.WriteWikiOrganization(projectName, org);
            return ok
                ? Results.Ok(docs.GetWikiOrganization(projectName))
                : Results.NotFound(new { error = $"Unknown project '{projectName}'" });
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
}
