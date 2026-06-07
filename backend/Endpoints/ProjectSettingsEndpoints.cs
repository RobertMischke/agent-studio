using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
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
                    orchestratorThinkingLevel = kv.Value.OrchestratorThinkingLevel,
                    // Epic decomposition (planning) run knobs (way 3): null
                    // model means "use the epic card's own model"; subTasksToReady
                    // null/false lands generated sub-tasks in 0-backlog.
                    epicPlanningModel = kv.Value.EpicPlanningModel,
                    epicPlanningThinkingLevel = kv.Value.EpicPlanningThinkingLevel,
                    epicSubTasksToReady = kv.Value.EpicSubTasksToReady ?? false,
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
                    // Resolved per-CLI permission mode + source for all four CLIs
                    // (project override → detected global config → YOLO default).
                    // The project-settings UI uses this to render the effective
                    // mode without a per-CLI round-trip.
                    cliModes = CliTypes.All.ToDictionary(
                        cli => cli,
                        cli =>
                        {
                            var r = settings.ResolveCliMode(kv.Key, cli);
                            return new { mode = r.Mode, source = r.Source, args = r.Args };
                        },
                        StringComparer.OrdinalIgnoreCase),
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
                // here (it uses the task's own CLI + model); aspect and drift
                // steps invoke an LLM so they accept a model; tool/orchestrator
                // gate steps accept a mode.
                usesModel = s.Kind is StepKind.Aspect or StepKind.Drift,
                usesPrompt = s.Kind is StepKind.Aspect or StepKind.Drift,
                supportsMode = s.Kind is StepKind.Tool or StepKind.Orchestrator,
                // The loop guard is a safety net that always runs (the
                // StuckLoopGuard circuit-breaker fires regardless of this row);
                // the pipeline step only mirrors its state, so it is not an
                // opt-out toggle - making it disable-able would let a project
                // hide a loop the breaker still acts on.
                canDisable = s.Kind != StepKind.Core
                    && !string.Equals(s.Id, PipelineCatalogue.LoopGuardStepId, StringComparison.Ordinal),
                // The drift post-steps default off (opt-in); every other step
                // defaults on. The Settings UI uses this to render the toggle's
                // initial state when the project has no explicit override.
                defaultEnabled = s.DefaultEnabled,
                supportsCondition = s.Kind != StepKind.Core,
            }).ToList();

            // The abort-triggered review step lives off the linear AllSteps list
            // (it only fires after a non-clean run end) but is configurable
            // through the same per-project override mechanism.
            var abort = PipelineCatalogue.AbortReviewStep;
            steps.Add(new
            {
                id = abort.Id,
                displayName = abort.DisplayName,
                kind = abort.Kind.ToString(),
                usesModel = true,
                usesPrompt = true,
                supportsMode = false,
                canDisable = true,
                defaultEnabled = abort.DefaultEnabled,
                supportsCondition = true,
            });

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
            // instead of writing dead config that never reaches a real step. The
            // abort-review step lives off the linear AllSteps list but is a valid
            // configurable target, so accept it explicitly.
            var known_step = PipelineCatalogue.Standard.AllSteps
                    .Any(s => string.Equals(s.Id, req.StepId, StringComparison.OrdinalIgnoreCase))
                || string.Equals(PipelineCatalogue.AbortReviewStep.Id, req.StepId, StringComparison.OrdinalIgnoreCase);
            if (!known_step)
                return Results.BadRequest(new { error = $"Unknown pipeline step '{req.StepId}'" });

            if (!string.IsNullOrWhiteSpace(req.Mode) && PostStepConfigResolver.ParseMode(req.Mode) is null)
                return Results.BadRequest(new { error = $"Unsupported mode '{req.Mode}' (expected off / warn / fail)" });

            // Validate any run condition: the token must be known, value-bearing
            // tokens need a value, and the condition must target a step the
            // runtime actually evaluates conditions for (today: abort-review).
            // An "always" / blank condition is a no-op and is left to normalize
            // away in the service.
            if (req.Condition is { } condition && !string.IsNullOrWhiteSpace(condition.When))
            {
                var when = PipelineStepConditions.Normalize(condition.When);
                if (when is null)
                    return Results.BadRequest(new { error = $"Unsupported condition '{condition.When}'" });

                if (when != PipelineStepConditions.Always
                    && PipelineStepConditions.RequiresValue(when)
                    && string.IsNullOrWhiteSpace(condition.Value))
                    return Results.BadRequest(new { error = $"Condition '{when}' requires a value" });
            }

            settings.SetPipelineStep(projectName, req.StepId, new PipelineStepSetting
            {
                Enabled = req.Enabled,
                Mode = req.Mode,
                Model = req.Model,
                ThinkingLevel = req.ThinkingLevel,
                Prompt = req.Prompt,
                Condition = req.Condition,
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

        // Per-project CLI permission modes. GET returns the resolved mode +
        // source + rendered flags for every CLI (defaults filled in) so the
        // settings UI can render the effective state and dropdown in one shot.
        app.MapGet("/api/projects/{projectName}/cli-modes", (string projectName, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            var s = settings.Get(projectName);
            var resolved = CliTypes.All.ToDictionary(
                cli => cli,
                cli =>
                {
                    var r = settings.ResolveCliMode(projectName, cli);
                    return new { mode = r.Mode, source = r.Source, args = r.Args };
                },
                StringComparer.OrdinalIgnoreCase);
            return Results.Ok(new
            {
                resolved,
                overrides = s.CliModes ?? new Dictionary<string, string>(),
                available = CliPermissionModes.UserVisible,
            });
        });

        // PUT one CLI's permission mode. An empty/null mode clears the override
        // (revert to the platform default / global config). Takes effect on the
        // next spawn without a backend restart (ProjectRunner resolves live).
        app.MapPut("/api/projects/{projectName}/cli-mode", (string projectName, SetCliModeRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            if (!CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"Unknown CLI '{req.CliType}'" });
            // Empty clears; a non-empty value must be a known mode.
            if (!string.IsNullOrWhiteSpace(req.Mode) && !CliPermissionModes.IsValid(req.Mode))
                return Results.BadRequest(new { error = $"Unsupported permission mode '{req.Mode}'" });

            settings.SetCliMode(projectName, req.CliType, req.Mode);
            var r = settings.ResolveCliMode(projectName, req.CliType);
            return Results.Ok(new { cli = r.CliType, mode = r.Mode, source = r.Source, args = r.Args });
        });

        // Effective-mode probe (ticket test path). Returns the mode a spawn of
        // <name> in <project> would use right now, its source, and the exact
        // flags the driver would inject — the reload-able check the E2E uses to
        // confirm a UI toggle reaches the resolver.
        app.MapGet("/api/cli/{name}/effective-mode", (string name, string? project, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            if (!CliTypes.IsValid(name))
                return Results.BadRequest(new { error = $"Unknown CLI '{name}'" });
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "query parameter 'project' is required" });

            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, project, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{project}'" });

            var r = settings.ResolveCliMode(project, name);
            return Results.Ok(new
            {
                cli = r.CliType,
                project,
                mode = r.Mode,
                source = r.Source,
                args = r.Args,
            });
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

            settings.SetOrchestratorModel(projectName, req.Model, req.ThinkingLevel);
            return Results.Ok(settings.Get(projectName));
        });

        // Epic decomposition (planning) run knobs (way 3): the model that
        // authors the sub-task list and whether generated sub-tasks land in
        // 2-ready instead of 0-backlog. Null fields leave that knob untouched.
        app.MapPut("/api/projects/{projectName}/epic-planning", (string projectName, SetEpicPlanningRequest req, ProjectSettingsService settings, TaskScannerService scanner) =>
        {
            var known = scanner.GetWatchPaths().Any(e => string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (!known) return Results.NotFound(new { error = $"Unknown project '{projectName}'" });

            settings.SetEpicPlanning(projectName, req.Model, req.ThinkingLevel, req.SubTasksToReady);
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
