namespace AgentStudio.Diagnostics;

public sealed record RunnerEventIngestRequest(
    string TaskKey,
    string Kind,
    DateTime Timestamp,
    string? Message,
    string? RunnerId = null,
    string? LeaseId = null,
    long FencingToken = 0);

public static class RunnerEventIngestionEndpoints
{
    public static void MapRunnerEventIngestionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/runner/events", (RunnerEventIngestRequest request, HttpContext context, AccessSecurityStore security, RunLeaseService leases, ITaskScanner scanner) =>
        {
            if (string.IsNullOrWhiteSpace(request.TaskKey) || string.IsNullOrWhiteSpace(request.Kind))
                return Results.BadRequest(new { error = "taskKey-and-kind-required" });
            var runner = context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;
            if (runner is null)
            {
                // The networked middleware never admits this route without a
                // scoped Runner principal. Preserve the local-profile daemon,
                // which has no service credential and no security audit file.
                return Results.Accepted();
            }
            if (!RunnerLeaseAuthorization.IsCurrent(
                    context, leases, request.TaskKey, request.RunnerId, request.LeaseId, request.FencingToken))
                return Results.Conflict(new { error = "stale-runner-lease" });
            var task = scanner.ScanAllJobs().FirstOrDefault(candidate =>
                string.Equals(candidate.TaskKey, request.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Key, request.TaskKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Id, request.TaskKey, StringComparison.OrdinalIgnoreCase));
            security.AppendRunAudit(new RunSecurityAuditEvent(
                request.Timestamp == default ? DateTime.UtcNow : request.Timestamp,
                "event:" + CredentialRedactor.Redact(request.Kind).Trim(), request.TaskKey, task?.ProjectName,
                string.IsNullOrWhiteSpace(task?.OwnerClientId) ? "automation:unknown" : task.OwnerClientId,
                runner.RunnerId, runner.CredentialId,
                Outcome: CredentialRedactor.Redact(request.Message)));
            return Results.Accepted();
        });
    }
}
