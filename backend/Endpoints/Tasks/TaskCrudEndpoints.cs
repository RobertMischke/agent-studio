using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Cli;
using OrchestratorApi.Services.Tasks;
using OrchestratorApi.Services.Runner;
using OrchestratorApi.Services.Tokens;
using static OrchestratorApi.Endpoints.Tasks.TaskEndpointHelpers;

namespace OrchestratorApi.Endpoints.Tasks;

/// <summary>
/// Job CRUD + state transitions: list, detail, create, delete, move,
/// reorder, change-project, plus the "set one job field" PUTs (model,
/// cli-type, title). These are the routes that read or rewrite the
/// canonical <c>task.json</c> on disk.
/// </summary>
public static class TaskCrudEndpoints
{
    public static void MapTaskCrudEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/", (bool? includeFixtures, TaskScannerService scanner, CliRouter router, TaskRunnerService runners, ITokenAggregator tokens, IConfiguration configuration) =>
        {
            var raw = scanner.ScanAllJobs();
            if (includeFixtures != true) raw = raw.Where(j => !j.Fixture).ToList();
            var tokenLookup = BuildTokenLookup(raw, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(raw, configuration);
            var jobs = raw.Select(job => WithRuntime(job, router, runners, tokenLookup, verdictLookup)).ToList();
            return Results.Ok(jobs);
        });

        group.MapGet("/grouped", (bool? includeFixtures, TaskScannerService scanner, CliRouter router, TaskRunnerService runners, ITokenAggregator tokens, IConfiguration configuration, ProjectSettingsService projectSettings) =>
        {
            var raw = scanner.ScanAllJobs();
            if (includeFixtures != true) raw = raw.Where(j => !j.Fixture).ToList();
            var tokenLookup = BuildTokenLookup(raw, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(raw, configuration);
            var jobs = raw.Select(job => WithRuntime(job, router, runners, tokenLookup, verdictLookup)).ToList();
            // F35: each lane is sorted using a per-project strategy. The kanban
            // mixes projects inside one lane, so the sort groups by project,
            // applies that project's resolved strategy, then concatenates the
            // groups in alphabetical project order for a deterministic global
            // result. Settings are cached in-memory (single lock) so the
            // per-project lookup is essentially free.
            var settingsByProject = new Dictionary<string, ProjectSettings>(StringComparer.OrdinalIgnoreCase);
            ProjectSettings SettingsFor(string projectName)
            {
                if (!settingsByProject.TryGetValue(projectName, out var s))
                {
                    s = projectSettings.Get(projectName);
                    settingsByProject[projectName] = s;
                }
                return s;
            }
            List<TaskInfo> SortLane(string lane)
                => LaneSortApplier.Sort(jobs.Where(j => j.State == lane), lane, SettingsFor).ToList();

            // ADR-0025: explicit AutoReview + HumanReview lanes. The legacy
            // "Review" key is kept (auto-review only) so older clients that
            // only know the four pre-ADR-0025 lane names keep getting a
            // populated bucket and don't crash on a missing field.
            var autoReview = SortLane(TaskStates.AutoReview);
            var humanReview = SortLane(TaskStates.HumanReview);
            var grouped = new
            {
                // Backlog: triage staging area, the leftmost lane and the
                // default landing for new jobs.
                Backlog = SortLane(TaskStates.Backlog),
                Preparation = SortLane(TaskStates.Preparation),
                // ADR-0026: orchestrator-prep lane (hide-when-empty). The
                // 1b-needs-human-review bounce lane has been retired.
                OrchestratorPrep = SortLane(TaskStates.OrchestratorPrep),
                Ready = SortLane(TaskStates.Ready),
                Progress = SortLane(TaskStates.Progress),
                // ADR-0028: 3a-failed-pickup is a hide-when-empty lane that
                // surfaces orphan boot-sweep verdicts the runner used to hide
                // in 7-archive. Empty by default; clients render it only when
                // it has at least one job.
                FailedPickup = SortLane(TaskStates.FailedPickup),
                // 3b-code-not-complete: hide-when-empty park lane for tasks that
                // exhausted their auto-pickup retry budget without reaching
                // review. Empty by default; clients render it only when populated.
                CodeNotComplete = SortLane(TaskStates.CodeNotComplete),
                AutoReview = autoReview,
                HumanReview = humanReview,
                Review = autoReview, // legacy alias for pre-ADR-0025 clients
                Completed = SortLane(TaskStates.Completed),
                Archive = SortLane(TaskStates.Archive)
            };
            return Results.Ok(grouped);
        });

        group.MapGet("/{jobId}", (string jobId, string? watchPath, TaskScannerService scanner, CliRouter router, TaskRunnerService runners, ITokenAggregator tokens, IConfiguration configuration) =>
        {
            var detail = scanner.GetJobDetail(jobId, watchPath);
            if (detail is null) return Results.NotFound();
            var tokenLookup = BuildTokenLookup(new[] { detail.Info }, tokens);
            var verdictLookup = BuildOrchestratorVerdictLookup(new[] { detail.Info }, configuration);
            return Results.Ok(WithRuntime(detail, router, runners, tokenLookup, verdictLookup));
        });

        // Promote a finished planning task to a pre-filled coding task. Returns
        // a fully-populated draft (title + prompt body from the report's
        // `## Proposed task prompt` section, copyable image references,
        // mode=coding, state=1-preparation) that the frontend feeds into the
        // existing create-task modal. The modal stays the single source of
        // truth for create UX; this endpoint only reads. See
        // docs/research/planning-research-task-kinds-2026-05.md.
        group.MapGet("/{jobId}/promote-to-coding", (string jobId, string? watchPath, TaskScannerService scanner) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info is null) return Results.NotFound();
            if (TaskModes.Normalize(info.Mode) != TaskModes.Planning)
                return Results.BadRequest(new { error = "Only planning tasks can be promoted to a coding task." });

            var plan = scanner.BuildPromoteToCodingPlan(jobId, watchPath);
            if (plan is null) return Results.NotFound();

            var watchPathQuery = string.IsNullOrEmpty(plan.WatchPath)
                ? ""
                : $"?watchPath={Uri.EscapeDataString(plan.WatchPath)}";
            var attachments = plan.Attachments
                .Select(a => a with
                {
                    Url = $"/api/tasks/{Uri.EscapeDataString(jobId)}/{a.Source}/{Uri.EscapeDataString(a.FileName)}{watchPathQuery}"
                })
                .ToList();

            return Results.Ok(plan with { Attachments = attachments });
        });

        group.MapPut("/{jobId}/state", async (string jobId, string? watchPath, MoveJobRequest req,
            TaskTransitionService transitions,
            CancellationToken ct) =>
        {
            var validation = ValidateTargetState(req.TargetState);
            if (validation != null) return validation;

            return MoveResult(await transitions.MoveAsync(jobId, req.TargetState, watchPath, ct, req.TargetIndex));
        });

        group.MapPost("/{jobId}/move", async (string jobId, string? watchPath, MoveJobRequest req,
            TaskTransitionService transitions,
            CancellationToken ct) =>
        {
            var validation = ValidateTargetState(req.TargetState);
            if (validation != null) return validation;

            return MoveResult(await transitions.MoveAsync(jobId, req.TargetState, watchPath, ct, req.TargetIndex));
        });

        // Batch move / restore. Per-item atomic: a failure on item N must
        // not roll back items already applied; each item is independently
        // routed through TaskTransitionService.MoveAsync, which is the same
        // path the single-item endpoint above uses. The whole batch returns
        // 200 OK with a per-item status array so the caller can retry just
        // the failures. See AGENTS.md "Job organization rule: API first".
        group.MapPost("/batch-move", async (BatchMoveRequest req,
            TaskTransitionService transitions,
            CancellationToken ct) =>
        {
            if (req?.Items is null || req.Items.Count == 0)
                return Results.BadRequest(new { error = "items is required and must contain at least one entry" });

            var results = await transitions.BatchMoveAsync(req.Items, ct);
            return Results.Ok(new BatchMoveResponse { Results = results.ToList() });
        });

        // Lift a folder out of 3a-failed-pickup back into 2-ready and
        // rename it to drop the -pickup-failed-<utc> suffix. Closes the
        // gap that previously forced operators to fall back to `mv` +
        // manual rename - exactly the bypass the architecture test +
        // AGENTS.md "API first" rule are meant to stop. The state-machine
        // owns the move + slug rewrite atomically; the endpoint logs a
        // forensics row to <workspace>/logs/pickup-failures.jsonl so the
        // dead-letter -> restore lifecycle is reviewable per-slug.
        group.MapPost("/{jobId}/restore-from-failed-pickup",
            (string jobId, string? watchPath, RestoreFromFailedPickupRequest? req,
                TaskScannerService scanner,
                TaskStateMachine states,
                PickupFailureLog pickupFailures) =>
        {
            var keepDeadLetterSlug = req?.KeepDeadLetterSlug ?? false;

            // Capture the project name before the move so the forensics row
            // can attribute the restore even if a post-move FindJob race
            // returns null between the move and the scanner cache refresh.
            var preMove = scanner.FindJob(jobId, watchPath);
            var projectName = preMove?.ProjectName ?? "";

            var outcome = states.RestoreFromFailedPickup(jobId, watchPath, keepDeadLetterSlug);

            switch (outcome.Status)
            {
                case RestoreFromFailedPickupStatus.Success:
                    pickupFailures.AppendRestore(new PickupRestoreRecord
                    {
                        At = DateTime.UtcNow,
                        Kind = PickupFailureKinds.PickupRestored,
                        ProjectName = projectName,
                        Slug = outcome.OriginalSlug ?? "",
                        SourceSlug = outcome.SourceSlug ?? jobId,
                        RestoredAs = outcome.RestoredSlug ?? "",
                        TargetState = TaskStates.Ready,
                        Reason = keepDeadLetterSlug
                            ? "Operator restore via API; dead-letter slug suffix preserved."
                            : "Operator restore via API; original slug recovered."
                    });
                    return Results.Ok(new
                    {
                        status = "restored",
                        restoredSlug = outcome.RestoredSlug,
                        originalSlug = outcome.OriginalSlug,
                        sourceSlug = outcome.SourceSlug,
                        targetState = TaskStates.Ready
                    });
                case RestoreFromFailedPickupStatus.NoOp:
                    return Results.Ok(new
                    {
                        status = "no-op",
                        restoredSlug = outcome.RestoredSlug,
                        originalSlug = outcome.OriginalSlug,
                        sourceSlug = outcome.SourceSlug,
                        message = outcome.Message
                    });
                case RestoreFromFailedPickupStatus.NotFound:
                    return Results.NotFound(new { error = outcome.Message ?? "Folder not found in 3a-failed-pickup." });
                case RestoreFromFailedPickupStatus.TargetFolderExists:
                    return Results.Conflict(new { error = outcome.Message });
                case RestoreFromFailedPickupStatus.InvalidSlug:
                    return Results.BadRequest(new { error = outcome.Message });
                default:
                    return Results.Problem(outcome.Message ?? "Restore failed");
            }
        });

        group.MapDelete("/orphan-folder", ([Microsoft.AspNetCore.Mvc.FromBody] OrphanFolderDeleteRequest req, TaskStateMachine states) =>
        {
            var outcome = states.DeleteOrphanFolder(req.WatchPath ?? "", req.Lane ?? "", req.Folder ?? "");
            return outcome.Status switch
            {
                OrphanFolderDeleteStatus.Success => Results.Ok(new { status = "deleted" }),
                OrphanFolderDeleteStatus.InvalidRequest => Results.BadRequest(new { error = outcome.Message }),
                OrphanFolderDeleteStatus.NonTerminalLane => Results.BadRequest(new { error = outcome.Message }),
                OrphanFolderDeleteStatus.HasJobJson => Results.Conflict(new { error = outcome.Message }),
                OrphanFolderDeleteStatus.NotFound => Results.NotFound(new { error = outcome.Message }),
                _ => Results.Problem(outcome.Message ?? "Orphan folder deletion failed")
            };
        });

        group.MapDelete("/{jobId}", (string jobId, string? watchPath, TaskStateMachine states) =>
        {
            var success = states.DeleteJob(jobId, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPost("/", (CreateJobRequest req, HttpContext ctx, TaskMutationService mutations) =>
        {
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest("Title is required");

            // Header X-Client-Id wins when the body does not name an owner.
            // The middleware has already validated the header against the
            // ClientIdentityStore, so we trust it here.
            if (string.IsNullOrWhiteSpace(req.OwnerClientId))
            {
                var headerOwner = ctx.Request.Headers["X-Client-Id"].FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(headerOwner))
                {
                    req = req with { OwnerClientId = headerOwner };
                }
            }

            var jobId = mutations.CreateJob(req);
            return jobId is null ? Results.Conflict("Job already exists or invalid input") : Results.Ok(new { id = jobId });
        });

        group.MapPost("/reorder", (ReorderRequest req, TaskStateMachine states) =>
        {
            var jobs = req.Jobs.Count > 0
                ? req.Jobs
                : req.JobIds.Select(id => new TaskOrderItem { JobId = id }).ToList();
            var success = states.ReorderJobs(jobs);
            return success ? Results.Ok() : Results.BadRequest("Reorder failed");
        });

        // "Do Next" from the detail view: surface TaskStateMachine.PromoteToReadyTop
        // so the user can push a queued task to the head of the project's ready
        // queue with one click. The state machine handles the reorder atomically
        // on disk and preserves the relative position of any other queued
        // jobs that already carry a PendingIntent.
        group.MapPost("/{jobId}/move-to-top", (string jobId, string? watchPath, TaskStateMachine states) =>
        {
            var position = states.PromoteToReadyTop(jobId, watchPath);
            return position == 0 ? Results.NotFound() : Results.Ok(new { position });
        });

        group.MapPost("/{jobId}/change-project", (string jobId, string? watchPath, ChangeProjectRequest req, TaskStateMachine states) =>
        {
            var success = states.ChangeProject(jobId, req.TargetWatchPath, watchPath);
            return success ? Results.Ok() : Results.BadRequest("Failed to change project");
        });

        group.MapPut("/{jobId}/model", (string jobId, string? watchPath, SetJobModelRequest req, TaskMutationService mutations) =>
        {
            var success = mutations.SetJobModel(jobId, req?.Model, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/thinking-level", (string jobId, string? watchPath, SetJobThinkingLevelRequest req, TaskMutationService mutations) =>
        {
            var success = mutations.SetJobThinkingLevel(jobId, req?.ThinkingLevel, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/cli-type", (string jobId, string? watchPath, SetJobCliTypeRequest req, TaskMutationService mutations) =>
        {
            if (req is null || !CliTypes.IsValid(req.CliType))
                return Results.BadRequest(new { error = $"cliType must be one of {string.Join(", ", CliTypes.All)}" });
            var ok = mutations.SetJobCliType(jobId, req.CliType, watchPath);
            if (!ok) return Results.NotFound();
            if (req.UseOwnSession.HasValue)
                mutations.SetJobUseOwnSession(jobId, req.UseOwnSession.Value, watchPath);
            return Results.Ok();
        });

        // Epics assignment way 2 (post-hoc): attach/detach a task to a parent epic.
        // Body { epicId }: null or empty detaches. (Way 1 is CreateJobRequest.EpicId
        // at create time; way 3 is an epic's decomposition run creating sub-tasks
        // with epicId via the create path.)
        group.MapPut("/{jobId}/epic", (string jobId, string? watchPath, SetJobEpicRequest req, TaskMutationService mutations) =>
        {
            var ok = mutations.SetJobEpic(jobId, req?.EpicId, watchPath);
            return ok ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/title", (string jobId, string? watchPath, SetJobTitleRequest req, TaskMutationService mutations) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            var success = mutations.SetJobTitle(jobId, req.Title, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        group.MapPut("/{jobId}/task-type", (string jobId, string? watchPath, SetJobTaskTypeRequest req, TaskMutationService mutations) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.TaskType))
                return Results.BadRequest(new { error = "taskType is required" });
            var success = mutations.SetJobTaskType(jobId, req.TaskType, watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        // Replace-all: the request's Tags array becomes the new full set on
        // the job. Empty list clears tags. Unknown ids are accepted (the
        // registry may evolve out from under a job); ghost rendering is the
        // FE's responsibility.
        group.MapPut("/{jobId}/tags", (string jobId, string? watchPath, SetJobTagsRequest req, TaskMutationService mutations) =>
        {
            var success = mutations.SetJobTags(jobId, req?.Tags ?? new List<string>(), watchPath);
            return success ? Results.Ok() : Results.NotFound();
        });

        // F34: replace-all write of the structured cross-reference object.
        // Validated against the whole workspace before persisting: every
        // referenced key must exist, a task may not reference itself, and the
        // dependsOn graph must stay a DAG (no cycles). A validation failure
        // returns 400 with a per-edge error list so the FE can mark the
        // offending chip. The 200 body echoes the normalised references.
        group.MapPut("/{jobId}/references", (string jobId, string? watchPath, SetTaskReferencesRequest req,
            TaskScannerService scanner, TaskMutationService mutations) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();

            var proposed = (req ?? new SetTaskReferencesRequest()).ToReferences();
            var index = TaskReferenceIndex.Build(scanner.ScanAllJobs());
            var validation = TaskReferenceValidator.Validate(
                info.Key ?? "", proposed, index.KnownKeys, index.DependsOnGraph);

            if (!validation.IsValid)
                return Results.BadRequest(new
                {
                    error = "Invalid references",
                    errors = validation.Errors.Select(e => new
                    {
                        code = e.Code.ToString(),
                        kind = e.Kind,
                        target = e.Target,
                        message = e.Message
                    })
                });

            var success = mutations.SetTaskReferences(jobId, proposed, watchPath);
            return success ? Results.Ok(proposed) : Results.NotFound();
        });

        // F34 reverse-index: tasks that reference this one. Optional ?kind=
        // narrows to a single relation (dependsOn / relatedTo / blockedBy /
        // supersedes). Drives the detail-view "referenced by" list and the
        // "show dependents of X" board filter. A keyless task (pre-F33) can
        // never be referenced, so it returns an empty list.
        group.MapGet("/{jobId}/dependents", (string jobId, string? watchPath, string? kind,
            TaskScannerService scanner) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(info.Key))
                return Results.Ok(Array.Empty<TaskReferenceLink>());

            var index = TaskReferenceIndex.Build(scanner.ScanAllJobs());
            return Results.Ok(index.Dependents(info.Key, kind));
        });
    }

    /// <summary>
    /// Validates the target state for move/state endpoints. Returns null on
    /// success. Surfaces a directed error when the caller used a pre-ADR-0025
    /// numbered lane name (<c>4-review</c>, <c>5-completed</c>,
    /// <c>6-archive</c>) so client code can be migrated without guessing.
    /// </summary>
    private static IResult? ValidateTargetState(string targetState)
    {
        if (TaskStates.All.Contains(targetState)) return null;

        if (TaskStates.NumberedLegacyMap.TryGetValue(targetState, out var newName))
        {
            return Results.BadRequest(
                $"Lane '{targetState}' was renamed in ADR-0025. " +
                $"Use '{newName}' instead. Full lane order: {string.Join(", ", TaskStates.All)}.");
        }

        return Results.BadRequest($"Invalid state. Allowed: {string.Join(", ", TaskStates.All)}");
    }
}
