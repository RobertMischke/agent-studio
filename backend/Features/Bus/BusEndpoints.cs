

namespace AgentStudio.Bus;

/// <summary>
/// Read API for the project-screen Agent Message Bus panel. Backed by
/// <see cref="AgentMessageBusStore"/>: disk is the source of truth, the store
/// keeps an in-memory projection per (workspace, project) so these
/// UI-polled endpoints never block on a full disk scan.
/// </summary>
public static class BusEndpoints
{
    public static void MapBusEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/bus");

        group.MapGet("/{project}/summary", (
            string project,
            IConfiguration config,
            AgentMessageBusStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(EmptySummary(project));
            var summary = store.Summarize(workspace!, project, ct);
            return Results.Ok(summary);
        });

        group.MapGet("/{project}/recent", (
            string project,
            IConfiguration config,
            AgentMessageBusStore store,
            int? limit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(Array.Empty<AgentMessage>());
            var cap = Math.Clamp(limit ?? 100, 1, 1000);
            var items = store.Recent(workspace!, project, cap, ct);
            return Results.Ok(items);
        });

        group.MapGet("/{project}/messages", (
            string project,
            IConfiguration config,
            AgentMessageBusStore store,
            string? jobId,
            string? runId,
            string? participantId,
            string? kind,
            string? severity,
            string? cli,
            string? skill,
            string? tag,
            string? correlationId,
            DateTime? since,
            DateTime? until,
            int? limit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(Array.Empty<AgentMessage>());
            var query = new AgentMessageQuery(
                JobId: jobId,
                RunId: runId,
                ParticipantId: participantId,
                Kind: kind,
                Severity: severity,
                Cli: cli,
                Skill: skill,
                Tag: tag,
                CorrelationId: correlationId,
                Since: since,
                Until: until,
                Limit: limit is null ? null : Math.Clamp(limit.Value, 1, 5000));
            var items = store.Query(workspace!, project, query, ct);
            return Results.Ok(items);
        });

        group.MapGet("/{project}/messages/{id}", (
            string project,
            string id,
            IConfiguration config,
            AgentMessageBusStore store,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "project and id required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace)) return Results.NotFound();
            var msg = store.GetById(workspace!, project, id, ct);
            return msg is null ? Results.NotFound() : Results.Ok(msg);
        });

        group.MapGet("/{project}/token-aggregate", (
            string project,
            IConfiguration config,
            BusAggregationCache cache,
            DateTime? since,
            DateTime? until,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(EmptyAggregate(project));
            var snapshot = cache.Aggregate(workspace!, project, since, until, ct);
            return Results.Ok(snapshot);
        });
    }

    private static TokenAggregateResponse EmptyAggregate(string project) => new(
        Project: project,
        TotalMessages: 0,
        Since: null,
        Until: null,
        ByModel: Array.Empty<TokenAggregateBucket>(),
        ByParticipant: Array.Empty<TokenAggregateBucket>(),
        ByDay: Array.Empty<TokenAggregateBucket>(),
        Totals: new TokenAggregateTotals(0, 0, 0, 0, 0, null));

    private static AgentMessageSummary EmptySummary(string project) => new(
        project,
        TotalMessages: 0,
        FirstMessageAt: null,
        LastMessageAt: null,
        CountsByKind: new Dictionary<string, int>(),
        CountsByParticipant: new Dictionary<string, int>(),
        CountsBySeverity: new Dictionary<string, int>());
}
