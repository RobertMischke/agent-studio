using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pipeline;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Pipeline read surface for one job. Returns the static
/// <see cref="TaskPipeline"/> the runtime targets for this job plus the
/// latest <see cref="PipelineExecutionRecord"/> on disk
/// (<c>pipeline-execution.json</c> in the job folder), or a
/// "no execution yet" envelope when the job has not been processed.
///
/// <para>
/// Phase 1 always returns the standard pipeline; the per-project
/// pipeline-settings override lands with the Settings panel follow-up
/// task. Keeping the spec on the response (not just the execution)
/// lets the Overview pipeline view render even before the first run
/// has emitted any per-step records.
/// </para>
/// </summary>
public static class JobPipelineEndpoints
{
    public static void MapJobPipelineEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/pipeline", (
            string jobId,
            string? watchPath,
            TaskScannerService scanner,
            PipelineExecutionLog pipelineLog) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var pipeline = PipelineCatalogue.Standard;
            var record = pipelineLog.Read(info.FolderPath);

            return Results.Ok(new
            {
                pipeline,
                execution = record,
            });
        });
    }
}
