using static AgentStudio.Tasks.TaskEndpointHelpers;

namespace AgentStudio.Tasks;

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
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            ProjectSettingsService projectSettings,
            PipelineExecutionLog pipelineLog,
            OnDemandPostStepService onDemand) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var record = pipelineLog.Read(info.FolderPath);
            // Cost is derived from the raw recorded tokens; enrich a copy
            // with per-aspect concern detail for the response so the
            // Overview pipeline can tooltip the CONCERNS pill.
            var cost = PipelineCostCalculator.Summarize(record);
            // Per-model tokens, per run plus a grand total over all runs, off
            // the raw record (includes previousAttempts) for the Overview
            // "RUNS - tokens by model" surface.
            var tokensByModel = PipelineCostCalculator.SummarizeByModel(record);
            var execution = AspectConcernReader.Enrich(record, info.FolderPath);

            var settings = string.IsNullOrWhiteSpace(info.ProjectName)
                ? null
                : projectSettings.Get(info.ProjectName);
            var pipeline = ProjectPipelineOrder.Apply(PipelineCatalogue.ForMode(info.Mode), settings);
            var resultFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var step in pipeline.AllSteps)
            {
                var relativePath = step.Kind switch
                {
                    StepKind.Core => "status.md",
                    StepKind.Aspect => $"{step.Id}.md",
                    _ => null,
                };
                if (relativePath is not null && File.Exists(Path.Combine(info.FolderPath, relativePath)))
                    resultFiles[step.Id] = relativePath;
            }
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
                    var configured = PipelineStepConfigResolver.Lookup(settings, step.Id);
                    return new
                    {
                        enabled = PipelineStepConfigResolver.IsEnabled(settings, step),
                        enabledSource = configured is null ? "catalogue" : "project",
                        canDisable = PipelineStepConfigResolver.CanDisable(step),
                        cliType = configured?.CliType ?? step.CliType,
                        model = configured?.Model,
                        thinkingLevel = configured?.ThinkingLevel,
                        mode = configured?.Mode,
                        prompt = configured?.Prompt,
                        condition = configured?.Condition,
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
                tokensByModel,
                config,
                resultFiles,
                onDemand = new
                {
                    plannedStepIds = onDemand.ReadPlan(info.FolderPath),
                    attempts = onDemand.ReadAttempts(info.FolderPath),
                },
            });
        });

        // Add a known idempotent post-step to this card and execute only that
        // step. CORE and the historical orchestrator verdict are untouched.
        group.MapPost("/{jobId}/pipeline/steps/{stepId}/run", async (
            string jobId,
            string stepId,
            string? project,
            string? watchPath,
            RunPostStepRequest? body,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            OnDemandPostStepService onDemand,
            CancellationToken ct) =>
        {
            watchPath = ResolveWatchPath(projects, project, body?.WatchPath ?? watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!OnDemandPostStepService.IsSupported(stepId))
                return Results.BadRequest(new { error = $"Post-step '{stepId}' cannot run on demand" });

            var entry = scanner.GetWatchPaths().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, info.ProjectName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return Results.BadRequest(new { error = "Project source is not configured" });

            var result = await onDemand.RunAsync(info, entry, stepId, body?.AddToCard ?? true, ct);
            return Results.Ok(result);
        });

        // Read-model for the raw step-call prompts captured at central
        // dispatch into .metadata/prompts.jsonl. The UI 'Prompt ansehen'
        // action on a step / timeline entry parses this to show the exact
        // prompt the aspect / code-review-grade / ... step sent to the CLI.
        // Main-run prompts and follow-ups are intentionally absent here -
        // they already live in the task's prompt.md / chat.
        group.MapGet("/{jobId}/step-prompts", (
            string jobId,
            string? project,
            string? watchPath,
            TaskScannerService scanner,
            AgentStudio.Registry.ProjectRegistry projects,
            AgentStudio.Cli.StepPromptLog promptLog) =>
        {
            watchPath = ResolveWatchPath(projects, project, watchPath);
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var prompts = promptLog.ReadForJob(info.FolderPath);
            return Results.Ok(new { prompts });
        });
    }
}

public sealed record RunPostStepRequest
{
    public string? WatchPath { get; init; }
    public bool AddToCard { get; init; } = true;
}
