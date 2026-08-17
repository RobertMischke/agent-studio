
namespace AgentStudio.Prompts;

/// <summary>
/// One-shot prompt enhancer. <c>POST /api/prompt/enhance</c> hands a
/// free-text task description to Claude Haiku and returns a refined
/// prompt, a one-line intent, and topical tags. No side effects: the
/// caller (the Create-task dialog's "Enhance" button) shows the result
/// as a preview the user can apply or discard.
/// </summary>
public static class PromptEnhancementEndpoints
{
    public record EnhancePromptRequest(string Prompt);
    public record EnhancePromptResponse(string RefinedPrompt, string Intent, IReadOnlyList<string> Tags);

    public static void MapPromptEnhancementEndpoints(this WebApplication app)
    {
        app.MapPost("/api/prompt/enhance", async (EnhancePromptRequest req, PromptEnhancementService svc, CancellationToken ct) =>
        {
            if (req == null) return Results.BadRequest(new { error = "Body is required" });

            try
            {
                var result = await svc.EnhanceAsync(req.Prompt ?? "", ct);
                return Results.Ok(new EnhancePromptResponse(result.RefinedPrompt, result.Intent, result.Tags));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }
}
