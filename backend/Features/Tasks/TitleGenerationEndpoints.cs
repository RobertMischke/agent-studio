
namespace AgentStudio.Tasks;

/// <summary>
/// One-shot title generator. <c>POST /api/title/generate</c> hands a free-text
/// task description to Claude Haiku and returns a short imperative English
/// title. No side effects: the caller (the Create-task dialog's "Generate"
/// button) puts the returned title into the form's title field and the user
/// keeps editing.
/// </summary>
public static class TitleGenerationEndpoints
{
    public record GenerateTitleRequest(string Prompt);
    public record GenerateTitleResponse(string Title);

    public static void MapTitleGenerationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/title/generate", async (GenerateTitleRequest req, TitleGenerationService svc, CancellationToken ct) =>
        {
            if (req == null) return Results.BadRequest(new { error = "Body is required" });

            try
            {
                var title = await svc.GenerateAsync(req.Prompt ?? "", ct);
                return Results.Ok(new GenerateTitleResponse(title));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Preview);
    }
}
