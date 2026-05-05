using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;
using static OrchestratorApi.Endpoints.Jobs.JobEndpointHelpers;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Job CRUD + state transitions: list, detail, create, delete, move,
/// reorder, change-project, plus the "set one job field" PUTs (model,
/// cli-type, title). These are the routes that read or rewrite the
/// canonical <c>job.json</c> on disk.
/// </summary>
public static class JobCrudEndpoints
{
    public static void MapJobCrudEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", (bool? includeFixtures, JobScannerService scanner, CliRouter router, TaskRunnerService runners, TokenSummaryService tokens, IConfiguration configuration) =>
        {
            var raw = scanner.ScanAllJobs();
            if (includeFixtures != true) raw = raw.Where(j => !j.Fixture).ToList();
            var tokenLookup = BuildTokenLookup(raw, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(raw, configuration);
            var jobs = raw.Select(job => WithRuntime(job, router, runners, tokenLookup, verdictLookup)).ToList();
            return Results.Ok(jobs);
        });

        group.MapGet("/grouped", (bool? includeFixtures, JobScannerService scanner, CliRouter router, TaskRunnerService runners, TokenSummaryService tokens, IConfiguration configuration) =>
        {
            var raw = scanner.ScanAllJobs();
            if (includeFixtures != true) raw = raw.Where(j => !j.Fixture).ToList();
            var tokenLookup = BuildTokenLookup(raw, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(raw, configuration);
            var jobs = raw.Select(job => WithRuntime(job, router, runners, tokenLookup, verdictLookup)).ToList();
            // ADR-0025: explicit AutoReview + HumanReview lanes. The legacy
            // "Review" key is kept (auto-review only) so older clients that
            // only know the four pre-ADR-0025 lane names keep getting a
            // populated bucket and don't crash on a missing field.
            var autoReview = jobs.Where(j => j.State == JobStates.AutoReview).OrderBy(j => j.Order).ToList();
            var humanReview = jobs.Where(j => j.State == JobStates.HumanReview).OrderBy(j => j.Order).ToList();
            var grouped = new
            {
                Preparation = jobs.Where(j => j.State == JobStates.Preparation).OrderBy(j => j.Order).ToList(),
                // ADR-0026: orchestrator-prep + needs-human-review lanes.
                // Empty by default; clients render NeedsHumanReview only when
                // it has at least one job (hide-when-empty rule).
                OrchestratorPrep = jobs.Where(j => j.State == JobStates.OrchestratorPrep).OrderBy(j => j.Order).ToList(),
                NeedsHumanReview = jobs.Where(j => j.State == JobStates.NeedsHumanReview).OrderBy(j => j.Order).ToList(),
                Ready = jobs.Where(j => j.State == JobStates.Ready).OrderBy(j => j.Order).ToList(),
                Progress = jobs.Where(j => j.State == JobStates.Progress).OrderBy(j => j.Order).ToList(),
                AutoReview = autoReview,
                HumanReview = humanReview,
                Review = autoReview, // legacy alias for pre-ADR-0025 clients
                Completed = jobs.Where(j => j.State == JobStates.Completed).OrderBy(j => j.Order).ToList(),
                Archive = jobs.Where(j => j.State == JobStates.Archive).OrderBy(j => j.Order).ToList()
            };
            return Results.Ok(grouped);
        });

        group.MapGet("/{jobId}", (string jobId, string? watchPath, JobScannerService scanner, CliRouter router, TaskRunnerService runners, TokenSummaryService tokens, IConfiguration configuration) =>
        {
            var detail = scanner.GetJobDetail(jobId, watchPath);
            if (detail is null) return Results.NotFound();
            var tokenLookup = BuildTokenLookup(new[] { detail.Info }, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(new[] { detail.Info }, configuration);
            return Results.Ok(WithRuntime(detail, router, runners, tokenLookup, verdictLookup));
        });

        group.MapPut("/{jobId}/state", async (string jobId, string? watchPath, MoveJobRequest req,
            JobTransitionService transitions,
            CancellationToken ct) =>
        {
            var validation = ValidateTargetState(req.TargetState);
            if (validation != null) return validation;

            return MoveResult(await transitions.MoveAsync(jobId, req.TargetState, watchPath, ct));
        });

        group.MapPost("/{jobId}/move", async (string jobId, string? watchPath, MoveJobRequest req,
            JobTransitionService transitions,
            CancellationToken ct) =>
        {
            var validation = ValidateTargetState(req.TargetState);
            if (validation != null) return validation;

            return MoveResult(await transitions.MoveAsync(jobId, req.TargetState, watchPath, ct));
        });

        group.MapDelete("/{jobId}", (string jobId, string? watchPath, JobStateMachine states) =>
        {
            var success = states.DeleteJob(jobId, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/", (CreateJobRequest req, HttpContext ctx, JobMutationService mutations) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required");

            // Header X-Client-Id wins when the body does not name an owner.
            // The middleware has already validated the header against the
            // ClientIdentityStore, so we trust it here.
            if (string.IsNullOrWhiteSpace(req.OwnerClientId))
            {
                var headerOwner = ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(headerOwner))
                {
                    req = req with { OwnerClientId = headerOwner };
                }
            }

            var jobId = mutations.CreateJob(req);
            return jobId is null ? Results.Conflict("Job already exists or invalid input") : Results.Ok(new { id = jobId });
        });

        group.MapPost("/reorder", (ReorderRequest req, JobStateMachine states) =>
        {
            var jobs = req.Jobs.Count > 0
                ? req.Jobs
                : req.JobIds.Select(id => new JobOrderItem { JobId = id }).ToList();
            var success = states.ReorderJobs(jobs);
            return success ? Results.Ok() : Results.BadRequest("Reorder failed");
        });

        group.MapPost("/{jobId}/change-project", (string jobId, string? watchPath, ChangeProjectRequest req, JobStateMachine states) =>
        {
            var success = states.ChangeProject(jobId, req.TargetWatchPath, watchPath);
            return success ? Results.Ok() : Results.BadRequest("Failed to change project");
        });

        group.MapPut("/{jobId}/model", (string jobId, string? watchPath, SetJobModelRequest req, JobMutationService mutations) =>
        {
            var success = mutations.SetJobModel(jobId, req?.Model, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/cli-type", (string jobId, string? watchPath, SetJobCliTypeRequest req, JobMutationService mutations) =>
        {
            if (req is null || !CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"cliType must be one of {string.Join(", ", CliTypes.All)}" });
            var ok = mutations.SetJobCliType(jobId, req.CliType, watchPath);
            if (!ok) return Results.NotFound();
            if (req.UseOwnSession.HasValue)
                mutations.SetJobUseOwnSession(jobId, req.UseOwnSession.Value, watchPath);
            return Results.Ok();
        });

        group.MapPut("/{jobId}/title", (string jobId, string? watchPath, SetJobTitleRequest req, JobMutationService mutations) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            var success = mutations.SetJobTitle(jobId, req.Title, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });
    }

    /// <summary>
    /// Validates the target state for move/state endpoints. Returns null on
    /// success. Surfaces a directed error when the caller used a pre-ADR-0025
    /// numbered lane name (<c>4-review</c>, <c>5-completed</c>,
    /// <c>6-archive</c>) so client code can be migrated without guessing.
    /// </summary>
    private static IResult? ValidateTargetState(string targetState)
    {
        if (JobStates.All.Contains(targetState)) return null;

        if (JobStates.NumberedLegacyMap.TryGetValue(targetState, out var newName))
        {
            return Results.BadRequest(
                $"Lane '{targetState}' was renamed in ADR-0025. " +
                $"Use '{newName}' instead. Full lane order: {string.Join(", ", JobStates.All)}.");
        }

        return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", JobStates.All)}");
    }
}
