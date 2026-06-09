# Pipeline Domain Map

Version: 2026-06-09
Status: System-of-record map for task-processing pipeline changes.

Use this when a change touches pre/core/post steps, pipeline catalog entries,
step ordering, step history, step cost, review fan-out, or the task-detail
pipeline view.

## Entry Points

- [docs/adr/adr-0051-task-processing-pipeline.md](adr/adr-0051-task-processing-pipeline.md)
  is the concept ADR for CI/CD-style task pipelines.
- [docs/concepts/task-execution-and-log-architecture.md](concepts/task-execution-and-log-architecture.md)
  covers the Server/Runner split, stream logs, leases, and shared state.
- [docs/schemas/pipeline-definition.schema.json](schemas/pipeline-definition.schema.json)
  pins versioned pipeline definitions.
- [docs/schemas/step-run.schema.json](schemas/step-run.schema.json) pins
  per-step telemetry rows.
- [docs/token-pricing.md](token-pricing.md) is the single source for pipeline
  cost derivation.

## Key Code

- `backend/Services/Pipeline/PipelineCatalogue.cs`: standard and read-only
  pipeline definitions, step ids, default ordering, step run modes, and display
  names.
- `backend/Services/Pipeline/PipelineExecutionLog.cs`: per-run
  `pipeline-execution.json` history consumed by the Overview and future
  pipeline surfaces.
- `backend/Services/Pipeline/MergeIntoDevelopRunner.cs`: the deferred,
  operator-triggered `post-merge-into-develop` post-step. Performs the real
  `task/<id> -> develop` merge via `GitService.MergeBranchIntoIntegration` when
  the operator accepts a done-green task (the `HumanReview -> Completed`
  transition wired in `TaskTransitionService`), then records the outcome so the
  pending step flips to passed / failed / skipped in place.
- `backend/Services/Pipeline/PipelineStepConfigResolver.cs`: effective model and
  step config resolution.
- `backend/Services/Pipeline/PipelineStepConditionEvaluator.cs`: per-step
  condition evaluation.
- `backend/Services/Pipeline/ProjectPipelineOrder.cs`: project-level step order
  handling.
- `backend/Services/Pipeline/ProjectPipelineCostService.cs` and
  `PipelineCostCalculator.cs`: cost summary projection.
- `backend/Services/Runner/PostAbortReviewStepService.cs` and
  `backend/Services/Runner/PostAbortReview.cs`: abort-review contract and
  deterministic decider.
- `backend/Services/Runner/ReviewDecisionOrchestrator.cs`: post-core review and
  final orchestrator decision recording.
- `backend/Endpoints/Tasks/TaskPipelineEndpoints.cs`: API surface for task
  pipeline data.
- `frontend/src/app/features/task-pipeline/` and the task-detail Overview:
  pipeline presentation.

## Invariants

- `post-orchestrator-review` is an early completeness gate. It must never render
  as a final verdict.
- `post-orchestrator-decision` is the single final orchestrator verdict.
- Abort review is contract-bounded: the model returns a verdict, while
  `PostAbortReviewDecider` owns the binding action and rerun budget.
- The read-only pipeline drops git steps. Planning and research tasks must not
  be forced through write-oriented post steps.
- A `Deferred` step (e.g. `post-merge-into-develop`) is fully implemented but
  runs only on an external operator trigger, not automatically in the
  post-bracket. It is distinct from a `Stub`: a stub has no implementation and
  renders "planned", a deferred step renders "pending" until triggered. The
  merge into develop is best-effort and runs only after the lane move has
  already landed, so it can never block the transition; a conflict is a visible
  `Failed` outcome (conflicted files in the verdict summary) and the working
  tree is left clean, never silently resolved.
- Pipeline history is per run. Re-opened tasks append a new attempt and keep
  earlier attempts addressable.
- If a new step emits a disk or wire shape, add or update a schema and the
  corresponding fixture tests.

## Verification

- Catalogue changes need `PipelineCatalogueTests` and any step-specific test
  that pins display names, ordering, run mode, and enabled defaults.
- Step condition, model, or order changes need `ProjectSettingsServiceTests`,
  `PipelineStepConditionTests`, and `PipelineStepModelDefaultsTests` coverage.
- Review and abort-review changes need `ReviewDecisionOrchestrator*Tests`,
  `PostAbortReviewDeciderTests`, and `PostAbortReviewStepServiceTests`.
- Frontend pipeline rendering changes need Playwright or component coverage plus
  screenshots when the user-facing view changes.
