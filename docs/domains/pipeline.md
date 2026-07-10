# Pipeline Domain Map

Version: 2026-06-10
Status: System-of-record map for task-processing pipeline changes.

Use this when a change touches pre/core/post steps, pipeline catalog entries,
step ordering, step history, step cost, review fan-out, or the task-detail
pipeline view.

## Entry Points

- [docs/architecture/decisions/proposed/adr-0051-task-processing-pipeline.md](../architecture/decisions/proposed/adr-0051-task-processing-pipeline.md)
  is the concept ADR for CI/CD-style task pipelines.
- [docs/concepts/task-execution-and-log-architecture.md](../concepts/task-execution-and-log-architecture.md)
  covers the Server/Runner split, stream logs, leases, and shared state.
- [docs/schemas/pipeline-definition.schema.json](../schemas/pipeline-definition.schema.json)
  pins versioned pipeline definitions.
- [docs/schemas/step-run.schema.json](../schemas/step-run.schema.json) pins
  per-step telemetry rows.
- [docs/domains/token-pricing.md](./token-pricing.md) is the single source for pipeline
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
  pending step flips to passed / failed / skipped in place. After a successful
  merge it also pushes the integration branch itself to `origin`
  (`post-merge-into-develop-push`, AGT-1999) so integration is never only local:
  the push is offloaded via `IntegrationPushQueue` / `IntegrationPushWorker`
  (`PushIntegrationBranchAsync`, the same "not on the request path" strategy as
  the completed-job workspace push), a transient failure retries with backoff per
  the AGT-1944 environmental-retry taxonomy, and a spent budget records a visible
  `Failed` step flagged `environmental`. Default-on; opt out per project via the
  step's `PipelineSteps` override. The origin push primitive is
  `GitService.PushIntegrationBranchAsync` (non-force; a diverged remote is
  reported, never overwritten).
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
  final orchestrator decision recording. `RunCodeReviewGradePostStepAsync` wires
  the automatic quality-grade step (see below) after the aspect fan-out.
- `backend/Services/Review/CodeReviewStepService.cs`: the shared code-review
  engine. `CodeReviewMode.Verdict` is the legacy user-triggered pass/concerns/block
  review; `CodeReviewMode.Grade` is the automatic pipeline pass that assigns an
  A/B/C/D quality grade and writes a rendered `code-review-grade-<ts>.md`.
- `backend/Services/Review/CodeReviewGrade.cs`: grade enum, the
  `[[CODE_REVIEW_GRADE: grade=<A|B|C|D>; summary=<short>]]` sentinel parser, the
  `code-review:grade-{a..d}` tag mapping, and the grade->pass/concerns/block
  severity mapping.
- `backend/Services/Review/CodeReviewGradeModelSelector.cs`: resolves the grade
  model/CLI from `CodeReviewStep:DefaultModel` / `CodeReviewStep:DefaultCli`,
  defaulting to Claude Opus 4.8.
- `backend/Features/Cli/Routing/OneShot/PromptLoggingCliOneShot.cs`: the
  central-dispatch decorator over `ICliOneShot.RunAsync` that captures the raw
  final prompt of every one-shot step-call. `backend/Host/Program.cs` registers
  `ICliOneShot` as this decorator wrapping `ClaudeOneShot`, so wrapping the
  single seam captures every step that opts in by setting `JobFolderPath` +
  `StepId` on its `CliOneShotRequest` (today: the review aspects via
  `AspectRunnerService` and the code-review-grade / verdict passes via
  `CodeReviewStepService`).
- `backend/Features/Cli/Routing/OneShot/StepPromptLog.cs`: the per-job
  append/read writer for `.metadata/prompts.jsonl` (see filesystem-contract).
  Writes through the shared `IJsonlAppender` (concurrent aspect fan-out cannot
  interleave bytes); reads parse the file back into the step-prompt read-model,
  skipping blank / unparseable lines.
- `backend/Features/Tasks/TaskPipelineEndpoints.cs`: API surface for task
  pipeline data, including `GET /{jobId}/step-prompts`, the read-model the
  Overview "Prompt" affordance parses from `.metadata/prompts.jsonl`.
- `frontend/src/app/features/task-pipeline/` and the task-detail Overview:
  pipeline presentation.

## Invariants

- Aspect and code-review prompts carry a complete evidence set (AGT-2022): the
  run-window diff summary is appended with the task-branch-vs-base commit range
  (`base..task/<id>` via `GitService.GetCommitsInRangeAtRoot`) so a squash/merge
  or steer follow-up with an empty working diff still shows the real change set;
  the job's `results/` folder inventory (`ResultsInventory.Render`, file list +
  short excerpts); and a one-line card-mode framing (`ReviewCardMode.Describe`)
  so a read-only planning/research card is not read as missing work. The
  "deliverables missing" verdict is legitimate ONLY when the branch diff is empty
  AND `results/` has no artefacts AND no external deliverable (e.g. a `docs/`
  commit) is documented. `AspectRunInputs` / `CodeReviewStepRequest` carry the
  `ResultsInventory` + `CardMode` fields; the `{{results_inventory}}` and
  `{{card_mode}}` slots render them in every aspect + code-review template.
- `post-orchestrator-review` is an early completeness gate. It must never render
  as a final verdict.
- `post-orchestrator-decision` is the single final orchestrator verdict.
- `post-code-review-grade` is the automatic quality-grade step (ASS-1657). It is
  `DefaultEnabled`, runs after the four aspect reviews and before
  `post-orchestrator-decision`, and assigns every pipelined task an A/B/C/D grade
  with the rubric: A solves the goal completely with tests/evidence, B is solid
  with small gaps, C has concerns (half-done/unclear), D misses the goal or
  redundantly redoes existing code. It is reporting-only and never gates the lane:
  the grade surfaces as a `code-review:grade-{a..d}` card tag plus a rendered
  detail file, a D records a `Failed` step row so it stands out in the Overview,
  and A-C record `Passed`. The grade model is quality-first: it defaults to Claude
  Opus 4.8 (`CodeReviewStep:DefaultModel`, CLI `CodeReviewStep:DefaultCli`) while
  the four cheap aspect reviews stay on Haiku - the deliberate ASS-855/ASS-916
  asymmetry. Opt out per deployment with `CodeReviewStep:AutoGrade=false`. An
  unparseable reply degrades to grade C, never silently A.
- A missing / unparseable aspect verdict caused by the reviewing CLI dying (the
  backend cut that killed the aspect runner mid-run) is an ENVIRONMENTAL infra
  fault, never the card's unfinished work (AGT-2021, belege AGT-1996). The aspect
  runner reruns that step exactly once with the AGT-1944 environmental backoff
  (`PostProcessingOutcomeTaxonomy.DecidePostStepVerdictRetry`,
  `MaxPostStepVerdictRetries` = 1); only when the retry again yields no output is
  the verdict flagged `AspectVerdict.IsInfraFailure`. The orchestrator then
  short-circuits before the accept / reissue routing and escalates the card
  flagged `environmental` + `InfraCrash` as a chain-ending `Escalate` decision, so
  the reissue budget is NOT charged (`ReviewDecisionOrchestrator.HandleAspectInfraCrashAsync`).
  A CLI that DID reply (even garbage) is not infra: it keeps the existing
  `review:unparseable` concern. The other post-steps
  (`post-code-review-grade`, wiki-maintenance / wiki-learnings, regression-radar)
  are reporting-only and already swallow a crash into a Skipped/Failed step row,
  so a post-step crash there never gates the lane or counts as a work deficit.
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
  tree is left clean, never silently resolved. The paired
  `post-merge-into-develop-push` step (AGT-1999) pushes the integration branch to
  `origin` after a successful merge; it is offloaded off the request path and
  never force-pushes, so it too can never block the transition, and a push
  failure is a visible step outcome (`environmental` after the AGT-1944 retry
  budget is spent, or `remote-rejected` on a diverged remote) rather than a
  silent drop. The optional AGT-2009 counterpart - auto-cleanup of merged
  `task/*`/`refs/backups/*` refs right after a successful merge step - is
  intentionally **not** wired into the pipeline; merged-ref removal is an
  operator-triggered action only (Project Hub Git-Management). See
  `docs/wiki/concepts/task-integration-and-merge-workflow.md` §"Branch cleanup"
  for the dry-run/execute contract and the AGT-1945 guard it would reuse.
- Pipeline history is per run. Re-opened tasks append a new attempt and keep
  earlier attempts addressable.
- Raw step-call prompts are captured once, at central dispatch, into
  `.metadata/prompts.jsonl` ("Rohdaten komplett, Herleitung als Lesemodell").
  The capture happens BEFORE the inner CLI call so a timed-out / failed step
  still leaves its prompt; it is best-effort and must never propagate an IO
  failure into the run. Only one-shot step-calls that set both `JobFolderPath`
  and `StepId` are recorded; the main run and its follow-ups are deliberately
  excluded (already in `prompt.md` / chat) so there is no double bookkeeping.
  The UI derives, never re-stores: it reads `GET /step-prompts` rather than
  writing a second copy.
- If a new step emits a disk or wire shape, add or update a schema and the
  corresponding fixture tests.

## Verification

- Catalogue changes need `PipelineCatalogueTests` and any step-specific test
  that pins display names, ordering, run mode, and enabled defaults.
- Step condition, model, or order changes need `ProjectSettingsServiceTests`,
  `PipelineStepConditionTests`, and `PipelineStepModelDefaultsTests` coverage.
- Review and abort-review changes need `ReviewDecisionOrchestrator*Tests`,
  `PostAbortReviewDeciderTests`, and `PostAbortReviewStepServiceTests`.
- Quality-grade step changes need `CodeReviewStepServiceTests` (grade parsing,
  tagging, MD render), `CodeReviewGradeModelSelectorTests` (Opus-4.8 default vs
  Haiku regression guard), `CodeReviewGradeParsingTests` (sentinel grammar), and
  `ReviewDecisionOrchestratorGradeStepTests` (end-to-end: the step executes,
  invokes Opus 4.8 not Haiku, and stamps the `code-review:grade-*` tag).
- Raw step-prompt capture changes need `StepPromptLogTests` (writer/reader
  round-trip with provenance, dedup for main-run shape, capture-before-failure)
  and the `overview-pane.component.spec.ts` step-prompt read-model assertion.
- Frontend pipeline rendering changes need Playwright or component coverage plus
  screenshots when the user-facing view changes.
