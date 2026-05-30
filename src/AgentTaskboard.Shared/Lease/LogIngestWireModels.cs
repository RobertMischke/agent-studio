namespace OrchestratorApi.Models;

/// <summary>
/// Wire contract for the Runner → Server log-ingestion API (Step 6). A remote
/// Runner ships the output lines it produced for a task to the server, which
/// appends them to that task's durable <c>logs/cli-output.log</c> — the
/// "Schreiben über die API" half of the distributed model. While a run is in
/// progress the live view is still read locally (direct, fast); ingestion is
/// what lets the server aggregate a remote Runner's output for history and
/// cross-machine viewing.
/// </summary>
public sealed record LogIngestRequest(string TaskKey, List<CliOutputLine> Lines);

public sealed record LogIngestResponse(string TaskKey, int Appended, string? Message = null);
