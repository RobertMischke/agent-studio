# Orchestrator-Prep as an active-flow pipeline step: retire the `1a-orchestrator-prep` lane

Status: research / design proposal. No production code lands in this task.
Author target: implementer of the prep-into-pipeline slice (sibling of the auto-review consolidation under the "Expanded Lifecycle Lanes" theme) and a future ADR that supersedes the lane decision in ADR-0026.
Scope: pure design. This document maps the two prep mechanisms that exist today, diagnoses why prep is modelled as its own backlog lane rather than a pipeline step, and proposes converging both into a single optional, parallelizable Pre pipeline step that runs in the active flow before the coding run, uses the project model, and is visible in the pipeline table.

This is the symmetric "pre side" of [auto-review-postprocessing-consolidation-2026-06.md](auto-review-postprocessing-consolidation-2026-06.md). That document moves review out of a lagging decoupled poll into an event-driven post-processing step without re-coupling it to the runner latch. This one moves preparation out of a dedicated backlog lane into a Pre pipeline step without re-coupling it to the runner latch. The invariant preserved in both is identical: one coding CLI per project (ADR-0001).

## 1. Problem

`1a-orchestrator-prep` is a backlog lane. Conceptually it is not a backlog state at all: it is an optional processing step that runs over a task before it is ready to code. Modelling it as a lane has three costs:

1. It widens the backlog with a state that is really a step. The operator sees a lane whose only job is "the orchestrator is thinking about this card", which belongs in the pipeline view, not the board.
2. It is invisible to the pipeline table. The pipeline (ADR-0045) already renders every pre / core / post step with status and duration. Prep runs entirely outside that surface, in a hosted service that writes a private `orchestrator-prep.json` sidecar.
3. There are now two prep mechanisms (section 2.1 and 2.2) that overlap and diverge. One is lane-based and heuristic-only; the other is phase-based, decoupled, and already proves the parallelizable shape the operator wants. Neither uses the project-configured model.

The operator wants five things (the task's acceptance criteria):

1. No `1a-orchestrator-prep` lane in the backlog.
2. Prep appears as an (optional) pipeline step, like every other step.
3. Prep runs in the active flow before the actual start (the transition into `3-progress`), and is parallelizable: it must not block throughput (cf. the ASS-619 decoupling requirement).
4. Prep respects the project model selection (not a hardcoded model).
5. The pipeline table shows the prep step including status and duration.

## 2. Current architecture (with anchors)

### 2.1 Lane-based prep (ADR-0026)

`OrchestratorPrepHostedService` ([backend/Services/Supervisor/OrchestratorPrepHostedService.cs:35](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)) is a `BackgroundService`. Per project it:

- refills: when `2-ready` is below `Orchestrator:QueueFloor` (default 2), pulls the head of `1-preparation` into `1a-orchestrator-prep` ([OrchestratorPrepHostedService.cs:122-134](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)),
- decides: runs the pure-rule engine `OrchestratorPrepRules.Decide` ([backend/Services/Runner/OrchestratorPrepRules.cs](../../backend/Services/Runner/OrchestratorPrepRules.cs)) over the prompt and writes `orchestrator-prep.json` with the iteration / verdict / clarity ([OrchestratorPrepHostedService.cs:172-176, 231-249](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)),
- routes: `Accept` -> `2-ready`, `Bounce` -> `1b-needs-human-review`, `Iterate` -> stay in `1a` with the counter bumped, `Hold` -> no move ([OrchestratorPrepHostedService.cs:179-207](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)).

Load-bearing facts:

- It is OFF by default: `Orchestrator:PrepEnabled` defaults to `false` ([OrchestratorPrepHostedService.cs:72](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)). On an instance that never enabled it, the lane never moves.
- It is heuristic-only. `OrchestratorPrepRules.ScoreClarity` is a pure function over prompt text; it makes no model call, so the project model is irrelevant to it.
- It is gated by autonomy level ([OrchestratorPrepHostedService.cs:111-112](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)): level 0 (manual) never advances a card.
- It already runs decoupled from the coding latch. The class comment is explicit: prep "is its own pipeline phase and does not start a coding CLI ... ADR-0001's boundary (one coding CLI per project at a time) is unchanged" ([OrchestratorPrepHostedService.cs:18-28](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)). So the parallelizable property the operator asks for is already true; it is just hidden behind a lane.

### 2.2 Phase-based prep: the newer intake mechanism

A second, newer prep mechanism already exists and is the better-shaped one. `IntakeHostedService` ([backend/Services/Runner/IntakeHostedService.cs:28](../../backend/Services/Runner/IntakeHostedService.cs)) + `IntakeRunner` ([backend/Services/Runner/IntakeRunner.cs:61](../../backend/Services/Runner/IntakeRunner.cs)):

- runs over `2-ready` cards whose phase is `human-ready` or null ([IntakeHostedService.cs:93-100, 112-116](../../backend/Services/Runner/IntakeHostedService.cs)),
- evaluates a deterministic check set (`Evaluate`: blocked / duplicate / clarity / split) and returns a typed `IntakeVerdict` ([IntakeRunner.cs:96-117](../../backend/Services/Runner/IntakeRunner.cs)),
- writes the verdict as a lifecycle phase, not a lane: `intake-running` -> `intake-passed` / `intake-blocked`, plus a `lifecycle.json` sidecar with one `LifecycleCheck` per check ([IntakeRunner.cs:147-215](../../backend/Services/Runner/IntakeRunner.cs)),
- emits chat + bus events tagged `[intake]` so the activity log renders intake as its own actor ([IntakeRunner.cs:163-179, 217-229](../../backend/Services/Runner/IntakeRunner.cs)),
- is opt-in per project (`ProjectSettings.IntakeEnabled`, [IntakeHostedService.cs:57-61](../../backend/Services/Runner/IntakeHostedService.cs)) and processes one card per project per tick to bound work.

The class comment states the decoupling directly: "intake is not a coding run, so it can run while the project's coding runner is busy on a different job. The single-active-run boundary applies to `3-progress`, not to intake." ([IntakeHostedService.cs:18-26](../../backend/Services/Runner/IntakeHostedService.cs)).

So intake is already: parallelizable, decoupled from the coding latch, phase-based (no extra lane), in the active flow (it sits in `2-ready`, before the transition into `3-progress`), and surfaced in `lifecycle.json`. It is missing exactly three things the task asks for: it is heuristic-only (no project model), it is not modelled as a Pre pipeline step, and it is not rendered in the pipeline table. And it co-exists with the redundant `1a` lane.

### 2.3 The runner pickup gate

The runner only picks a `2-ready` card up into `3-progress` when intake has passed. `ProjectRunner` reads `IntakeEnabled` ([backend/Services/Runner/ProjectRunner.cs:3068](../../backend/Services/Runner/ProjectRunner.cs)) and the pickup gate `IsReadyForPickup` returns `job.Phase == LifecyclePhases.IntakePassed` ([ProjectRunner.cs:3144-3151](../../backend/Services/Runner/ProjectRunner.cs)). This is the seam where "prep must finish before the coding run starts" is already enforced, without holding the coding latch: a card waits in `2-ready` until its phase flips to `intake-passed`, and meanwhile the runner is free to pick a different card that has already passed.

### 2.4 The pipeline catalogue Pre slot (reserved, empty)

`PipelineCatalogue.BuildStandardPipeline` ([backend/Services/Pipeline/PipelineCatalogue.cs:153](../../backend/Services/Pipeline/PipelineCatalogue.cs)) ships exactly one Pre step today, the loop guard:

```
Pre:  pre-loop-guard
Core: core-agent-run
Post: aspect-* (parallel) -> git-commit-attribution (Stub)
      -> lint-scss -> regression-radar -> orchestrator-decision
      -> drift-* (opt-in, default-off)
```

The class comment names the Pre section as the home for exactly this work: "Pre-steps are reserved slots (no runtime today; future tasks plug requirement-clarification / context-retrieval / skill-readiness here)" ([PipelineCatalogue.cs:6-12](../../backend/Services/Pipeline/PipelineCatalogue.cs)). A prep step is a "requirement-clarification" Pre step by another name; ADR-0045 reserved this slot for it.

### 2.5 Pre-step recording precedent

There is already a worked example of recording a deterministic Pre step into the pipeline table. The loop guard is recorded in `ProjectRunner` via `PipelineExecutionLog`: it stamps a `PipelineStepExecution` with `StepId = PipelineCatalogue.LoopGuardStepId`, a `Passed` / `Failed` status, and a verdict ([ProjectRunner.cs:2014-2035](../../backend/Services/Runner/ProjectRunner.cs)). A prep Pre step follows the same recording pattern: a row in `pipeline-execution.json` with status + duration + verdict, which the Overview pipeline view already knows how to render (`PipelineStepExecution` carries `Status`, `DurationMs`, `Verdict`, `VerdictSummary`; see [src/AgentTaskboard.Shared/Models/PipelineModels.cs:173-207](../../src/AgentTaskboard.Shared/Models/PipelineModels.cs)).

### 2.6 The model-resolution contract already exists

`ProjectSettings.OrchestratorModel` ([src/AgentTaskboard.Shared/Models/TaskModels.cs:1145](../../src/AgentTaskboard.Shared/Models/TaskModels.cs)) is the per-project model, and `PipelineStep.Model` ([TaskModels.cs:1295-1299](../../src/AgentTaskboard.Shared/Models/TaskModels.cs)) documents the resolution order: step -> `OrchestratorModel` -> runtime default. So "prep respects the project model" needs no new contract; it needs the prep step to invoke an LLM through that existing resolution path instead of being heuristic-only.

## 3. Root-cause analysis (why prep is a lane and why there are two)

1. Prep is a lane. Why? ADR-0026 introduced `1a-orchestrator-prep` additively as a backlog lane (and `1b-needs-human-review` as the bounce target), because at the time the pipeline model (ADR-0045) did not exist; a lane was the only first-class way to make prep visible and to give it a refill / iterate / bounce lifecycle.
2. Why is it still a lane after ADR-0045 shipped the Pre slot? The Pre slot was reserved but never filled (section 2.4). Nobody migrated prep into it; the lane kept working, off by default, so there was no forcing function.
3. Why are there two prep mechanisms? Intake (ADR-driven by the expanded-lifecycle-lanes plan) was built later as the phase-based, decoupled version of "evaluate a card before coding", but it landed in `2-ready` as a phase rather than replacing the `1a` lane. The two were never reconciled: the lane mechanism (ADR-0026) and the phase mechanism (intake) now do overlapping work in different states with different data models.
4. Why does neither use the project model? Both shipped as heuristic-first slices on purpose (cheap, auditable, no LLM budget). The model upgrade was always a follow-up; the task is that follow-up plus the consolidation.
5. Stop. The leaf cause is that prep predates the pipeline model and was never migrated into it, and a second prep mechanism grew up beside it. The fix is one convergence: a single optional Pre pipeline step, running on the already-decoupled intake hosted service, using the project model, with the `1a` lane retired.

## 4. The core design tension, resolved

"Prep is a pipeline Pre step that runs before the coding run" and "prep must be parallelizable and must not block throughput" sound contradictory: a Pre step in the catalogue runs in the run's pre-section, which is on the runner thread under the active-job latch, ahead of `core-agent-run`. Putting prep there would re-couple it to runner throughput, the exact opposite of the requirement (and of ASS-619).

The resolution mirrors the post-processing one. "Pre pipeline step" is a modelling and visibility statement, not an execution-context statement.

- Pre step in the catalogue means prep is a first-class `PipelineStep` in the Pre section, recorded into `pipeline-execution.json`, and rendered in the pipeline table with status + duration. This is what satisfies criteria 2 and 5.
- Runs in the active flow before the coding run means prep evaluates a `2-ready` card and gates its pickup into `3-progress` (the existing `intake-passed` gate, section 2.3). The card cannot start coding until prep passes. This is what satisfies criterion 3a ("before the transition into `3-progress`").
- Parallelizable / does not block throughput means prep executes on the decoupled pre-processing executor (the intake hosted service), not on the runner thread and not under the coding latch. The runner stays free to pick any other already-passed card while prep runs. This is what satisfies criterion 3b, and intake already works exactly this way (section 2.2).

So the prep step is a Pre step in the model and the table, but its execution context is the decoupled pre-processing worker, not the runner's pre-section. The catalogue `Pre` entry is the contract and the telemetry anchor; the intake hosted service is the runtime. This is the same split the post-processing design draws between the catalogue `Post` entries and the post-processing executor.

The single invariant preserved is "one coding CLI per project" (ADR-0001): prep does not start a coding CLI, so it never contends for the latch.

## 5. Proposed architecture

### 5.1 One pre-processing step, two engines converged

Converge ADR-0026 lane-prep and intake into a single pre-processing step:

- The phase model wins as the substrate: prep is a lifecycle phase on a `2-ready` card (`intake-running` -> `intake-passed` / `intake-blocked`, reusing `LifecyclePhases`), not a backlog lane.
- The rule engine value of ADR-0026 (`OrchestratorPrepRules`: clarity scoring, autonomy gating, typed bounce reasons) folds into the pre-processing evaluation alongside the intake checks. Whether they stay two check families under one runner or merge into one is an implementation detail; the public outcome contract (`IntakeVerdict` / phase) is what the runner and UI pin to.
- The execution host is the existing `IntakeHostedService` (already decoupled, opt-in, one-card-per-tick), extended to record a pipeline-step row and, when configured, to make a project-model LLM call.

### 5.2 Fill the reserved Pre slot

Add a prep step to `PipelineCatalogue.BuildStandardPipeline` Pre section, ahead of `pre-loop-guard`:

```
Pre:  pre-orchestrator-prep   (Kind = Module, DefaultEnabled = false, accepts Model)
      pre-loop-guard
```

- `DefaultEnabled = false`: prep is optional and opt-in per project, matching today's `IntakeEnabled` / `PrepEnabled` defaults and the drift-step precedent ([PipelineCatalogue.cs:300-310](../../backend/Services/Pipeline/PipelineCatalogue.cs)). `PipelineStepConfigResolver.IsEnabled` already resolves a project override against this default.
- It carries a `Model` so the per-step / project resolution applies; null falls back to `OrchestratorModel` (section 2.6).
- A new `StepKind` is likely unnecessary; `Module` is the documented kind for a pre-processing module that prepares context ([PipelineModels.cs:78-85](../../src/AgentTaskboard.Shared/Models/PipelineModels.cs)). Decide between `Module` and a dedicated `Prep` kind in the implementation slice (open question 1).
- The read-only pipeline inherits it for free unless prep is git-touching (it is not), so no `GitStepIds` change is needed.

### 5.3 Run decoupled, gate pickup (criterion 3)

No change to the gating seam: the runner keeps picking up only cards whose phase is `intake-passed` ([ProjectRunner.cs:3144-3151](../../backend/Services/Runner/ProjectRunner.cs)). The prep step flips that phase from the decoupled worker. The runner thread never runs prep, so prep never blocks the coding CLI or the pickup of other passed cards.

### 5.4 Project-model selection (criterion 4)

Upgrade the prep evaluation from heuristic-only to a project-model LLM pass, resolved through `PipelineStep.Model` -> `ProjectSettings.OrchestratorModel` -> runtime default. Keep the heuristic as the deterministic floor / fallback when no model is configured or the model call fails (the contract-bounded-agent rule, ADR-0032: the model classifies clarity, the rule engine still decides accept / bounce / iterate). This preserves auditability and a zero-cost default while honouring the project model when set.

### 5.5 Pipeline-table visibility (criteria 2 and 5)

When prep runs, record a `PipelineStepExecution` for `pre-orchestrator-prep` via `PipelineExecutionLog`, exactly as the loop guard does (section 2.5): `StartedAt` / `CompletedAt` / `DurationMs`, `Status` (Passed = intake-passed, Failed/Skipped on block, Running while in flight), and a `Verdict` + `VerdictSummary` (the clarity band or the bounce reason). The Overview pipeline view renders this with no frontend shape change. The model actually used lands in `PipelineStepExecution.Model`. This replaces the private `orchestrator-prep.json` sidecar as the operator-visible surface (the sidecar can stay as an internal iteration record or be folded into `lifecycle.json`).

### 5.6 Retire the `1a` lane (criterion 1)

Follow the lane-removal precedent set by ADR-0051 (eliminate the failed-pickup lane): route-by-nature, then boot-drain, then remove the constant.

1. Route by nature: stop moving cards into `1a-orchestrator-prep`. New prep work happens as the `2-ready` phase. The refill that pulled `1-preparation` -> `1a` ([OrchestratorPrepHostedService.cs:122-134](../../backend/Services/Supervisor/OrchestratorPrepHostedService.cs)) becomes a `1-preparation` -> `2-ready` move whose card then runs prep as a phase.
2. Boot-drain: a one-shot boot pass re-homes any card still sitting in `1a-orchestrator-prep` on disk into `2-ready` (phase `human-ready`, so prep re-runs) or `1-preparation`. Keep the `TaskStates.OrchestratorPrep` constant alive during the drain era so old on-disk folders still parse.
3. Remove: once no instance has `1a` cards, delete the lane constant, drop it from `TaskStates.All`, retire `OrchestratorPrepHostedService`, and remove the lane from the board taxonomy and the `orchestrator-prep-and-autonomy` mockup.

### 5.7 The bounce destination

ADR-0026's bounce target `1b-needs-human-review` is itself a lane. Two options:

- Option A (phase, target): a hard block becomes the `intake-blocked` phase on the `2-ready` card (intake already does this). No `1b` lane. The board shows a blocked badge on the card; the operator resolves in place. This is the cleaner end state and matches intake.
- Option B (keep `1b`): retire only `1a` per the literal acceptance criterion and keep `1b` as the human-review destination for prep bounces. Smaller change, but leaves a second prep-only lane behind.

Recommendation: Option A, to avoid trading one prep lane for another. The task only names `1a`, but `1b` exists solely to serve the `1a` flow; folding it into the `intake-blocked` phase finishes the consolidation. Flag this explicitly in the slice so the operator confirms removing `1b` is in scope.

### 5.8 Crash recovery

Intake already handles the in-flight case: a restart between the `intake-running` stamp and the verdict leaves the card in `intake-running`, which the next tick re-runs and resolves ([IntakeRunner.cs:144-147](../../backend/Services/Runner/IntakeRunner.cs)). The prep step inherits this. The boot-drain (5.6 step 2) covers the legacy `1a` folders.

## 6. Concrete code-change map (per file)

- `backend/Services/Pipeline/PipelineCatalogue.cs`: add `PreOrchestratorPrepStepId` and a Pre step (Module, `DefaultEnabled = false`, carries `Model`) ahead of the loop guard in `BuildStandardPipeline`. The read-only pipeline inherits it (not a git step).
- `backend/Services/Runner/IntakeRunner.cs`: extend the evaluation to optionally invoke a project-model LLM pass (resolved via `PipelineStep.Model` -> `OrchestratorModel`), keeping the heuristic as the deterministic floor; fold in the `OrchestratorPrepRules` clarity / autonomy / bounce-reason logic.
- `backend/Services/Runner/IntakeHostedService.cs`: record a `pre-orchestrator-prep` `PipelineStepExecution` (start / status / duration / verdict / model) via `PipelineExecutionLog` on each run, so the pipeline table reflects prep.
- `backend/Services/Runner/OrchestratorPrepRules.cs`: keep as the pure decision engine; it becomes the rule layer behind the prep step's classify-then-decide contract rather than the lane service's engine.
- `backend/Services/Supervisor/OrchestratorPrepHostedService.cs`: change the refill to target `2-ready` (phase `human-ready`) instead of `1a`; add the boot-drain of legacy `1a` folders; then retire the service once the lane is gone.
- `backend/Services/Runner/ProjectRunner.cs`: no change to the pickup gate (`IsReadyForPickup` already keys on `intake-passed`, [ProjectRunner.cs:3144-3151](../../backend/Services/Runner/ProjectRunner.cs)). The loop-guard recording at [ProjectRunner.cs:2014-2035](../../backend/Services/Runner/ProjectRunner.cs) is the pattern to mirror for the prep row.
- `src/AgentTaskboard.Shared/Models/TaskModels.cs`: when the lane is removed, drop `TaskStates.OrchestratorPrep` (and `NeedsHumanReview` if Option A in 5.7) from the constants and `All[]`; keep them through the drain era. No `LifecyclePhases` shape change (the `intake-*` phases already exist).
- `backend/Program.cs`: deregister `OrchestratorPrepHostedService` at retirement; `IntakeHostedService` stays.
- Config: prep enable becomes the per-project step override (resolved by `PipelineStepConfigResolver`) plus the existing `IntakeEnabled`; reconcile with `Orchestrator:PrepEnabled` (likely retire it in favour of the step override).
- Docs / mockups: update `docs/mockups/kanban-board-design/` and `docs/mockups/orchestrator-prep-and-autonomy/` taxonomy to drop the lane; add the ADR (section 10).

## 7. Phased implementation plan (slices)

Each slice ships with tests and a doc update. Worker CLIs do not commit; the platform owns the commit/push boundary.

1. Model the Pre step. Add `pre-orchestrator-prep` to the catalogue (default-off, no runtime yet). Record it as `Planned`/`Skipped` like the commit-attribution stub. Pipeline-table renders the row. No behaviour change. `PipelineCatalogueTests` stay green.
2. Wire prep to the intake host + record telemetry. Have `IntakeHostedService` record the prep `PipelineStepExecution` (status + duration + verdict) when it runs intake. Now the existing heuristic prep is visible in the pipeline table. Still heuristic-only.
3. Project-model upgrade. Add the optional project-model LLM clarity pass behind the heuristic floor, resolved via the step / project model contract. A prompt-template / model-call change needs a live probe (ADR-0004). Record the model actually used.
4. Retire the `1a` lane. Redirect refill to `2-ready`; add the boot-drain; remove `OrchestratorPrepHostedService`; drop the lane constant from `All[]` and the board taxonomy. Decide `1b` (5.7).
5. (Optional) Fold `1b` into `intake-blocked` if Option A is chosen; update the board to render a blocked badge in `2-ready` instead of a lane.

## 8. Test strategy

- Visibility: a run with prep enabled produces a `pre-orchestrator-prep` row in `pipeline-execution.json` with a non-zero duration and a verdict; the Overview pipeline view renders status + duration (a frontend Playwright check, since this is a visible surface).
- Decoupling: extend the parallel-lanes pickup tests to assert the runner picks up an already-`intake-passed` card while a different card is mid-prep on the worker. Prep never holds the coding latch.
- Gating: a card in `2-ready` with phase != `intake-passed` is not picked up; flipping to `intake-passed` lets the runner take it. (Pins the existing `IsReadyForPickup` contract.)
- Model resolution: the prep step uses `PipelineStep.Model` when set, else `OrchestratorModel`, else the runtime default; the resolved model is recorded on the step execution.
- Decision parity: the consolidated prep reproduces the existing `OrchestratorPrepRules` verdict matrix (accept / iterate / bounce / hold by clarity band and autonomy level) and the intake outcome matrix (pass / clarification / duplicate / split / blocked).
- Migration: the boot-drain re-homes a seeded legacy `1a-orchestrator-prep` folder into `2-ready` (or `1-preparation`) exactly once; no card is stranded.

## 9. Acceptance-criteria mapping

| Criterion (from the task) | Where it is satisfied |
|---|---|
| No `1a-orchestrator-prep` lane in backlog | 5.6 (route-by-nature + boot-drain + remove constant), slice 4 |
| Prep appears as an (optional) pipeline step | 5.2 (Pre slot, `DefaultEnabled = false`), slice 1 |
| Prep runs in active flow before `3-progress`, parallelizable | 4 + 5.3 (phase on `2-ready`, gates pickup via `intake-passed`, runs on decoupled worker, never holds the latch) |
| Prep respects project model selection | 5.4 + 2.6 (step -> `OrchestratorModel` -> default), slice 3 |
| Pipeline table shows prep incl. status / duration | 5.5 + 2.5 (`PipelineStepExecution` recorded like the loop guard), slice 2 |

## 10. Epic / ADR mapping

- This belongs under the "Expanded Lifecycle Lanes" theme, as the pre-side sibling of the auto-review post-processing consolidation (ASS-176). References: ASS-176 (post-processing lane), the ASS-619 decoupling requirement (prep / review must not block runner throughput), ASS-624 (completion loop in the pipeline).
- It supersedes the lane decision in ADR-0026 (orchestrator-prep lane + `1a` / `1b`) with "prep is an optional Pre pipeline step running on the decoupled pre-processing worker; the autonomy scale and clarity rules survive, the lane does not". The autonomy scale itself (levels 0-4) is not removed, only its lane embodiment.
- It fills the Pre slot that ADR-0045 reserved.
- New ADR numbering: the archive runs through ADR-0053 ([architecture-decisions.md:1049](../architecture-decisions.md)), so a new ADR for this work claims ADR-0054. Note the existing collision between the archive's ADR-0051 (eliminate failed-pickup lane) and the standalone proposed file `adr/adr-0051-task-processing-pipeline.md`; do not reuse 0051. When slice 4 lands, add ADR-0054 recording the supersede and the preserved invariant (one coding CLI per project) versus the relaxed one (prep is a phase + pipeline step, not a backlog lane).
- The contract-bounded-agent rule (ADR-0032) is preserved: the model classifies clarity, the rule engine decides accept / bounce / iterate; the agent never moves a card itself.

## 11. Open questions

- Step kind: reuse `StepKind.Module` (its documented purpose) or add a dedicated `StepKind.Prep` for cleaner UI grouping?
- `1b-needs-human-review`: fold into the `intake-blocked` phase (Option A, recommended) or keep as a lane (Option B)? The task only names `1a`.
- Config consolidation: retire `Orchestrator:PrepEnabled` and `Orchestrator:QueueFloor` in favour of the per-project step override + `IntakeEnabled`, or keep the queue-floor refill behaviour (it is a throughput feature, not a prep feature)?
- The refill semantics: today prep refills `2-ready` from `1-preparation` when below a floor. After consolidation, is the floor-driven refill still wanted, and does it belong to prep or to a separate backlog-feeder concern?
- One shared pre-processing worker (today's `IntakeHostedService`, one card per project per tick) versus per-project fairness if prep becomes an LLM pass with real latency.

## 12. Out of scope (intentionally)

- Implementing the step. That is slices 1-5 above.
- Changing the autonomy scale (levels 0-4) or its semantics; only its lane embodiment is removed.
- The post-processing / auto-review side (its own design doc and epic).
- Frontend board redesign beyond removing the retired lane(s) and rendering the prep pipeline row.
- Per-task parallel coding work or worktree management (separate, ADR-0052).

End of design.
