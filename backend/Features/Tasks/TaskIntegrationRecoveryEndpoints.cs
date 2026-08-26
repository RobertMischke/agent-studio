using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

/// <summary>
/// Operator entry point for the same typed integration-recovery application
/// boundary used by the acceptance rail.
/// </summary>
public static class TaskIntegrationRecoveryEndpoints
{
    public static void MapTaskIntegrationRecoveryEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{jobId}/integration/rebase", async (
            string jobId,
            string? project,
            string? watchPath,
            ProjectRegistry projects,
            TaskIntegrationRecoveryService recovery,
            CancellationToken cancellationToken) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var result = await recovery.TryQueueAsync(
                new TaskIntegrationRecoveryRequest(
                    jobId,
                    watchPath,
                    Automatic: false,
                    Source: "operator-endpoint"),
                cancellationToken);

            return result.Status switch
            {
                TaskIntegrationRecoveryStatus.NotFound => Results.NotFound(new
                {
                    error = result.Message,
                }),
                TaskIntegrationRecoveryStatus.NotEligible
                    or TaskIntegrationRecoveryStatus.BudgetExhausted => Results.Conflict(new
                    {
                        error = result.Message,
                        integrationStatus = IntegrationStatuses.ConflictSkipped,
                        failureCode = result.FailureCode,
                    }),
                TaskIntegrationRecoveryStatus.Failed => Results.Json(
                    new { error = result.Message },
                    statusCode: StatusCodes.Status500InternalServerError),
                _ => Results.Accepted(value: new
                {
                    status = "queued",
                    mode = ContinueModes.Steer,
                    targetState = TaskStates.Ready,
                    position = result.Position,
                    deliveryRef = result.DeliveryRef,
                    resultSha = result.ResultSha,
                    integrationBranch = result.IntegrationBranch,
                    conflictRequeues = result.ConflictRequeues,
                }),
            };
        }).WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Continue);
    }
}
