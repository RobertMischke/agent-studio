namespace AgentStudio.Proposals;

public sealed record ProposalDecisionRequest(string Decision, string? RejectionReason, string? RejectionReasonRaw);
public sealed record ProposalFeedbackRequest(string Feedback);
public sealed record ProposalGenerationRequest(string Topic, string? Guidance);

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

        app.MapPost("/api/projects/{projectName}/proposals/generate", async (string projectName,
            ProposalGenerationRequest body, ProjectProposalService proposals, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Topic)) return Results.BadRequest(new { error = "topic is required" });
            try { return Results.Ok(new { proposal = await proposals.GenerateAsync(projectName, body.Topic, body.Guidance ?? "", ct) }); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Project not found" }); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        });

        app.MapPost("/api/projects/{projectName}/proposals/refine-feedback", async (
            ProposalFeedbackRequest body, ProjectProposalService proposals, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Feedback)) return Results.BadRequest(new { error = "feedback is required" });
            try { return Results.Ok(new { refinedFeedback = await proposals.RefineFeedbackAsync(body.Feedback, ct) }); }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 502); }
        });

        app.MapPost("/api/projects/{projectName}/proposals/{id}/decision", (string projectName, string id,
            ProposalDecisionRequest body, ProjectProposalService proposals) =>
        {
            if (body.Decision is not ("approve" or "reject"))
                return Results.BadRequest(new { error = "decision must be approve or reject" });
            if (body.Decision == "reject" && string.IsNullOrWhiteSpace(body.RejectionReason))
                return Results.BadRequest(new { error = "rejectionReason is required when rejecting" });
            var result = proposals.Decide(projectName, id, body.Decision, body.RejectionReason, body.RejectionReasonRaw);
            return result == null ? Results.NotFound(new { error = "Proposal not found" }) : Results.Ok(result);
        });

        app.MapDelete("/api/projects/{projectName}/proposals/{id}", (string projectName, string id, ProjectProposalService proposals) =>
            proposals.Remove(projectName, id) ? Results.NoContent() : Results.NotFound(new { error = "Proposal not found" }));

        app.MapDelete("/api/projects/{projectName}/proposals", (string projectName, string keepGeneration, ProjectProposalService proposals) =>
        {
            try { return Results.Ok(new { removed = proposals.RemoveOlderGenerations(projectName, keepGeneration) }); }
            catch (KeyNotFoundException) { return Results.NotFound(new { error = "Project not found" }); }
        });

        app.MapGet("/api/projects/{projectName}/proposals/evidence/{**relPath}",
            (string projectName, string relPath, ProjectProposalService proposals, HttpContext http) =>
            {
                var full = proposals.GetEvidencePath(projectName, relPath);
                if (full == null) return Results.NotFound();
                http.Response.Headers.CacheControl = "private, max-age=300";
                return Results.File(full, EvidenceContentType(full), lastModified: File.GetLastWriteTimeUtc(full), enableRangeProcessing: true);
            });
    }

    private static string EvidenceContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };
}
