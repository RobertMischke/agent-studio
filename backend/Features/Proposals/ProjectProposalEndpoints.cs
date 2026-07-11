namespace AgentStudio.Proposals;

public sealed record ProposalDecisionRequest(string Decision);

public static class ProjectProposalEndpoints
{
    public static void MapProjectProposalEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/proposals", (string projectName, ProjectProposalService proposals) =>
        {
            var items = proposals.List(projectName);
            return items == null
                ? Results.NotFound(new { error = $"Unknown project '{projectName}'" })
                : Results.Ok(new { project = projectName, items });
        });

        app.MapGet("/api/projects/{projectName}/proposals/{id}", (string projectName, string id, ProjectProposalService proposals) =>
        {
            var item = proposals.Get(projectName, id);
            return item == null ? Results.NotFound(new { error = "Proposal not found" }) : Results.Ok(item);
        });

        app.MapPost("/api/projects/{projectName}/proposals/{id}/decision", (string projectName, string id,
            ProposalDecisionRequest body, ProjectProposalService proposals) =>
        {
            if (body.Decision is not ("approve" or "reject"))
                return Results.BadRequest(new { error = "decision must be approve or reject" });
            var result = proposals.Decide(projectName, id, body.Decision);
            return result == null ? Results.NotFound(new { error = "Proposal not found" }) : Results.Ok(result);
        });

        app.MapGet("/api/projects/{projectName}/proposals/evidence/{**relPath}",
            (string projectName, string relPath, ProjectProposalService proposals) =>
            {
                var proposal = proposals.List(projectName)?.FirstOrDefault(p =>
                    string.Equals(p.EvidenceScreenshot.Replace('\\', '/').TrimStart('/'), relPath.Replace('\\', '/').TrimStart('/'), StringComparison.OrdinalIgnoreCase));
                if (proposal == null) return Results.NotFound();
                var root = ProjectRepoResolver.ResolveForProject(projectName,
                    app.Services.GetRequiredService<TaskScannerService>(), app.Services.GetRequiredService<ProjectRegistry>());
                if (root == null) return Results.NotFound();
                var full = Path.GetFullPath(Path.Combine(root, "docs/proposals", proposal.EvidenceScreenshot));
                var guard = Path.GetFullPath(Path.Combine(root, "docs/proposals")) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(guard, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return Results.NotFound();
                return Results.File(full, "image/png");
            });
    }
}
