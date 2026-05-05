using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Read-side surface for the <see cref="ReviewDecisionOrchestrator"/>:
/// per-project list of jobs in <c>4-auto-review</c> whose latest CLI output
/// carries an unresolved <c>[[TASK_NEEDS_INPUT]]</c> sentinel. Drives the
/// project-view banner that signals "the orchestrator owes a decision
/// here" so the user can either let auto-decide work or step in.
/// </summary>
public static class ReviewDecisionsEndpoints
{
    public static void MapReviewDecisionsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/projects/{projectName}/review-decisions-pending",
            (string projectName, JobScannerService scanner) =>
        {
            var entries = scanner.GetWatchPaths();
            var entry = entries.FirstOrDefault(e =>
                string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var pending = new List<PendingDecisionDto>();
            var reviewDir = Path.Combine(entry.Path, JobStates.AutoReview);
            if (Directory.Exists(reviewDir))
            {
                foreach (var jobDir in Directory.GetDirectories(reviewDir))
                {
                    var logPath = JobPaths.CliOutputLog(jobDir);
                    if (!File.Exists(logPath)) continue;

                    string log;
                    try { log = File.ReadAllText(logPath); }
                    catch { continue; }

                    var needs = ReviewDecisionParsing.FindUnresolvedNeedsInput(log);
                    if (needs == null) continue;

                    var jobJsonPath = Path.Combine(jobDir, "job.json");
                    string id = Path.GetFileName(jobDir);
                    string title = id;
                    if (File.Exists(jobJsonPath))
                    {
                        try
                        {
                            var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(jobJsonPath));
                            if (json.RootElement.TryGetProperty("id", out var idEl)) id = idEl.GetString() ?? id;
                            if (json.RootElement.TryGetProperty("title", out var tEl)) title = tEl.GetString() ?? id;
                        }
                        catch { /* best-effort metadata read */ }
                    }

                    pending.Add(new PendingDecisionDto(id, title, needs.Reason));
                }
            }

            return Results.Ok(new PendingDecisionsResponse(projectName, pending));
        });
    }
}

public sealed record PendingDecisionDto(string JobId, string Title, string? Reason);
public sealed record PendingDecisionsResponse(string Project, IReadOnlyList<PendingDecisionDto> Items);
