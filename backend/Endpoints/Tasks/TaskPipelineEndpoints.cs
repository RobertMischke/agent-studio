using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Pipeline;

namespace OrchestratorApi.Endpoints.Tasks;

/// <summary>
/// Pipeline read surface for one job. Returns the static
/// <see cref="TaskPipeline"/> the runtime targets for this job, the
/// latest <see cref="PipelineExecutionRecord"/> on disk
/// (<c>pipeline-execution.json</c> in the job folder), the per-project
/// step configuration (enabled / model / mode resolved from
/// <c>project-settings.json</c>), and a derived per-step + task-total
/// cost breakdown.
///
/// <para>
/// The cost is computed on read from the already-recorded per-step
/// tokens via the single <c>TokenPricing</c> table - cheap because one
/// task has only a handful of steps. The config block lets the Overview
/// render "what ran, on which model, what did it cost" and the Settings
/// panel render the per-step enable / model / mode controls without a
/// second round-trip.
/// </para>
/// </summary>
public static class TaskPipelineEndpoints
{
    public static void MapTaskPipelineEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/pipeline", (
            string jobId,
            string? watchPath,
            TaskScannerService scanner,
            ProjectSettingsService projectSettings,
            PipelineExecutionLog pipelineLog) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var pipeline = PipelineCatalogue.Standard;
            var record = pipelineLog.Read(info.FolderPath);
            // Cost is derived from the raw recorded tokens; enrich a copy
            // with per-aspect concern detail for the response so the
            // Overview pipeline can tooltip the CONCERNS pill.
            var cost = PipelineCostCalculator.Summarize(record);
            var execution = AspectConcernReader.Enrich(record, info.FolderPath);

            var settings = string.IsNullOrWhiteSpace(info.ProjectName)
                ? null
                : projectSettings.Get(info.ProjectName);
            var config = pipeline.AllSteps.ToDictionary(
                step => step.Id,
                step =>
                {
                    // Effective model + source resolved the same way the runtime
                    // resolves it (step override -> project model -> global ->
                    // catalogue -> runtime default), so the Overview pipeline can
                    // show which model each LLM-backed step WILL run on before the
                    // run, not just after. Null for deterministic / core steps.
                    var resolved = PipelineStepModelDefaults.Resolve(settings, step);
                    return new
                    {
                        enabled = PipelineStepConfigResolver.IsEnabled(settings, step.Id),
                        model = PipelineStepConfigResolver.Lookup(settings, step.Id)?.Model,
                        thinkingLevel = PipelineStepConfigResolver.Lookup(settings, step.Id)?.ThinkingLevel,
                        mode = PipelineStepConfigResolver.Lookup(settings, step.Id)?.Mode,
                        resolvedModel = resolved?.Model,
                        modelSource = resolved?.Source,
                    };
                },
                StringComparer.OrdinalIgnoreCase);

            return Results.Ok(new
            {
                pipeline,
                execution,
                cost,
                config,
            });
        });
    }
}
