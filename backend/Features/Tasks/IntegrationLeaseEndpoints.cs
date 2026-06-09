using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Task Server integration-lease API. Remote runners use this before mutating a
/// project's integration branch; the fencing token returned by acquire must
/// accompany heartbeat/release and future integration actions.
/// </summary>
public static class IntegrationLeaseEndpoints
{
    public static void MapIntegrationLeaseEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runner/integration-lease");

        group.MapPost("/acquire", (IntegrationLeaseAcquireRequest req, IntegrationLeaseService leases) =>
            Results.Ok(leases.TryAcquire(req)));

        group.MapPost("/heartbeat", (IntegrationLeaseHeartbeatRequest req, IntegrationLeaseService leases) =>
            Results.Ok(leases.Renew(req)));

        group.MapPost("/release", (IntegrationLeaseReleaseRequest req, IntegrationLeaseService leases) =>
            Results.Ok(leases.Release(req)));

        group.MapGet("/{projectName}/{integrationBranch}", (
            string projectName,
            string integrationBranch,
            IntegrationLeaseService leases) => Results.Ok(leases.Peek(projectName, integrationBranch)));
    }
}
