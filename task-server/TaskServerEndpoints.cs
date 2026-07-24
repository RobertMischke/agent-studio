using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public static class TaskServerEndpoints
{
    public static void MapTaskServerEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "live" }));
        app.MapGet("/readyz", (TaskServerStore store) => store.AuthorityReady
            ? Results.Ok(new { status = "ready", authority = "restored", mode = store.Mode.ToString() })
            : Results.Json(new ApiError("authority-not-ready", "Lease and fence authority has not been restored."), statusCode: 503));

        var api = app.MapGroup("/api/v1");
        api.MapGet("/protocol", (TaskServerStore store) => Results.Ok(store.Status().Protocol));
        api.MapPost("/protocol/compatibility", (ProtocolCompatibilityRequest request, TaskServerStore store) =>
        {
            var supported = TaskServerProtocol.Supports(request.ProtocolVersion)
                && request.ClientKind is "studio" or "runner" or "management";
            var response = new ProtocolCompatibilityResponse(
                supported,
                store.Status().Protocol,
                supported ? null : $"{request.ClientKind} protocol {request.ProtocolVersion} is not supported.");
            return supported ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status426UpgradeRequired);
        });

        api.MapGet("/workspaces", async (TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListWorkspacesAsync(ct)));
        api.MapPost("/workspaces", async (HttpContext context, CreateWorkspaceRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateWorkspaceAsync(request, Actor(context), ct), StatusCodes.Status201Created));

        api.MapGet("/projects", async (string? workspaceId, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListProjectsAsync(workspaceId, ct)));
        api.MapPost("/projects", async (HttpContext context, CreateProjectRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateProjectAsync(request, Actor(context), ct), StatusCodes.Status201Created));

        api.MapGet("/projects/{projectId}/tasks", async (string projectId, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListTasksAsync(projectId, ct)));
        api.MapGet("/projects/{projectId}/tasks/{taskIdentity}", async (string projectId, string taskIdentity, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetTaskAsync(projectId, taskIdentity, ct)));
        api.MapPost("/projects/{projectId}/tasks", async (
            HttpContext context, string projectId, CreateTaskRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateTaskAsync(projectId, request, Actor(context), ct), StatusCodes.Status201Created));
        api.MapPut("/projects/{projectId}/tasks/{taskIdentity}", async (
            HttpContext context, string projectId, string taskIdentity, UpdateTaskRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.UpdateTaskAsync(projectId, taskIdentity, request, Actor(context), ct)));

        var runners = api.MapGroup("/runners");
        runners.MapPut("/{runnerId}", async (
            HttpContext context, string runnerId, RegisterRunnerRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RegisterRunnerAsync(runnerId, request, Actor(context), ct)));
        runners.MapPost("/{runnerId}/claims", async (
            HttpContext context, string runnerId, ClaimRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (!string.Equals(runnerId, request.RunnerId, StringComparison.Ordinal))
                return Results.BadRequest(new ApiError("runner-id-mismatch", "Route and request runner ids differ."));
            return await InvokeAsync(() => store.ClaimAsync(request, Actor(context), ct));
        });
        runners.MapPost("/{runnerId}/review-claims", async (
            HttpContext context, string runnerId, ReviewClaimRequest request, TaskServerStore store, CancellationToken ct) =>
        {
            if (!string.Equals(runnerId, request.ExecutorId, StringComparison.Ordinal))
                return Results.BadRequest(new ApiError("runner-id-mismatch", "Route and request review executor ids differ."));
            return await InvokeAsync(() => store.ClaimReviewAsync(request, Actor(context), ct));
        });

        var runs = api.MapGroup("/runs");
        runs.MapPost("/{runId}/lease/renew", async (
            HttpContext context, string runId, LeaseRenewRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RenewLeaseAsync(runId, request, Actor(context), ct)));
        runs.MapPost("/{runId}/lease/release", async (
            HttpContext context, string runId, LeaseReleaseRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ReleaseLeaseAsync(runId, request, Actor(context), ct)));
        runs.MapPost("/{runId}/completion", async (
            HttpContext context, string runId, CompleteRunRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CompleteRunAsync(runId, request, Actor(context), ct)));
        runs.MapPost("/{runId}/events", async (
            HttpContext context, string runId, EventIngestRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.IngestEventAsync(runId, request, Actor(context), ct), StatusCodes.Status201Created));
        runs.MapGet("/{runId}/events", async (string runId, long? after, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListEventsAsync(runId, after ?? 0, ct)));
        runs.MapPost("/{runId}/artifacts", async (
            HttpContext context, string runId, ArtifactIngestRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.IngestArtifactAsync(runId, request, Actor(context), ct), StatusCodes.Status201Created));
        runs.MapGet("/{runId}/artifacts", async (string runId, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListArtifactsAsync(runId, ct)));
        runs.MapGet("/{runId}/artifacts/{artifactId}/content", async (
            string runId, string artifactId, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetArtifactContentAsync(runId, artifactId, ct)));

        var reviews = api.MapGroup("/reviews");
        reviews.MapPost("/subjects", async (
            HttpContext context, CreateReviewSubjectRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateReviewSubjectAsync(request, Actor(context), ct), StatusCodes.Status201Created));
        reviews.MapGet("/subjects/{subjectId}", async (string subjectId, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetReviewSubjectAsync(subjectId, ct)));
        reviews.MapGet("/attempts/{attemptId}", async (string attemptId, TaskServerStore store, CancellationToken ct)
            => await InvokeNullableAsync(() => store.GetReviewAttemptAsync(attemptId, ct)));
        reviews.MapPost("/attempts/{attemptId}/lease/renew", async (
            HttpContext context, string attemptId, ReviewLeaseRenewRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RenewReviewLeaseAsync(attemptId, request, Actor(context), ct)));
        reviews.MapPost("/attempts/{attemptId}/report", async (
            HttpContext context, string attemptId, ReviewReportRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ReportReviewAsync(attemptId, request, Actor(context), ct)));
        reviews.MapPost("/attempts/{attemptId}/cleanup", async (
            HttpContext context, string attemptId, ReviewCleanupRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CleanupReviewAsync(attemptId, request, Actor(context), ct)));

        var management = api.MapGroup("/management");
        management.MapGet("/status", (TaskServerStore store) => Results.Ok(store.Status()));
        management.MapPut("/mode", async (
            HttpContext context, ChangeModeRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ChangeModeAsync(request, Actor(context), ct)));
        management.MapPost("/prepare-shutdown", async (
            HttpContext context, PrepareShutdownRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.PrepareShutdownAsync(request, Actor(context), ct)));
        management.MapPost("/backups", async (
            HttpContext context, BackupRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.CreateBackupAsync(request, Actor(context), ct), StatusCodes.Status201Created));
        management.MapPost("/restore", async (
            HttpContext context, RestoreRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.RestoreBackupAsync(request, Actor(context), ct)));
        management.MapPost("/attempts/{runId}/resolve-unknown", async (
            HttpContext context, string runId, ResolveUnknownAttemptRequest request, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ResolveUnknownAttemptAsync(runId, request, Actor(context), ct)));
        management.MapGet("/audit", async (long? after, TaskServerStore store, CancellationToken ct)
            => await InvokeAsync(() => store.ListAuditAsync(after ?? 0, ct)));
        management.MapPost("/migrations/legacy/inventory", async (
            LegacyMigrationRequest request, LegacyMigrationService migration, CancellationToken ct)
            => await InvokeAsync(() => migration.InventoryAsync(request, ct)));
        management.MapPost("/migrations/legacy/import", async (
            HttpContext context, LegacyMigrationRequest request, LegacyMigrationService migration, CancellationToken ct)
            => await InvokeAsync(() => migration.ImportAsync(request, Actor(context), ct)));
    }

    private static string Actor(HttpContext context)
        => context.Request.Headers["X-Actor-Id"].FirstOrDefault()
           ?? context.Request.Headers["X-Client-Id"].FirstOrDefault()
           ?? "local-compatibility";

    private static async Task<IResult> InvokeAsync<T>(Func<Task<T>> action, int successStatus = StatusCodes.Status200OK)
    {
        try
        {
            var value = await action();
            return Results.Json(value, statusCode: successStatus);
        }
        catch (Exception exception)
        {
            return MapError(exception);
        }
    }

    private static async Task<IResult> InvokeNullableAsync<T>(Func<Task<T?>> action) where T : class
    {
        try
        {
            var value = await action();
            return value is null ? Results.NotFound(new ApiError("not-found", "Resource was not found.")) : Results.Ok(value);
        }
        catch (Exception exception)
        {
            return MapError(exception);
        }
    }

    private static IResult MapError(Exception exception) => exception switch
    {
        TaskServerProtocolException protocol => Results.Json(
            new ApiError("protocol-unsupported", protocol.Message), statusCode: StatusCodes.Status426UpgradeRequired),
        TaskServerConflictException conflict => Results.Conflict(new ApiError(conflict.Code, conflict.Message)),
        KeyNotFoundException => Results.NotFound(new ApiError("not-found", exception.Message)),
        ArgumentException or FormatException => Results.BadRequest(new ApiError("invalid-request", exception.Message)),
        DirectoryNotFoundException => Results.BadRequest(new ApiError("legacy-root-not-found", exception.Message)),
        InvalidOperationException => Results.Json(new ApiError("not-ready", exception.Message), statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Json(new ApiError("internal-error", "The Task Server could not complete the request."), statusCode: StatusCodes.Status500InternalServerError),
    };
}
