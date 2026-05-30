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

        clients.MapGet("/{id}", (string id, ClientIdentityStore store, TaskScannerService scanner) =>
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

        // Per-client default CLI + model used when the user creates new tasks
        // (and surfaced into the orchestrator chat prompt so a "create me
        // three tasks" request lands on the user's actual preferences, not a
        // hardcoded fallback). Reads are open; writes require a registered
        // X-Client-Id, same as the rest of /api/.
        clients.MapGet("/{id}/defaults", (string id, ClientIdentityStore store) =>
        {
            var record = store.Find(id);
            if (record is null) return Results.NotFound(new { error = "client-not-found" });
            return Results.Ok(new ClientDefaultsResponse
            {
                Id = record.Id,
                DefaultCliType = record.DefaultCliType,
                DefaultModel = record.DefaultModel
            });
        });

        clients.MapPut("/{id}/defaults", (string id, SetClientDefaultsRequest? request, ClientIdentityStore store) =>
        {
            if (request is null) return Results.BadRequest(new { error = "body-required" });

            // Empty string clears the corresponding side; null leaves it
            // untouched. The store distinguishes the two via the clear flags.
            var clearCli = request.DefaultCliType is not null && string.IsNullOrWhiteSpace(request.DefaultCliType);
            var clearModel = request.DefaultModel is not null && string.IsNullOrWhiteSpace(request.DefaultModel);

            // Validate the CLI value against the known set if it's a non-empty set.
            string? cli = string.IsNullOrWhiteSpace(request.DefaultCliType) ? null : request.DefaultCliType!.Trim().ToLowerInvariant();
            if (cli is not null && cli is not ("claude" or "codex" or "copilot" or "gemini"))
            {
                return Results.BadRequest(new { error = "invalid-cli-type", allowed = new[] { "claude", "codex", "copilot", "gemini" } });
            }

            string? model = string.IsNullOrWhiteSpace(request.DefaultModel) ? null : request.DefaultModel!.Trim();
            if (model is { Length: > 200 })
            {
                return Results.BadRequest(new { error = "model-too-long" });
            }

            var updated = store.SetDefaults(id, cli, model, clearCli, clearModel);
            if (updated is null) return Results.NotFound(new { error = "client-not-found" });

            return Results.Ok(new ClientDefaultsResponse
            {
                Id = updated.Id,
                DefaultCliType = updated.DefaultCliType,
                DefaultModel = updated.DefaultModel
            });
        });
    }
}
