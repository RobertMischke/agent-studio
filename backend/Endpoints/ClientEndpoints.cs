using OrchestratorApi.Models;
using OrchestratorApi.Services.Clients;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Routes for client identity registration, listing, lookup, and
/// soft-delete. Pairs with <see cref="ClientIdentityStore"/> and the
/// X-Client-Id middleware.
/// </summary>
public static class ClientEndpoints
{
    public static void MapClientEndpoints(this WebApplication app)
    {
        var clients = app.MapGroup("/api/clients");

        clients.MapPost("/register", (RegisterClientRequest request, ClientIdentityStore store) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.BadRequest(new { error = "displayName is required" });
            }
            try
            {
                var record = store.Register(request);
                return Results.Ok(ClientSummary.From(record));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        clients.MapGet("/", (ClientIdentityStore store) =>
        {
            var summaries = store.ListAll().Select(ClientSummary.From).ToList();
            return Results.Ok(summaries);
        });

        clients.MapGet("/{id}", (string id, ClientIdentityStore store, JobScannerService scanner) =>
        {
            var record = store.Find(id);
            if (record is null) return Results.NotFound(new { error = "client-not-found" });

            var owned = scanner.ScanAllJobs()
                .Where(j => string.Equals(j.OwnerClientId, id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.LastActivity)
                .ToList();

            var detail = new ClientDetail
            {
                Identity = ClientSummary.From(record),
                OwnedJobCount = owned.Count,
                RecentJobIds = owned.Take(10).Select(j => j.Id).ToList()
            };
            return Results.Ok(detail);
        });

        clients.MapDelete("/{id}", (string id, ClientIdentityStore store) =>
        {
            if (string.Equals(id, DefaultClientIdentity.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest(new { error = "default-identity-cannot-be-retired" });
            }
            var changed = store.SoftDelete(id);
            return changed
                ? Results.Ok(new { id, kind = ClientIdentityKinds.Retired })
                : Results.NotFound(new { error = "client-not-found" });
        });
    }
}
