using OrchestratorApi.Services.Diagnostics;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Read-only diagnostics surface that exposes operator-facing artefacts
/// from the rolling backend log. Lives under <c>/api/diagnostics/*</c>
/// so the supervisor and the Layer 3 system review can pick up crash
/// evidence without parsing a daily log file.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/diagnostics");

        // Returns the most recent crash captured by the process-wide
        // exception handlers in Program.cs. 200 with a JSON body when a
        // crash has been recorded since the file was last cleared, 404
        // when the marker is absent (the happy path on a clean run).
        group.MapGet("/last-crash", (CrashRecorder recorder) =>
        {
            var path = recorder.MarkerPath;
            if (!File.Exists(path)) return Results.NotFound(new { recorded = false });
            try
            {
                var bytes = File.ReadAllBytes(path);
                return Results.File(bytes, "application/json");
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to read crash marker: {ex.Message}", statusCode: 500);
            }
        });

        // Reports the resolved log directory + the file currently being
        // written to. Cheap and load-bearing: a sibling agent or the
        // user can paste the path into a tail command without guessing
        // where the log lives this run.
        group.MapGet("/log-location", (BackendFileLogSink sink) => Results.Ok(new
        {
            directory = sink.ResolvedDirectory,
            currentFile = sink.CurrentLogPath,
        }));
    }
}
