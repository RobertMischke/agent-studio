# Pipeline Domain Map

Version: 2026-08-02
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
- `backend/Features/Runner/PromptEnrichmentService.cs`: deterministic,
  zero-selector-token `pre-prompt-enrichment` step. It classifies the authored
  card, selects curated versioned project/style/delegation blocks, appends at
  most two optional blocks within a 1,500-token budget, and persists
  `enrichment-report.json` before dispatch. Failure to persist the report blocks
  dispatch. The step is default-on and can be disabled through the normal
  per-project `PipelineSteps` convention.
- `backend/Features/Pipeline/PipelineStepEconomyAdvisor.cs`: opt-in automated
  recommendation layer for cheap pipeline work. It passes only live-discovered
  Spark candidates to `IModelEconomyAdvisor`, preserves explicit step pins, and
  falls back to the normal runtime model when no qualified Spark model exists.
- `backend/Features/Pipeline/PipelineCatalogue.cs`: standard, report-only,
  concept, and UI pipeline definitions, step ids, default ordering, step run
  modes, and display names.
- `backend/Features/Pipeline/ConceptWorkbenchContract.cs`,
  `ConceptWorkbenchPublisher.cs`, and `ConceptPromotionService.cs`: the
  document-first concept contract. One isolated concept run may author exactly
  one `docs/operations/<topic>/` Workbench, publishes it through the managed
  project-artifact commit boundary, reviews document completeness and evidence,
  waits for human sight review, then creates coding cards from the descriptor.
- `backend/Features/Pipeline/PipelineCatalogue.cs`,
  `backend/Features/Runner/UiTaskPipelineRouter.cs`, and
  `backend/Features/Runner/UiIterationGate.cs`: the named UI iteration pipeline,
  shared EvidenceGate-based routing, mandatory iteration result layout, and
  bounded hand-off to Human Review. The durable Part 2 consumer shape is defined
  in [the UI task pipeline contract](../contracts/ui-task-pipeline.md).
- `backend/Services/Pipeline/PipelineExecutionLog.cs`: per-run
  `pipeline-execution.json` history consumed by the Overview and future
  pipeline surfaces.
- `backend/Features/Pipeline/RemotePipelineExecutionProjection.cs`: read-time
  bridge for remote cards. It overlays the remote claim and completion facts
  from session/timeline data, the latest Review Plane grade, and canonical
  token-ledger calls onto the normal pipeline catalogue while preserving
  locally recorded integration gates. It never writes
  `pipeline-execution.json` or another lifecycle state.
- `contracts/TaskServer.Contracts/OrchestrationContracts.cs`,
  `task-server/TaskServerOrchestrationStore.cs`, and
  `orchestrator-engine/`: the separated flow boundary. Project flow
  definitions are versioned Task Server data. The API-only Engine executes
  ReviewDecision, Council, PostProcessing, GateDispatch, and CompletionJudge
  stages under bounded per-stage concurrency. A run snapshots the definition
  version and ordered stages at creation, so later definition edits do not
  rewrite in-flight work.
- `backend/Features/TestRuns/`: the separate project test-run lifecycle. These
  runs belong to commits rather than cards and expose planned order, scope,
  host, state, result, duration, and derived card attachments through
  `GET /api/projects/{project}/test-runs`. They do not replace per-task pipeline
  step telemetry.
- `backend/Features/Pipeline/MergeIntoDevelopRunner.cs`: the deferred,
  operator-triggered `post-merge-into-develop` post-step. Performs the real
  delivery merge when the operator accepts a done-green task.
  `TaskTransitionService` keeps the card in Human Review with phase
  `integrating`, stamps the internal `integrationpending` recovery marker, and
  enqueues `AcceptedIntegrationQueue`; `AcceptedIntegrationWorker` performs the
  serialized merge, pre-develop build gate, rollback, and push hand-off outside
  the HTTP request. Only `Merged` or `AlreadyMerged` moves the card to Completed.
  Failures clear the phase, retain the card in Human Review, and append a hard
  integration-failed journal event.
  `DeliveryRefResolver` chooses the immutable result ref first, then an
  attributed commit branch, then `runner/<runner>/<task-key>`, with
  `task/<slug>` only as the legacy local fallback. Remote delivery is fetched
  from origin and fenced to `ResultSha`. Transactional acceptance first fetches
  the configured origin integration ref and uses that refreshed remote ancestry
  for its already-integrated decision, avoiding a redundant gate when the local
  branch is stale. Immediately before a real merge or release gate, the
  configured integration ref is fetched again and fast-forwarded; a missing
  local branch is created from origin and a divergent one fails visibly. The
  outcome is recorded so the pending step
  flips to passed / failed / skipped in place. After a successful merge it
  also pushes the integration branch itself to `origin`
  (`post-merge-into-develop-push`, AGT-1999) so integration is never only local:
  the push is offloaded via `IntegrationPushQueue` / `IntegrationPushWorker`
  (`PushIntegrationBranchAsync`, the same "not on the request path" strategy as
  the completed-job workspace push), a transient failure retries with backoff per
  the AGT-1944 environmental-retry taxonomy, and a spent budget records a visible
  `Failed` step flagged `environmental`. Default-on; opt out per project via the
  step's `PipelineSteps` override. The origin push primitive is
  `GitService.PushIntegrationBranchAsync` (non-force; a diverged remote is
  reported, never overwritten).
  An `AlreadyMerged` replay is not gate evidence. When the pre-develop build
  gate applies, recovery requires a durable
  `post-steps/pre-develop-build-gate-N.log` receipt whose expected and tested
  SHA both equal the exact integration tip being released. A missing, partial,
  or SHA-mismatched receipt reruns the gate against that tip. Until the gate
  returns green, the merge step stays pending and no push request is released;
  a red recovery gate records `GateFailed` and leaves the existing branch graph
  unchanged for manual repair. If the gate applies but its runtime component is
  unavailable, recovery also fails closed without releasing a push. This closes
  the BP-02 merge-commit-before-gate crash window without treating Git ancestry
  as a verdict.
- `backend/Features/Pipeline/RemoteGateActivityStore.cs`: process-local active
  read model fed by the SSH gate start/completion events. The Execution Hosts view
  uses it to show GATE work separately from daemon RUN slots; the store is
  visibility-only and never admits, cancels, or reorders a gate.
- `runner/RemoteReviewWorkspace.cs` and
  `contracts/TaskServer.Contracts/ReviewContracts.cs`: exact-subject remote
  verification. Test commands marked `CompareToBaseline` compare their parsed
  failing-test set with the merge-base on the plan's integration ref. The
  parser recognizes .NET, Jest, Karma, Vitest, native Node test, and npm
  lifecycle output, normalizes ANSI-decorated names and volatile Jest file
  durations, and versions its cache entries when parsing semantics change.
  Baseline results are single-flight cached by repository, baseline SHA, and
  command hash. Only failures still new after one subject retry block the
  review.
- `runner/ReviewStateStore.cs`, `runner/DurableReviewProcess.cs`, and
  `runner/RemoteReviewExecutor.cs`: durable Remote Review execution. Workspace
  preparation persists the immutable subject and lease/fence before the review
  plan starts. The detached worker atomically records process identity, command
  checkpoints, and terminal evidence. A replacement daemon adopts only a
  positively proven process generation and submits the same attempt through the
  deterministic `review-report:<attempt>:<fence>` key.
- `runner/ReviewWorkspaceRetention.cs`: review workspace retention. The
  executor removes an attempt workspace immediately only after the Task Server
  accepts its terminal report. The daemon also sweeps inactive attempt
  directories older than 72 hours once per hour. Active resource namespaces,
  the reusable `.baseline-cache`, reparse points, and unrelated directories are
  never deletion candidates.
- `AcceptedIntegrationBackstopHostedService` re-drives accepted remote
  and local deliveries after a backend restart when the durable Human Review
  `integrating` phase landed but the queued merge did not complete. The channel
  is only a latency optimization; phase, pending marker, pipeline record, and
  timeline are the durability boundary. Recovery consumes the same
  `TaskIntegrationStatusService` target-branch verdict as the board, so a stale
  Passed step cannot overrule missing Git presence. The backstop finalizes
  Completed only after successful integration and returns decided failures to
  Human Review.
- `IntegrationPushBackstopHostedService` reconstructs lost
  `IntegrationPushQueue` work from durable passed-merge and pending-push
  pipeline facts. The channel is a latency optimization, not the durability
  boundary.
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
- `backend/Shared/Models/PipelineTypes.cs` and
  `backend/Features/Pipeline/PipelineTypeSettings.cs`: resolve each card to the
  extensible `task`, `bug`, `feature`, or `planning` settings dimension and
  project that type's step overrides and order before runtime resolution.
- `backend/Features/Projects/ProjectSettingsService.cs`: persists typed
  pipeline overrides and migrates legacy flat settings into all three coding
  types. Planning deliberately starts from its lightweight defaults.
- `backend/Features/Pipeline/TestSelectionPlanner.cs`: staged test planning from
  the lane policy, changed files, project/component ownership, explicit impact
  rules, and Test Hub history. It produces the immutable selection audit used
  by the gate log.
- `backend/Features/Pipeline/LlmTestSelectionAdvisor.cs`: optional constrained
  adviser. It can add only stable candidate ids from the deterministic safe
  inventory and cannot emit an executable command.
- `backend/Features/Pipeline/PreMainTestGate.cs`: fail-closed release boundary
  that forces the full test level before a configured merge can advance
  `main`, irrespective of lane settings, diff input, history, or adviser
  output.
- `backend/Services/Pipeline/PipelineStepConditionEvaluator.cs`: per-step
  condition evaluation.
- `backend/Services/Pipeline/ProjectPipelineOrder.cs`: project-level step order
  handling.
- `backend/Features/Pipeline/ProjectStackDetector.cs`: bounded convention-based
  Angular, .NET, and Node detection from repository markers. Pipeline catalogue
  applicability never reads the configured build-profile stack label.
- `backend/Features/Pipeline/PipelineStepExecutionResolver.cs` and
  `PipelineStepProbeService.cs`: effective shell-command projection and isolated
  per-step probes. Probes do not create or move tasks and execute through the
  build/test gate runner so they share its machine lock.
- `backend/Services/Pipeline/ProjectPipelineCostService.cs` and
  `PipelineCostCalculator.cs`: cost summary projection.
- `backend/Services/Runner/PostAbortReviewStepService.cs` and
  `backend/Services/Runner/PostAbortReview.cs`: abort-review contract and
  deterministic decider.
- `backend/Services/Runner/ReviewDecisionOrchestrator.cs`: post-core review and
  final orchestrator decision recording. `RunCodeReviewGradePostStepAsync` wires
  the automatic quality-grade step (see below) after the aspect fan-out.
- `backend/Features/Runner/ReissuePromptExperiment.cs` and
  `scripts/reissue-prompt-experiment-analysis.mjs`: versioned, reproducible
  task-level control/treatment assignment for eligible finding-bearing
  reissues, hard assignment telemetry, and the right-censor-aware experimental
  report. The treatment changes prompt organization only and never selects a
  coding model, reviewer, rubric, pipeline, or gate. The predeclared contract
  and promotion threshold live in
  [Finding-first reissue prompt experiment](../../quality/pipeline-time-economy/reissue-prompt-experiment.md).
- `backend/Services/Review/CodeReviewStepService.cs`: the shared code-review
  engine. `CodeReviewMode.Verdict` is the legacy user-triggered pass/concerns/block
  review; `CodeReviewMode.Grade` is the automatic pipeline pass that assigns an
  A/B/C/D quality grade and writes a rendered `code-review-grade-<ts>.md`.
- `backend/Services/Review/CodeReviewGrade.cs`: grade enum, the
  `[[CODE_REVIEW_GRADE: grade=<A|B|C|D>; summary=<short>]]` sentinel parser, the
  `code-review:grade-{a..d}` tag mapping, and the grade->pass/concerns/block
  severity mapping.
- `backend/Features/Review/CouncilReviewReaction.cs`: structured review-finding
  parsing, the bounded per-finding council policy, targeted follow-up rendering,
  and the reaction sidecar stored beside each automatic grade artifact.
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
- `backend/Features/Tasks/TaskLiveStatusProjection.cs`: board and detail
  read-model for the current pipeline step, recorded CLI/model provenance,
  enabled upcoming steps, current runner/review queue position, and latest
  activity time. It reads the current execution root only.
- `frontend/src/app/features/task-pipeline/` and the task-detail Overview:
  pipeline presentation.

## Invariants

- Pipeline settings are resolved from the card before enablement, ordering,
  model, prompt, condition, gate, deferred merge, or push decisions. Generic
  coding work, bugs, and features have independent override maps even though
  they currently share the standard catalogue defaults. Planning and research
  use the lightweight planning chain and never inherit migrated coding
  overrides. Concept retains its dedicated document-first catalogue.
- The task pipeline endpoint projects local and remote lifecycle facts at read
  time. A remote claim/completion becomes CORE work, a Review Plane grade
  becomes the DECISION verdict, and recorded integration gates remain TOOL
  steps. Local-only PRE, ASPECT, DRIFT, and review steps that the remote route
  structurally omits are `Skipped` with an explicit remote/not-applicable
  reason. `Not run` is reserved for a step the current attempt genuinely never
  reached. Remote token totals, historical list-price estimates, and call
  counts come from the same token ledger as the Task tab.
- Test execution has three stable levels: `continuous` runs the configured
  fixed baseline, `work-package` adds tests selected from the current diff and
  Test Hub history, and `full` runs every declared test command. Project
  settings map task lanes to levels. Auto Review defaults to `work-package`
  when no mapping exists; an unavailable diff falls back to `full`. A configured
  continuous baseline also runs for documentation-only diffs, and an explicitly
  required `full` level can never be bypassed by the no-code-diff optimization.
- The build/test step reason always states the effective level, selected count,
  whether the full suite ran, and how many full-suite commands were omitted.
  The task Overview exposes that reason from the passed status icon as well, so
  a green work-package subset cannot be mistaken for a full-suite pass.
  Its `post-steps/build-test-gate-*.log` contains the exact diff input, history
  rows, candidate inventory, chosen ids/commands, selector/model, and reasons.
  `FullSuiteRan` is execution evidence, not a planning claim: it becomes true
  only after every selected full-suite test command was attempted.
- A failing continuous-baseline command during a work-package run creates a
  separate `post-steps/test-findings-*.json` record and a `warn` gate verdict.
  It does not block the card. Selected work-package tests still block. No
  failure is non-blocking at the pre-main full-suite boundary. If one physical
  command belongs to both the baseline and the diff-selected set, the stricter
  work-package classification wins.
- A remote ReviewAttempt does not require an historically red integration
  branch to become absolutely green. For each baseline-compared test command,
  its verdict is based on `subject failures - merge-base failures`.
  Intersecting failures remain visible as pre-existing, while the aspect summary
  names every new failure. The Review Executor reads xUnit
  `Category=ReviewFlaky` traits from the exact subject's built test assemblies.
  A newly failing marked test is retried once; if it does not reproduce, the
  report retains its identity as `FlakyQuarantine` and does not classify the
  card as `ProductFailure`. A reproduced marked failure remains a blocking new
  failure. A command with unparseable failing-test output stays fail-closed as a
  new failure. This comparison does not weaken the absolute full-suite boundary
  before advancing `main`.
- Remote Review command execution survives a planned Review daemon restart.
  Recovered attempts retain their original fence and containment namespace and
  resume before load-aware admission evaluates any fresh slot. Completed
  commands are not relaunched. If process adoption cannot be proven, the
  attempt ends visibly as `ReviewInfra / ExecutorRestarted` with the failed
  proof, completed-command count and duration, and retry reason. Replaying the
  fixed report key with another terminal payload is rejected.
- Model advice is additive and allowlisted. Deterministic diff/history choices
  cannot be removed, unknown candidate ids are ignored, and raw model output is
  never interpreted as a shell command.
- Any operation that can advance `main` must call `PreMainTestGate` first and
  proceed only on an `Ok` result with `FullSuiteRequired` and `FullSuiteRan` set.
  `PreMainTestGate` converts a nominally green runner result without that
  evidence into a failure, so callers cannot accidentally accept an incomplete
  release check. It also forces exact-subject execution even if the caller
  supplied a weaker request. The existing deferred integration merge is an
  enforced caller when its configured target resolves to `main`: it runs the
  full suite once on the exact source SHA, records
  `post-steps/pre-main-test-gate-*.log`, rechecks both branch tips after the
  suite, and only then fast-forwards `main`. A red or incomplete result leaves
  `main` unchanged. The future manifest-based release workflow must use the
  same boundary.
- Framework-specific catalogue steps declare `appliesTo`; `any` remains the
  default. The project catalogue response includes derived `detectedStacks`, an
  `applicable` flag, and the effective resolved command list for every step.
  Inapplicable steps remain visible in Project Hub -> Pipeline.
- A project-level step probe is diagnostic only. It may run the step's resolved
  shell command against the repository, but it never creates a task or changes a
  lane. Every shell probe is serialized by the build/test machine lock.

- `pre-model-qualification` runs before CORE and never performs quota fallback
  routing. It recommends from the live CLI catalogue without hardcoded model
  ids. Explicit card model/reasoning pins always win; legacy cards without
  provenance are treated as pinned. The selected/recommended pair remains
  visible on the step record.
- `pre-prompt-enrichment` runs after qualification and before CORE spawn. The
  original task block remains byte-for-byte readable and the labelled
  enrichment is additive inside the existing mode-framing seam. A worktree
  containment notice may still precede the task block. Its step token buckets
  describe selector work only, which is zero in the deterministic
  implementation. Appended prompt tokens are attributed in
  `enrichment-report.json` and remain part of CORE input, so pipeline cost
  totals do not count them twice.
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
- Eligible mapped reissues participate in `finding-first-v1` at the task level.
  The stable hash assignment keeps all attempts for one task in the same arm.
  Both versioned arms receive the identical open-finding payload and preserve
  scope and terminal-sentinel guardrails. Assignment and attempt events are hard
  telemetry; Grade A and orchestrator acceptance remain model-judged evidence;
  arm effects are experimental comparisons. Production-default promotion is
  forbidden until the predeclared benefit and deterministic-gate safeguards
  pass.
- Board cards and task detail share one live-status projection. The active step
  comes from the newest root `PipelineExecutionRecord`; `PreviousAttempts` is
  never eligible for a current-work or inactivity signal. CLI/model labels come
  from the recorded step or matching `StepPromptLog` entry, host identity comes
  from the existing execution-location projection, and queue positions come
  from the runner and post-processing queues that already schedule the work.
  The projection is read-only and introduces no telemetry or persisted task
  state. A Ready task treats an existing completed root as the previous attempt
  and previews the fresh enabled chain. Without an active step or queue
  position, active-lane cards report the newest recorded activity time and
  explicitly flag ten minutes of silence as a possible hang.

### Post-step lifecycle and ownership

The separated control-plane path preserves the same ownership rule:
definitions live in the Task Server, while execution lives in
`orchestrator-engine`. The Engine receives the task payload and prior stage
results only through the public API. Its `engine.env` contains bootstrap
connectivity, identity/credential, lease timing, and concurrency caps, never
project flow definitions, model routing, or gate policy.

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
  so a report-only planning/research card or docs-only concept card is not read
  as missing work. The
  "deliverables missing" verdict is legitimate ONLY when the branch diff is empty
  AND `results/` has no artefacts AND no external deliverable (e.g. a `docs/`
  commit) is documented. `AspectRunInputs` / `CodeReviewStepRequest` carry the
  `ResultsInventory` + `CardMode` fields; the `{{results_inventory}}` and
  `{{card_mode}}` slots render them in every aspect + code-review template.
- A fenced remote completion persists `review-subject.json` with its exact
  `RunAttemptId`, `ResultSha`, delivery ref, and actual integration branch ref.
  A reissue or transition into a new local or remote run invalidates the
  canonical sidecar. Before any already-integrated shortcut, acceptance trusts
  it only when its task key, attempt, and result SHA match the authority store's
  current settled RunAttempt. Both
  `post-build-test-gate` and `post-code-review-grade` use that SHA as their
  authoritative subject. The build gate's selected subject is carried through
  the later aspect and grade steps. The grade reviews the full
  merge-base-to-`ResultSha` task range, not only the result commit, and must not
  fall back to the canonical task-branch HEAD when the runner delivered a
  different commit. Merge and integration projections use the recorded branch
  ref, not the current project default. Otherwise the pipeline could test one
  revision, review another, omit earlier commits from a multi-commit delivery,
  or merge the reviewed result into the wrong line.
- `post-orchestrator-review` is an early completeness gate. It must never render
  as a final verdict.
- `post-orchestrator-decision` is the single final orchestrator verdict.
- Automatic quality-grade reviews follow the council contract. Every grade
  artifact receives an explicit orchestrator reaction. Grade A with no named
  deficiencies records `Accept, nothing open.` A review that names concrete
  deficiencies records one `FixNextRound`, `Accept`, or `Escalate` assessment
  per finding. `FixNextRound` reissues the same card within the shared loop
  budget and writes only the selected finding sentences to
  `orchestrator-follow-up.md`; exhausted budget escalates every remaining
  finding. A B/C/D response without the required concrete finding sentences is
  never treated as clean: it escalates the missing handoff because no safe,
  targeted round can be formed. When a deterministic build/test failure already
  reopens the same attempt, that follow-up includes both the build output and
  the selected council findings. The sibling `*.council-reaction.json` and the
  action decision journal entry are the read-side chain for review -> reaction
  -> target task/run. Task-detail renders this reaction on the review row. A
  legacy or manually triggered review without a sidecar shows an explicit
  `No orchestrator reaction recorded` audit state instead of silently omitting
  the reaction.

  This is a load-bearing review-orchestration contract, not optional reporting.
  The terminal routing is fixed:

  | Review outcome | Orchestrator reaction | Lane effect | Required durable evidence |
  |---|---|---|---|
  | Grade A, no findings | `Accept, nothing open.` | Continue through the remaining gates | Reaction sidecar on the grade artifact |
  | Named findings, loop budget available | One `FixNextRound` assessment per finding | Reissue the same task to `2-ready` | Sidecar, decision-journal record, targeted `orchestrator-follow-up.md`, and target task/run |
  | Named findings, loop budget exhausted | One `Escalate` assessment per finding | Move to `5e-escalated` | Sidecar and decision-journal record |
  | Grade B/C/D without concrete finding sentences | Escalate the missing handoff | Move to `5e-escalated` | Sidecar explaining why no safe targeted round can start |

  A task is not accepted merely because the letter grade is passing. Named
  findings take precedence over the grade letter. Completion, build/test,
  evidence, solution-quality, and council decisions share the same bounded
  reissue budget; the council reaction runs before generic evidence routing so
  its concrete finding sentences remain the next-round assignment.
- `post-code-review-grade` is the automatic quality-grade step (ASS-1657). It is
  `DefaultEnabled`, runs after the four aspect reviews and before
  `post-orchestrator-decision`, and assigns every pipelined task an A/B/C/D grade
  with the rubric: A solves the goal completely with tests/evidence, B is solid
  with small gaps, C has concerns (half-done/unclear), D misses the goal or
  redundantly redoes existing code. The grade token is reporting-only: it
  surfaces as a `code-review:grade-{a..d}` card tag plus a rendered detail file.
  A D records a `Failed` step row so it stands out in the Overview, and A-C
  record `Passed`. Named findings from that review are inputs to the separate
  council decision above and can therefore start a bounded round. The grade
  model is quality-first: it defaults to the
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
- Task detail renders pending, non-deferred rows as `Not run` when a settled
  attempt used a lightweight path or escalated before the full pipeline ran.
  Deferred rows remain `Pending`. The Result view has one verdict badge, and
  every human-review escalation writes a minimal Result scaffold before moving
  the task so preparation failures retain durable evidence.
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
- `PipelineHealthService` is the visibility-only sensor for pipeline-wide
  failure modes. `BuildTestGateRunner` reports acquired/completed pairs into
  it, and the service reads the existing append-only `lane_changed` ledgers.
  It never cancels a gate or moves a task. Code-owned conventions are a
  30-minute acquired-without-completed budget, three consecutive matching
  `failure_fingerprint` values on distinct cards, and a one-hour lane drain
  window that alarms when at least two cards have waited for 15 minutes with
  zero exits. Environmental retries of one card count once for the cross-card
  fingerprint sequence. Alarms append as `alert` / `pipeline-health` rows in
  the orchestrator feed; `GET /api/projects/{projectName}/pipeline-health`
  supplies the compact Pipeline page block with the active gate, global
  fingerprint streak, and completed/hour for each observed lane. This is
  sensor and alarm behavior only. Gate termination remains owned by the
  separate post-acquisition watchdog.
- Abort review is contract-bounded: the model returns a verdict, while
  `PostAbortReviewDecider` owns the binding action and rerun budget.
- The lightweight report pipeline is selected from canonical task mode
  `planning` or `research`. It retains deterministic preflight, one core report
  run, primary-report validation, and human-review handoff. It excludes git,
  build, automated tests, Stylelint, code-review aspects, code-quality grading,
  regression radar, Wiki automation, and drift checks. Research additionally
  requires `results/report.html`; the full deliverable and prompt contract is
  the [Research task delivery convention](../../operations/research-deliverables/index.html).
- The concept pipeline is distinct from the report-only pipeline. It runs in an
  isolated worktree, permits a diff only inside one
  `docs/operations/<topic>/` directory, and never merges that task branch.
  Workbench placement publishes `workbench.json` plus `index.html` through the
  managed project-artifact commit boundary. Concept review checks alternatives,
  recommendation, evidence, open decisions, and implementation-card source
  data. It deliberately does not run build, test, code aspects, or integration.
  A complete Workbench moves to `5-human-review` with a durable
  `concept-sight-review` marker. `DONE` and `NEEDS_INPUT` both count as
  successful delivery at this gate. Sight-review acceptance completes the
  source card; `POST /api/tasks/{id}/promote-concept` additionally creates the
  selected coding cards from the published document.
- A `Deferred` step (e.g. `post-merge-into-develop`) is fully implemented but
  runs only on an external operator trigger, not automatically in the
  post-bracket. It is distinct from a `Stub`: a stub has no implementation and
  renders "planned", a deferred step renders "pending" until triggered. The
  merge into develop runs on `AcceptedIntegrationWorker` while the card remains
  in Human Review with phase `integrating`. It is the acceptance transaction's
  gate: only `Merged` or `AlreadyMerged` commits the move to Completed. A
  conflict is a visible `Failed` outcome with conflicted files in the verdict
  summary; the phase clears, the card remains in Review, and the working tree is
  left clean. Once merge/gate/rollback starts, host cancellation
  does not interrupt that consistency boundary. `/healthz/drain` reports
  `gate-busy` while the boundary is active so the external stable restart
  watcher can wait for a bounded drain window. The paired
  `post-merge-into-develop-push` step (AGT-1999) pushes the integration branch to
  `origin` after a successful merge; it is offloaded off the request path and
  never force-pushes. A push
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
  step-shape pin (opt-in Tool step, after wiki-learnings and before the decision
  in the standard pipeline, omitted from the lightweight report pipeline).
- Task-spawner changes need `TaskSpawnerPostStepTests` (relevance sentinel parse
  yes/no/unparseable, dedup-ledger budget + same-target block, best-available-model
  default, and the end-to-end runner writing the follow-up card into a target
  project's flat store with a `relatedTo` back-reference) plus the
  `PipelineCatalogueTests` step-shape pin (opt-in Orchestrator step, after aspects
  and before the decision in the standard pipeline, omitted from the lightweight
  report pipeline).
- Frontend pipeline rendering changes need Playwright or component coverage plus
  screenshots when the user-facing view changes.
- Pipeline health changes need `PipelineHealthNightReplayTests`, the
  `pipeline-health-block` component spec, and the mocked night-alarm screenshot
  in `pipeline-page-evidence.spec.ts`.
