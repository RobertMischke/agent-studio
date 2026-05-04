using Microsoft.AspNetCore.Mvc;
using OrchestratorApi.Services.Supervisor;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// HTTP surface for the per-project supervisor. Read-only in this first cut:
/// only the observation primitive is wired. Emergency primitives and
/// advisories land in subsequent supervisor tasks.
/// </summary>
public static class SupervisorEndpoints
{
    public static void MapSupervisorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/supervisor");

        group.MapGet("/{project}/observation", async (
            string project,
            ProjectObservationService obs,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var observation = await obs.ObserveAsync(project, ct);
            return Results.Ok(observation);
        });

        group.MapPost("/{project}/intervene/cancel-run", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.JobId)) return Results.BadRequest(new { error = "jobId required" });
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            await svc.CancelRunAsync(project, body.JobId!, body.Reason!, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{project}/intervene/pause-pickup", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            var ttl = body.TtlSeconds.HasValue ? TimeSpan.FromSeconds(body.TtlSeconds.Value) : (TimeSpan?)null;
            await svc.PausePickupAsync(project, body.Reason!, ttl, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{project}/intervene/force-fail", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.JobId)) return Results.BadRequest(new { error = "jobId required" });
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            await svc.ForceFailAsync(project, body.JobId!, body.Reason!, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        });

        group.MapPost("/{project}/intervene/resume", async (
            string project,
            InterventionRequest body,
            SupervisorInterventionService svc,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body.Reason)) return Results.BadRequest(new { error = "reason required" });
            await svc.ResumeAsync(project, body.Reason!, SupervisorSource.User, ct);
            return Results.Ok(new { ok = true });
        });
    }
}

public sealed record InterventionRequest(string? Reason, string? JobId, int? TtlSeconds);
