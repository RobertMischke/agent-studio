using AgentStudio.Shared;

namespace AgentStudio.Tasks;

/// <summary>
/// The out-of-band task completion endpoint
/// (<c>docs/concepts/out-of-band-task-completion.md</c> §3). One POST that
/// reconciles a task finished outside the runner: writes <c>status.md</c> +
/// <c>results/deliverables.md</c>, terminalizes <c>lifecycle.json</c>, records
/// the external provenance, appends an <c>external_completion</c> timeline
/// entry, moves the lane, and commits the workspace evidence. The heavy lifting
/// lives in <see cref="ExternalCompletionService"/>; the endpoint is a thin
/// validation + status-to-HTTP shell, matching the neighbor endpoints.
/// </summary>
public static class TaskExternalCompletionEndpoints
{
    public static void MapTaskExternalCompletionEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/external-completion", async (
            string jobId,
            string? watchPath,
            ExternalCompletionRequest req,
            HttpContext ctx,
            ExternalCompletionService service,
            CancellationToken ct) =>
        {
            // Attribution: the operator who relayed the out-of-band result is
            // the caller in X-Client-Id (validated by the client middleware);
            // it drives the lane_changed ledger row. The completion *source*
            // (who actually did the work) is a separate field on the body.
            var clientId = ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
            var actor = string.IsNullOrWhiteSpace(clientId)
                ? TimelineActors.External
                : TimelineActors.Human(clientId!);

            var outcome = await service.CompleteAsync(jobId, watchPath, req, actor, ct);
            return outcome.Status switch
            {
                ExternalCompletionStatus.Success => Results.Ok(new ExternalCompletionResponse
                {
                    JobId = outcome.JobId ?? jobId,
                    TargetState = outcome.TargetState ?? TaskStates.HumanReview,
                    Source = string.IsNullOrWhiteSpace(req.Source) ? "external" : req.Source!.Trim(),
                    EvidenceCommitSha = outcome.EvidenceCommitSha,
                }),
                ExternalCompletionStatus.NotFound => Results.NotFound(),
                ExternalCompletionStatus.InvalidRequest => Results.BadRequest(new { error = outcome.Message }),
                ExternalCompletionStatus.MoveConflict => Results.Conflict(new { error = outcome.Message }),
                _ => Results.Json(
                    new { error = outcome.Message ?? "External completion failed." },
                    statusCode: StatusCodes.Status500InternalServerError),
            };
        });
    }
}
