using System.Text.Json;
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

        group.MapGet("/{project}/recent-events", (
            string project,
            IConfiguration config,
            int? max) =>
        {
            if (string.IsNullOrWhiteSpace(project)) return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace)) return Results.Ok(new { advisories = Array.Empty<SupervisorAdvisory>(), interventions = Array.Empty<SupervisorIntervention>() });
            var cap = Math.Clamp(max ?? 50, 1, 500);
            var advisories = ReadJsonl<SupervisorAdvisory>(SupervisorLogPaths.ObservationsFile(workspace!, project), cap);
            var interventions = ReadJsonl<SupervisorIntervention>(SupervisorLogPaths.InterventionsFile(workspace!, project), cap);
            return Results.Ok(new { advisories, interventions });
        });
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static List<T> ReadJsonl<T>(string path, int max)
    {
        if (!File.Exists(path)) return new List<T>();
        var lines = File.ReadAllLines(path);
        var start = Math.Max(0, lines.Length - max);
        var list = new List<T>(lines.Length - start);
        for (int i = start; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var item = JsonSerializer.Deserialize<T>(raw, JsonOptions);
                if (item != null) list.Add(item);
            }
            catch { /* skip malformed line */ }
        }
        return list;
    }
}

public sealed record InterventionRequest(string? Reason, string? JobId, int? TtlSeconds);
