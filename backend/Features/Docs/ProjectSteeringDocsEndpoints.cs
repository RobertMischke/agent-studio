
namespace AgentStudio.Docs;

/// <summary>
/// Project Steering Docs surface: read-only inventory of agent-facing
/// instruction files (README, AGENTS, ROADMAP, task contract, skills
/// lookup, ADR index, runtime prompts, project settings) plus a small
/// heuristic warning set for missing or stale entries. The "summarize"
/// and "propose update" actions are owned by the UI; they queue normal
/// 1-preparation tasks via the existing job-creation endpoint, so this
/// surface stays read-only on disk.
/// </summary>
public static class ProjectSteeringDocsEndpoints
{
    public static void MapProjectSteeringDocsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/steering",
            (string projectName, ProjectSteeringDocsService docs) =>
            {
                var ov = docs.GetOverview(projectName);
                return ov == null
                    ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                    : Results.Ok(ov);
            });

        app.MapGet("/api/projects/{projectName}/steering/files/{**relPath}",
            (string projectName, string relPath, ProjectSteeringDocsService docs) =>
            {
                var content = docs.ReadFile(projectName, relPath);
                return content == null
                    ? Results.NotFound(new { error = "File not found, path rejected, or not in the steering inventory." })
                    : Results.Ok(content);
            });

        // Real Tool-Use Read Analytics behind the former mockup: counts which
        // CLI tool-use reads consumed each agent doc, folded across the
        // project's task-folder tool-calls logs.
        app.MapGet("/api/projects/{projectName}/steering/read-analytics",
            (string projectName, int? days, AgentDocsReadAnalyticsService analytics) =>
            {
                var result = analytics.GetAnalytics(projectName, days ?? AgentDocsReadAnalyticsService.DefaultWindowDays);
                return result == null
                    ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                    : Results.Ok(result);
            });
    }
}
