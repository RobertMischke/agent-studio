using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Pipeline;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Body for <c>PUT /api/projects/{name}/intake</c>. Enables or disables the
/// orchestrator-intake gate. See <see cref="ProjectSettings.IntakeEnabled"/>.
/// </summary>
public record SetIntakeEnabledRequest
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Body for <c>POST /api/tasks/{jobId}/intake</c>. Optional explicit watch
/// path so a job id that exists in multiple workspaces resolves to the
/// caller's project.
/// </summary>
public record IntakeRunRequestBody
{
    public string WatchPath { get; init; } = "";
}

/// <summary>
/// Per-project preferences under <c>/api/projects</c> — read-all
/// for the header bar plus the per-project auto-commit toggle.
/// </summary>
public static class ProjectSettingsEndpoints
{
    public static void MapProjectSettingsEndpoints(this WebApplication app)
    {
        // Per-project preferences (auto-commit on/off today). Read-all returns a
        // flat map keyed by project name so the header can render every toggle
        // in one shot without N round-trips.
        app.MapGet("/api/projects/settings", (ProjectSettingsService settings) =>
        {
            var all = settings.GetAll();
            var projected = all.ToDictionary(
                kv => kv.Key,
                kv => new
                {
                    autoCommit = kv.Value.AutoCommit,
                    autoPushStrategy = AutoPushStrategies.Normalize(kv.Value.AutoPushStrategy),
                    runnerMode = kv.Value.RunnerMode,
                    orchestratorModel = kv.Value.OrchestratorModel,
                    intakeEnabled = kv.Value.IntakeEnabled,
                    autonomyLevel = kv.Value.AutonomyLevel,
                    // ADR-0052: parallel-execution knobs. maxParallelism == 1
                    // means the runner stays sequential; the branch/strategy
                    // pair only matters once it is raised above 1.
                    maxParallelism = kv.Value.MaxParallelism < 1 ? 1 : kv.Value.MaxParallelism,
                    integrationBranch = kv.Value.IntegrationBranch,
                    integrationStrategy = IntegrationStrategies.Normalize(kv.Value.IntegrationStrategy),
                    // F35: resolved per-lane strategy map (defaults filled in).
                    // The board uses this for the lane-header icon + the
                    // drag-disabled hint without a per-project round-trip.
                    laneSortStrategies = TaskStates.All.ToDictionary(
                        lane => lane,
                        lane => LaneSortStrategies.Resolve(kv.Value, lane),
                        StringComparer.OrdinalIgnoreCase),
                    // Per-step pipeline overrides (enabled / mode / model).
                    // Only the steps the operator has touched appear here;
                    // an absent step is on its built-in default.
                    pipelineSteps = kv.Value.PipelineSteps ?? new Dictionary<string, PipelineStepSetting>(),
                },
                StringComparer.OrdinalIgnoreCase);
            return Results.Ok(projected);
        });

        // The configurable pipeline-step catalogue: the code-defined steps a
        // project can enable/disable, set a model on, or set a gate mode on.
        // The Settings panel reads this to render one control per step
        // without hardcoding the step list on the frontend.
        app.MapGet("/api/projects/pipeline-catalogue", () =>
        {
            var pipeline = PipelineCatalogue.Standard;
            var steps = pipeline.AllSteps.Select(s => new
            {
                id = s.Id,
                displayName = s.DisplayName,
                kind = s.Kind.ToString(),
                // The core agent run cannot be disabled or model-overridden
                // here (it uses the task's own CLI + model); aspect steps
                // invoke an LLM so they accept a model; tool/orchestrator
                // gate steps accept a mode.
                usesModel = s.Kind == StepKind.Aspect,
                supportsMode = s.Kind is StepKind.Tool or StepKind.Orchestrator,
                canDisable = s.Kind != StepKind.Core,
            }).ToList();
            return Results.Ok(new { pipelineId = pipeline.Id, steps });
        });

        // Per-project pipeline-step override. Sets enabled / mode / model for
        // one step; an all-null body clears the override (revert to default).
        app.MapPut("/api/projects/{projectName}/pipeline-step", (string projectName, SetPipelineStepRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (string.IsNullOrWhiteSpace(req.StepId))
                return Results.BadRequest(new { error = "stepId is required" });

            // Reject step ids the catalogue does not know so a typo fails loud
            // instead of writing dead config that never reaches a real step.
            var known_step = PipelineCatalogue.Standard.AllSteps
                .Any(s => string.Equals(s.Id, req.StepId, StringComparison.OrdinalIgnoreCase));
            if (!known_step)
                return Results.BadRequest(new { error = $"Unknown pipeline step '{req.StepId}'" });

            if (!string.IsNullOrWhiteSpace(req.Mode) && PostStepConfigResolver.ParseMode(req.Mode) is null)
                return Results.BadRequest(new { error = $"Unsupported mode '{req.Mode}' (expected off / warn / fail)" });

            settings.SetPipelineStep(projectName, req.StepId, new PipelineStepSetting
            {
                Enabled = req.Enabled,
                Mode = req.Mode,
                Model = req.Model,
            });
            return Results.Ok(new
            {
                stepId = req.StepId,
                pipelineSteps = settings.Get(projectName).PipelineSteps ?? new Dictionary<string, PipelineStepSetting>(),
            });
        });

        app.MapPut("/api/projects/{projectName}/auto-commit", (string projectName, SetAutoCommitRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            // Reject unknown project names so a typo in the URL fails loud rather than silently
            // adding orphan settings entries that never reach a board column.
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetAutoCommit(projectName, req.Enabled);
            return Results.Ok(settings.Get(projectName));
        });

        app.MapPut("/api/projects/{projectName}/auto-push-strategy", (string projectName, SetAutoPushStrategyRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (!AutoPushStrategies.All.Contains(req.Strategy, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unsupported auto-push strategy '{req.Strategy}'" });

            var normalized = AutoPushStrategies.Normalize(req.Strategy);
            settings.SetAutoPushStrategy(projectName, normalized);
            return Results.Ok(settings.Get(projectName));
        });

        // ADR-0052: max number of tasks the runner runs concurrently for this
        // project. 1 (default) keeps it sequential; the value is clamped to
        // >= 1 server-side so a 0/negative body cannot stall the runner.
        app.MapPut("/api/projects/{projectName}/max-parallelism", (string projectName, SetMaxParallelismRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetMaxParallelism(projectName, req.MaxParallelism);
            return Results.Ok(settings.Get(projectName));
        });

        // ADR-0052: integration branch parallel task worktrees branch off and
        // merge back into. Blank reverts to the default (develop).
        app.MapPut("/api/projects/{projectName}/integration-branch", (string projectName, SetIntegrationBranchRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetIntegrationBranch(projectName, req.Branch);
            return Results.Ok(settings.Get(projectName));
        });

        // ADR-0052: how a finished task branch folds back into the integration
        // branch (direct-merge default, or pull-request).
        app.MapPut("/api/projects/{projectName}/integration-strategy", (string projectName, SetIntegrationStrategyRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (!IntegrationStrategies.All.Contains(req.Strategy, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unsupported integration strategy '{req.Strategy}'" });

            settings.SetIntegrationStrategy(projectName, req.Strategy);
            return Results.Ok(settings.Get(projectName));
        });

        // The orchestrator's model can be tuned per project. Defaults to
        // Opus when null. This is the only knob today; per-call overrides
        // are not exposed.
        app.MapPut("/api/projects/{projectName}/orchestrator-model", (string projectName, SetOrchestratorModelRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetOrchestratorModel(projectName, req.Model);
            return Results.Ok(settings.Get(projectName));
        });

        // ADR-0026: per-project autonomy slider for the orchestrator-prep
        // loop. Returns the resolved level (default 2 when not set) so the
        // header can render the slider without a second round-trip.
        app.MapGet("/api/projects/{projectName}/autonomy", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            return Results.Ok(new { level = s.AutonomyLevel ?? 2 });
        });

        app.MapPut("/api/projects/{projectName}/autonomy", (string projectName, SetAutonomyLevelRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetAutonomyLevel(projectName, req.Level);
            return Results.Ok(new { level = settings.Get(projectName).AutonomyLevel ?? 2 });
        });

        // F35: per-lane sort strategy. GET returns the resolved map (every
        // lane key present, defaults filled in) so the settings UI can render
        // a dropdown per lane without a second round-trip. PUT writes one
        // lane at a time; an empty/null strategy clears the override and the
        // lane reverts to its default.
        app.MapGet("/api/projects/{projectName}/lane-sort-strategies", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lane in TaskStates.All)
                resolved[lane] = LaneSortStrategies.Resolve(s, lane);
            return Results.Ok(new
            {
                resolved,
                overrides = s.LaneSortStrategyOverrides ?? new Dictionary<string, string>(),
                available = LaneSortStrategies.UserVisible,
            });
        });

        app.MapPut("/api/projects/{projectName}/lane-sort-strategy", (string projectName, SetLaneSortStrategyRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (string.IsNullOrWhiteSpace(req.Lane))
                return Results.BadRequest(new { error = "lane is required" });
            if (!TaskStates.All.Contains(req.Lane, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = $"Unknown lane '{req.Lane}'" });
            // Empty/null clears. Non-empty must be user-selectable; the
            // internal pickup-priority strategy is not exposed to the UI.
            if (!string.IsNullOrWhiteSpace(req.Strategy)
                && !LaneSortStrategies.IsUserSelectable(req.Strategy))
            {
                return Results.BadRequest(new { error = $"Unsupported sort strategy '{req.Strategy}'" });
            }

            settings.SetLaneSortStrategy(projectName, req.Lane, req.Strategy);
            var s = settings.Get(projectName);
            return Results.Ok(new
            {
                lane = req.Lane,
                strategy = LaneSortStrategies.Resolve(s, req.Lane),
                @override = s.LaneSortStrategyOverrides?.GetValueOrDefault(req.Lane)
            });
        });

        // Per-project orchestrator intake toggle (ready-orchestrator-intake-lane).
        // When enabled, the coding runner waits for intake to mark a 2-ready
        // card as intake-passed before picking it up. Default off so existing
        // projects keep their current behavior.
        app.MapPut("/api/projects/{projectName}/intake", (string projectName, SetIntakeEnabledRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetIntakeEnabled(projectName, req.Enabled);
            return Results.Ok(new { enabled = settings.Get(projectName).IntakeEnabled is true });
        });

        // Manual intake trigger: lets the user (or a test) re-run intake on a
        // single 2-ready job without waiting for the hosted-service tick. The
        // job stays in 2-ready; only the phase + lifecycle.json change. Reuses
        // IntakeRunner so the manual trigger and the background loop share one
        // verdict implementation.
        app.MapPost("/api/tasks/{jobId}/intake", (string jobId, IntakeRunRequestBody body, IntakeRunner intake) =>
        {
            try
            {
                var verdict = intake.RunForJob(jobId, string.IsNullOrWhiteSpace(body.WatchPath) ? null : body.WatchPath);
                return Results.Ok(new
                {
                    outcome = verdict.Outcome.ToString(),
                    reason = verdict.Reason,
                    details = verdict.Details
                });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }
}
