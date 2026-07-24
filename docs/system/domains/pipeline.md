# Pipeline Domain Map

Version: 2026-07-13
Status: System-of-record map for task-processing pipeline changes.

Use this when a change touches pre/core/post steps, pipeline catalog entries,
step ordering, step history, step cost, review fan-out, or the task-detail
pipeline view.

## Entry Points

- [docs/system/architecture/decisions/proposed/adr-0051-task-processing-pipeline.md](../architecture/decisions/proposed/adr-0051-task-processing-pipeline.md)
  is the concept ADR for CI/CD-style task pipelines.
- [docs/concepts/distributed-agent-studio-target-architecture.md](../../concepts/distributed-agent-studio-target-architecture.md)
  covers the Server/Runner split, stream logs, leases, and shared state.
- [Runner provenance, host handoff, and continuation](../../concepts/completion-review-and-remote-runner-stability.html#provenance)
  defines how a pipeline cycle, agent run, execution attempt, and step execution
  retain ordered runner/host placement across planned review handoffs and
  recovery. A single task- or pipeline-level runner field is not sufficient.
- [docs/app/schemas/pipeline-definition.schema.json](../schemas/pipeline-definition.schema.json)
  pins versioned pipeline definitions.
- [docs/app/schemas/step-run.schema.json](../schemas/step-run.schema.json) pins
  per-step telemetry rows.
- [docs/system/domains/token-pricing.md](./token-pricing.md) is the single source for pipeline
  cost derivation.
- [Workflow arguments become unbounded fan-out](../../operations/common-problems/workflow-args-json-string-fanout/)
  records the serialized-argument failure mode and the validation and resource
  caps required before parallel work starts.

## Key Code

- [Model Routing Policy](./model-routing-policy.md) is the canonical model and
  thinking-level selection policy, including weighted criteria, correctness
  floors, benchmark confidence, quota handling, and reissue promotion.
- `backend/Features/Pipeline/ModelQualificationService.cs`: zero-token PRE-step
  that classifies the task in project context and maps it onto the selected
  CLI's live model/reasoning ladders. `IModelEconomyAdvisor` is the stable
  `TokenEconomy.SuggestModel` seam.
- `backend/Features/Pipeline/PipelineStepEconomyAdvisor.cs`: opt-in automated
  recommendation layer for cheap pipeline work. It passes only live-discovered
  Spark candidates to `IModelEconomyAdvisor`, preserves explicit step pins, and
  falls back to the normal runtime model when no qualified Spark model exists.
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
- `backend/Features/Pipeline/TaskSpawnerPostStepRunner.cs` (+ `TaskSpawnerDecision.cs`,
  `TaskSpawnerModelSelector.cs`, `SpawnedTaskLedger.cs`): the opt-in
  `post-task-spawner` step (AGT-2028). After a task settles it asks the best
  available model whether the change set is relevant to a configured target
  project and, on a conservative yes, creates a follow-up card there via
  `TaskMutationService.CreateJob` with a `relatedTo` back-reference. Generic (not
  website-hardwired): target project, relevance question, and spawn lane come from
  `ProjectSettings.TaskSpawner`. Driven from `ReviewDecisionOrchestrator`
  (`RunTaskSpawnerPostStepAsync`); template `prompts/runtime/task-spawner-relevance.md`.
- `backend/Features/Pipeline/AgentsWikiSyncPostStepRunner.cs`: the opt-in
  `post-agents-wiki-sync` step (AGT-1782). Deterministic (no LLM): it keeps the
  AGENTS.md -> wiki pointers for a set of designated topics consistent (no dead /
  missing link) and maintains a machine-owned "Current State / Progress" page per
  designated topic under `docs/concepts/designated-topics/`, so agents read
  the current state of a topic instead of re-discovering it ("gegen im Kreis
  drehen"). The operator-owned topic list is `designated-topics/registry.json`
  (self-provisioned as an empty template on first run); a task is matched to a
  topic by a shared tag or a changed-file path prefix, and the per-topic
  current-state line is derived from the task's title / newest commit / typed
  outcome. Driven from `ReviewDecisionOrchestrator` (`RunAgentsWikiSyncPostStep`),
  next to the wiki-maintenance / wiki-learnings producers.
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
- `backend/Features/Review/CodeReviewGradeModelSelector.cs`: resolves the grade
  model/CLI from `CodeReviewStep:DefaultModel` / `CodeReviewStep:DefaultCli`,
  defaulting to Codex's live-discovered flagship (gpt-5.5 fallback) at its top
  advertised reasoning level.
- `backend/Features/Cli/Routing/OneShot/PromptLoggingCliOneShot.cs`: the
  central-dispatch decorator over `ICliOneShot.RunAsync` that captures the raw
  final prompt of every one-shot step-call. `backend/Host/Program.cs` registers
  `ICliOneShot` as this decorator wrapping `ClaudeOneShot`, so wrapping the
  single seam captures every step that opts in by setting `JobFolderPath` +
  `StepId` on its `CliOneShotRequest` (today: the review aspects via
  `AspectRunnerService` and the code-review-grade / verdict passes via
  `CodeReviewStepService`).
- `backend/Features/Cli/Routing/OneShot/CodexOneShot.cs`: read-only Codex JSONL
  adapter for model-backed pipeline steps. A project can opt an aspect or the
  abort-review step into Codex through its existing `PipelineSteps` CLI/model
  override, including a live-discovered Spark model, without changing the core
  coding run. Model-backed review/pipeline defaults now use Codex/OpenAI routes.
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

- `pre-model-qualification` runs before CORE and never performs quota fallback
  routing. It recommends from the live CLI catalogue without hardcoded model
  ids. Explicit card model/reasoning pins always win; legacy cards without
  provenance are treated as pinned. The selected/recommended pair remains
  visible on the step record.
- Cheap-model routing is explicit and reversible. `PipelineStepSetting` owns the
  `(cliType, model, thinkingLevel)` override per project and step; absent fields
  preserve the current runtime default. Aspect reviews and abort review honor
  all three fields. Spark model ids are selected from the live Codex catalogue,
  not pinned in the static registry, because the entitlement model can change.
  Setting `economyModel: true` on an aspect activates the automated
  `TokenEconomy.SuggestModel` path against the live Spark subset. The
  `pre-model-qualification` step (AGT-2146) remains the evidence-producing guard,
  explicit step pins continue to win, and a missing Spark candidate preserves
  the current runtime default. Coding CORE runs are not routed to Spark by this
  feature.
  Aspect output validation is unchanged and deterministic across models: valid
  sentinels map to the three aspect statuses, while a malformed Spark reply maps
  to `Concerns` plus `review:unparseable` through the existing parser path.

### Post-step lifecycle and ownership

A post-step has four distinct lifecycle states. **Defined** means the code-owned
catalogue knows its id, capabilities, dependencies, and default. **Enabled**
means a project override (or the catalogue default) includes it in future task
pipelines. **Run** means one task has an immutable execution attempt with its
own start, finish, outcome, and artefact reference. **Re-run** appends another
attempt for that same task and step; it does not restart CORE, replace an older
artefact, or rewrite the earlier attempt. The task Overview is the execution
surface, while Project Hub -> Pipeline is the durable activation surface.

Ownership is deliberately layered. The global catalogue owns what a step is
and whether it is available. A project owns the effective default configuration
(enabled, agent, prompt binding, condition, and order). A card owns only its
execution plan and attempt history: an operator may add a catalogue step to an
existing card after creation and run it immediately, without changing the
project default. The Overview must show the effective activation source as
`global`, `project`, or `condition`, with the backend supplying the exact reason
so the UI never re-derives precedence. Its settings link lands on Project Hub ->
Pipeline, the control that can persist a project override; a global default is
code-owned, so that same destination is where an operator overrides it. A
card-level addition is a separate execution-plan fact, not an activation source
or a new arbitrary executable definition; it can only reference a known
catalogue step.

On-demand execution is bounded to post-steps that declare themselves
idempotent and have an implemented runner. It is allowed after the main run and
after the card has reached a terminal lane. Each invocation appends a
timestamped result artefact or an append-only execution entry and records the
CLI-task substrate visibility used by normal pipeline steps. Quality grading is
the first LLM-backed retro use case: it resolves the task-owned branch/commit
range, writes a new `code-review-grade-<timestamp>.md`, updates the current
grade tag, and retains every older grade report. Reporting-only re-runs never
move the card or revise the historical orchestrator verdict.

Deterministic on-demand tools write one immutable task result at
`results/post-steps/<step-id>-attempt-<NNN>.md` and append the matching substrate
row to `logs/step-runs.jsonl`. The result links the project artefact a tool
created or refreshed. This separates the task's audit history from the tool's
idempotent project output, which may legitimately converge on one wiki page.
Attempt numbers are reserved with create-new filesystem markers before a run,
and result files are also create-new, so concurrent requests and process
restarts can leave gaps but can never reuse an attempt or overwrite evidence.
Rows carry the registry-backed `PROJ-NNN` identity, a canonical
`PROJ-NNN::jobId` key, and the schema-defined hash id; mutable display names and
watch paths are not persisted as identity. A project artefact write runs only
against a clean managed checkout, is committed by the platform as one bounded
commit, stamped onto the task, and handed to the completed-push queue. Commit
failure restores only the paths produced inside that boundary; pre-existing
operator changes cause the step to fail before its writer runs.

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
  and A-C record `Passed`. The grade model is quality-first: it defaults to the
  live-discovered Codex flagship with the top supported reasoning level
  (`CodeReviewStep:DefaultModel`, CLI `CodeReviewStep:DefaultCli`), while the four
  bounded aspect reviews use Codex `gpt-5.4-mini` at `high`. Opt out per deployment
  with `CodeReviewStep:AutoGrade=false`. An
  unparseable reply degrades to grade C, never silently A.
- The grade is reporting evidence, not a success gate. It therefore runs before
  the red build/test-gate reissue branch and before the aspect-infrastructure
  escalation branch. A grade transport/runtime failure records `Failed` with its
  error and clears stale `code-review:grade-*` tags, but never changes the lane
  decision.
- Completing a pipeline terminalizes every known, non-deferred, non-stub row:
  an unreached `Pending` row becomes `Skipped` with the branch's causal reason,
  while an interrupted `Running` row becomes `Failed`. Deferred merge/push rows
  remain `Pending`, catalogue stubs remain `Planned`, and unknown extension rows
  are preserved. `PipelineExecutionLog.Read` applies the same projection purely
  to legacy current and previous attempts without rewriting their JSON files.
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
- `post-build-test-gate` verifies a coding task in its registered
  `task/<id>` worktree when that worktree is live. It must not build in the
  shared project checkout for a worktree run: a dev backend can legitimately
  hold that checkout's build output open, and the shared checkout can contain
  different source. Sequential and legacy runs with no registered worktree
  retain the shared-checkout fallback. Within one backend process, complete
  verify-command loops are admitted one at a time per Git common directory, so
  a shared checkout and its linked worktrees cannot launch overlapping full
  builds or test suites. Admission and host-load waits are cancellable and do
  not consume the per-command execution timeout.
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
  `docs/concepts/task-integration-and-merge-workflow.md` §"Branch cleanup"
  for the dry-run/execute contract and the AGT-1945 guard it would reuse.
- `post-task-spawner` (AGT-2028) is an opt-in `StepKind.Orchestrator` post-step,
  `DefaultEnabled = false` and additionally gated on a `ProjectSettings.TaskSpawner`
  target - a project must both enable the step (`PipelineSteps["post-task-spawner"]`)
  and configure a target project before it fires. It runs in the reporting bracket
  (after the aspects, before the pipeline `Complete` mark) and is reporting-only:
  it NEVER changes the source task's lane decision. The relevance + prompt-generation
  model is quality-first (the live-discovered Codex flagship at its top effort via
  `TaskSpawnerModelSelector`, layered under the per-project step override), while the
  spawned card is left to the target project's default model. It is conservative and
  spam-safe by three guards: a run whose aspects `Block` does not spawn (it is about
  to be reissued); an unparseable / not-relevant / prompt-less verdict spawns nothing;
  and the per-source `.metadata/spawned-tasks.jsonl` ledger caps spawns at
  `MaxPerSourceTask` (default 1) so the reissue loop can never double-spawn. Spawn
  creates a `references.relatedTo` edge to the source (a non-blocking reference, NOT a
  `dependsOn` wait - the separate Task-Dependencies feature turns references into
  waits), records a `task_spawned` timeline entry + a `needs-follow-up-task`
  post-processing outcome on the source, and writes the card through the bounded
  `TaskMutationService.CreateJob` path (never a hand-written folder).
- `post-agents-wiki-sync` (AGT-1782) is an opt-in `StepKind.Tool` post-step,
  `DefaultEnabled = false`, deterministic (no model), and reporting-only: it NEVER
  changes the task lane decision. It depends on the core run (not the aspect
  verdicts) and sits with the sibling wiki producers, before the final decision. It
  writes only under `docs/concepts/designated-topics/` plus, when self-healing
  a missing pointer, a single managed block appended to the project's `AGENTS.md`;
  it never edits a hand-maintained concept page in place (those HTML/Markdown pages
  are human-owned), so the machine-maintained current-state block lives in the
  per-topic `<slug>.md` page referenced by a validated pointer. It is
  self-provisioning (seeds an empty `registry.json` an operator fills in) and
  idempotent (a re-run on the same task refreshes timestamps without duplicating a
  progress row; an unmatched task still validates pointers and regenerates the
  index). A missing concept page is surfaced as a visible dead-pointer finding in
  the generated index's "Pointer health" section and the step reason, never
  silently dropped.
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
  tagging, MD render), `CodeReviewGradeModelSelectorTests` (live Codex flagship
  default vs bounded aspect model), `CodeReviewGradeParsingTests` (sentinel
  grammar), and `ReviewDecisionOrchestratorGradeStepTests` (end-to-end: the step
  executes on normal, red build-gate, and aspect-infrastructure paths; invokes the
  Codex flagship; records runtime errors as `Failed`; and stamps only authoritative
  `code-review:grade-*` tags). `PipelineExecutionRestartTests` pins completed-row
  terminalization plus deferred/stub preservation and legacy-read projection.
- Raw step-prompt capture changes need `StepPromptLogTests` (writer/reader
  round-trip with provenance, dedup for main-run shape, capture-before-failure)
  and the `overview-pane.component.spec.ts` step-prompt read-model assertion.
- Agents/wiki-sync changes need `AgentsWikiSyncPostStepRunnerTests` (registry
  seed, tag / path matching, per-topic progress dedup, dead-pointer finding, and
  the AGENTS.md pointer verify / self-heal) plus the `PipelineCatalogueTests`
  step-shape pin (opt-in Tool step, after wiki-learnings, before the decision,
  kept in the read-only pipeline).
- Task-spawner changes need `TaskSpawnerPostStepTests` (relevance sentinel parse
  yes/no/unparseable, dedup-ledger budget + same-target block, best-available-model
  default, and the end-to-end runner writing the follow-up card into a target
  project's flat store with a `relatedTo` back-reference) plus the
  `PipelineCatalogueTests` step-shape pin (opt-in Orchestrator step, after aspects,
  before the decision, kept in the read-only pipeline).
- Frontend pipeline rendering changes need Playwright or component coverage plus
  screenshots when the user-facing view changes.
