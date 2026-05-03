using OrchestratorApi.Services;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Project-level docs surface (prototype): security archive +
/// architecture decision browser. See <see cref="ProjectDocsService"/>
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
