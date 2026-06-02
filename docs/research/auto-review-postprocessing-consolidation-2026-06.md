# Auto-Review Consolidation: synchronous orchestrator post-processing, review as its own task/step

Status: research / design proposal. No production code lands in this task.
Author target: implementer of the `post-processing-orchestrator-lane` epic (roadmap "Orchestrator Post Processing", ASS-176) and a future ADR that supersedes the decoupled-review decision in ADR-0025/0026/0027.
Scope: pure design. This document maps the current code, diagnoses why `4-auto-review` stalls, and proposes consolidating the review into the synchronous orchestrator post-processing as its own task/step, decoupled from runner throughput.

## 1. Problem

The `4-auto-review` lane is not drained reliably. Cards pile up and the lane header often shows `Last tick: 0 queued, 0 accept, 0 reissue, 0 escalate`. The auto-review work runs in a background tick that is decoupled from the run and lags behind it.

The operator wants three things:

1. Consolidate auto-review and run it synchronously as part of the orchestrator post-processing, instead of a lagging, decoupled side-tick.
2. Model review as its own task/step, clearly demarcated, visible, with its own status and lifecycle, not implicitly hanging off the coding run.
3. Decouple review from runner throughput. At the latest after the drift (post-)step, the next coding task may already start. Review must not block the pickup pipeline; it continues as its own task while the runner picks up the next coding task.

## 2. Current architecture (with anchors)

### 2.1 The coding runner loop (Layer 1)

`ProjectRunner.TickAsync` ([backend/Services/Runner/ProjectRunner.cs:464](../../backend/Services/Runner/ProjectRunner.cs)) is the per-project pickup loop. It picks a task from `3-progress` (resume-first) or `2-ready`, then `RunCliAsync` spawns the coding CLI. On a successful terminal sentinel the finalize block moves the task `3-progress -> 4-auto-review` through `TaskTransitionService.MoveAsync` ([ProjectRunner.cs:2583-2627](../../backend/Services/Runner/ProjectRunner.cs)) and the `finally` block clears the active-job latch (`_activeJobId = null`, [ProjectRunner.cs:2676-2690](../../backend/Services/Runner/ProjectRunner.cs)).

Load-bearing fact: the runner is already free for the next task as soon as the move completes. The active-job latch (one coding CLI per project, ADR-0001) is released in `finally`, independent of any review. The pickup comment at [ProjectRunner.cs:506-514](../../backend/Services/Runner/ProjectRunner.cs) states this explicitly: `4-auto-review` is owned by its own background service and does not block the runner tick.

### 2.2 The synchronous post-steps that already run on the transition

`TaskTransitionService.MoveAsync` ([backend/Services/Tasks/TaskTransitionService.cs:104-139](../../backend/Services/Tasks/TaskTransitionService.cs)) does, on the `3-progress -> 4-auto-review` transition:

- auto-commit + optional push,
- the lane move,
- `RunCommitAttribution` ([TaskTransitionService.cs:268](../../backend/Services/Tasks/TaskTransitionService.cs)): synchronous, deterministic, no LLM (ADR-0050),
- `TriggerDriftPostSteps` ([TaskTransitionService.cs:300](../../backend/Services/Tasks/TaskTransitionService.cs)): fire-and-forget `Task.Run`, opt-in, default-OFF (`DriftPostStepRunner`, [backend/Services/Drift/DriftPostStepRunner.cs](../../backend/Services/Drift/DriftPostStepRunner.cs)).

So the only post-steps that run at the run boundary today are commit-attribution (sync, fast) and drift (fire-and-forget, usually a no-op because it defaults off).

### 2.3 The decoupled review tick (the lag)

`ReviewDecisionOrchestrator` ([backend/Services/Runner/ReviewDecisionOrchestrator.cs](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)) is a `BackgroundService`. Its `ExecuteAsync` ([ReviewDecisionOrchestrator.cs:232](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)):

- waits `BootDelaySeconds` (default 5),
- runs a one-shot boot backfill sweep,
- then loops every `IntervalSeconds` (default 30) calling `TickOnceAsync`.

`TickOnceAsync` ([ReviewDecisionOrchestrator.cs:304](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)) walks every watched project's `4-auto-review` lane sequentially, and for each pending task routes to:

- `ProcessDoneAsync` ([ReviewDecisionOrchestrator.cs:935](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)): the multi-aspect pass (4 fast-model aspect calls via `AspectRunnerService`), then the lint-scss post-step, the regression-radar post-step, and the accept / reissue decision,
- `ProcessNoOpAsync` / `ProcessNoCompletionSignalAsync` / `ProcessBlockedAsync` / `ProcessNeedsInputAsync`: deterministic or single-call branches.

Wiring: `ReviewDecisionOrchestrator` is registered only as `AddHostedService` ([backend/Program.cs:255](../../backend/Program.cs)); it is not a singleton, so nothing can call it synchronously. `ProjectRunner` holds no reference to it (only a comment, [ProjectRunner.cs:511](../../backend/Services/Runner/ProjectRunner.cs)). `AspectRunnerService` and `AutoReviewStatusSnapshot` are singletons ([Program.cs:252-254](../../backend/Program.cs)), so the engine is reusable even though its only driver today is the poll.

### 2.4 The pipeline catalogue model (intended shape)

`PipelineCatalogue.Standard` ([backend/Services/Pipeline/PipelineCatalogue.cs:153](../../backend/Services/Pipeline/PipelineCatalogue.cs)) already names the post-run pipeline:

```
Pre:  pre-loop-guard
Core: core-agent-run
Post: aspect-* (parallel)
      -> post-git-commit-attribution (Stub; runs in TaskTransitionService)
      -> post-lint-scss
      -> post-regression-radar
      -> post-orchestrator-decision   (the auto-review decision)
      -> post-drift-* (opt-in, default-off, DependsOn = decision)
```

Note the divergence between the catalogue and the runtime wiring: the catalogue puts drift after the decision, but the runtime fires drift at the transition (section 2.2), well before the poll-driven decision runs. The catalogue describes intent; the trigger wiring is split across two execution contexts.

### 2.5 The lifecycle-phase substate model (already partly built)

`LifecyclePhases` ([src/AgentTaskboard.Shared/Models/TaskModels.cs:307](../../src/AgentTaskboard.Shared/Models/TaskModels.cs)) implements the hybrid model from [expanded-lifecycle-lanes-plan-2026-05.md](expanded-lifecycle-lanes-plan-2026-05.md). Inside `3-progress` it already defines `post-processing-running`, `post-processing-blocked`, and `awaiting-review`, plus `LifecycleSnapshot` / `LifecycleCheck` with a `PostProcessingChecks` list and an optional `lifecycle.json` sidecar. The "review as its own step with its own status" surface is half-present in the data model; nothing populates the post-processing checks yet.

## 3. Root-cause analysis (five whys)

1. Cards pile up in `4-auto-review`. Why? The review work runs in a separate background tick, not at the run boundary.
2. Why a separate tick? Historical. ADR-0025/0026/0027 introduced the review lane and a polling orchestrator, deliberately decoupled so a slow fast-model call could not stall the coding runner.
3. Why does the tick lag or stall in practice? Several compounding reasons in the code:
   - It is OFF by default: `ReviewDecisionOrchestrator:Enabled` defaults to `false` ([ReviewDecisionOrchestrator.cs:264, 289](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)). On an instance that never enabled it, the lane is never drained.
   - It polls. Even enabled, a task waits up to `IntervalSeconds` (default 30) before its first look.
   - It is single-threaded across all projects: one `TickOnceAsync` walks every project's lane in series, and the multi-aspect pass per task is itself sequential.
   - It is rate-limited: `CallsPerHour` default 30; when the budget is spent, `TickOnceAsync` does `return` mid-walk ([ReviewDecisionOrchestrator.cs:375-381, 389-395](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)), leaving the remaining cards for a future tick.
4. Why does the header read `0 queued, 0 accept, 0 reissue, 0 escalate`? `AutoReviewStatusSnapshot.BeginTick` zeroes every counter at the start of each tick ([backend/Services/Runner/AutoReviewStatusSnapshot.cs:41-53](../../backend/Services/Runner/AutoReviewStatusSnapshot.cs)). A tick that finds nothing actionable (disabled, empty lane, rate-limited, or only states it cannot progress) ends with all-zero counters. The snapshot is a per-tick global, not a per-task record, so it cannot show "this card is mid-review".
5. Stop. The leaf cause is architectural: review runs as an out-of-band poll rather than as a deterministic step in the post-run pipeline. Every symptom above is downstream of that one choice.

This is the gap the operator named. Requirement 3 (runner free after drift) is already structurally satisfied (section 2.1). The real work is requirements 1 and 2: move review from a lagging poll to a deterministic, event-driven post-processing step with its own visible status, without re-coupling it to the runner latch.

## 4. The core design tension, resolved

"Run review synchronously in post-processing" and "review must not block the runner" sound contradictory. They are not, once "synchronous" is defined precisely.

- Synchronous-to-post-processing means event-driven and deterministic at the run boundary: the moment the run finalizes, the task is handed to the post-processing pipeline, with no polling interval and no "wait for the next tick". It does not mean "on the runner's thread or under the runner's active-job latch".
- Decoupled-from-the-runner means the pipeline executes on a separate post-processing executor. The coding-runner latch (one coding CLI per project) is released as soon as the runner-owned steps finish, so the next coding task starts while review runs.

The single invariant that is preserved is "one coding CLI per project". The invariant that is explicitly relaxed (and the operator asks for this) is "post-processing must finish before the next task starts". Open question 2 in [expanded-lifecycle-lanes-plan-2026-05.md](expanded-lifecycle-lanes-plan-2026-05.md) recommended not overlapping post-processing with the next execution in V1; this design supersedes that for the review case, because requirement 3 demands the overlap.

## 5. Proposed architecture

### 5.1 A per-project post-processing executor

Introduce a `PostProcessingService` (singleton) plus a hosted `PostProcessingWorker` that drains a per-project work queue (a bounded `Channel<PostProcessingItem>`). The transition / finalize path enqueues an item at the run boundary; the worker drains it and runs the review pipeline. This replaces the recurring poll as the steady-state trigger:

- Deterministic: enqueue happens exactly when the run finishes, so review starts within milliseconds, not on the next 30s poll.
- Non-blocking: the enqueue is cheap and returns immediately; the runner clears its latch and picks the next task.
- Resilient: the boot sweep stays, but it enqueues stragglers (tasks that landed in the lane while the backend was offline) rather than being the primary path. Rate/cost limits become a guard inside the worker, not a throughput gate that drops cards.

### 5.2 The review decision engine, extracted

Pull the aspect-plus-decision logic out of `ReviewDecisionOrchestrator.ProcessDoneAsync` into an injectable `PostProcessingPipeline` (or `ReviewDecisionService`) that takes the typed inputs (`AspectRunInputs` already exists, [ReviewDecisionOrchestrator.cs:950](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)) and returns a typed decision (accept / accept-with-concerns / reissue / escalate). `AspectRunnerService` is already a singleton and is the reusable core. The deterministic NoOp / Blocked / NoCompletionSignal / NeedsInput branches move with it. The decision stays deterministic per the contract-bounded-agent pattern (ADR-0032): the aspects classify, the rule engine decides.

The state moves (accept -> `5-human-review`, reissue -> `2-ready` at top, escalate -> human-review) still go through `TaskStateMachine` / `TaskTransitionService`, preserving the single-writer rule.

### 5.3 Review as its own task/step (requirement 2)

Model the review as a first-class step using the data model that already exists:

- Phase: on entry, write `LifecyclePhases.PostProcessingRunning`; on a hard finding, `PostProcessingBlocked`; populate `LifecycleSnapshot.PostProcessingChecks` (one `LifecycleCheck` per pipeline step: aspects, lint, regression-radar, decision).
- Telemetry: keep recording each step into `pipeline-execution.json` via `PipelineExecutionLog` (the orchestrator already brackets the aspect run, [ReviewDecisionOrchestrator.cs:975-1005](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)).
- Status: replace the per-tick global `AutoReviewStatusSnapshot` with a per-task status derived from the phase + the pipeline-execution record, surfaced on the task's API shape. The lane header then reads the live per-task state ("Reviewing X, step 3/4") rather than a reset-every-tick counter.

Two ways to place the lane:

- Option A (lane-as-is, stepping stone): keep moving to `4-auto-review` immediately at the run boundary, but trigger the pipeline synchronously (event-driven) from the enqueue instead of the poll. Smallest change; `4-auto-review` stays the durable on-disk record.
- Option B (phase model, target): keep the task in `3-progress` with `phase = post-processing-running`, run the whole pipeline on the executor, and move to `5-human-review` (accept) or `2-ready` (reissue) when it finishes. `4-auto-review` becomes a transient/legacy lane that the phase subsumes. This is the end state the lifecycle-lanes plan and ASS-176 point at.

Recommendation: ship Option A first (it retires the poll with minimal risk), then converge on Option B under the lifecycle-lanes epic.

### 5.4 Ordering and the "runner free after drift" boundary (requirement 3)

Two execution contexts, with a clean handoff:

```
[runner latch held]   Core agent run
                      auto-commit (+ push)
                      lane move (3-progress -> review/phase)
                      commit-attribution (sync, deterministic, fast)
---- enqueue post-processing item; release runner latch ----   <= runner free here
[post-processing      drift dimensions (opt-in, independent, never gates the decision)
 executor, decoupled] aspect pass -> lint -> regression-radar -> decision
                      phase/status updates; final lane move
```

The runner is free as soon as commit-attribution finishes, which is at or before drift, satisfying "at the latest after the drift step the next task may start". Drift is opt-in and independent of the accept/reissue decision (it already never gates it), so whether it runs before or alongside the aspect pass is a scheduling detail, not a correctness one. Recommendation: run drift as an independent post-step on the executor, and the decision after the aspect pass; do not make the decision wait on drift.

### 5.5 Crash recovery

Today the boot sweep re-drives anything stuck in `4-auto-review`. In the target, a mid-flight post-processing item must survive a restart. Use the existing `CompletionMarker` / `lifecycle.json` to record "post-processing in progress" so a boot-time resume pass re-enqueues unfinished items. This keeps the existing guarantee (a crash between run-end and review cannot strand a task) without the polling loop.

## 6. Concrete code-change map (per file)

- `backend/Services/Runner/ProjectRunner.cs`: in the finalize block, after the move, enqueue the task onto the post-processing channel. Keep the `finally` latch clear unchanged.
- `backend/Services/Tasks/TaskTransitionService.cs`: keep `RunCommitAttribution` synchronous; keep / relocate `TriggerDriftPostSteps`; add the enqueue/handoff hook for the review pipeline (the single seam where the boundary is crossed).
- New `backend/Services/Pipeline/PostProcessingService.cs` + `PostProcessingWorker` (hosted): the per-project queue and drainer; orchestrates aspects -> lint -> regression-radar -> decision; writes phase, `lifecycle.json` checks, and pipeline-execution rows.
- New / extracted `ReviewDecisionService` (or `PostProcessingPipeline`): the decision engine lifted out of `ReviewDecisionOrchestrator.ProcessDoneAsync` and the deterministic branches, made injectable and unit-testable.
- `backend/Services/Runner/ReviewDecisionOrchestrator.cs`: demote to (a) the boot/backfill sweep that enqueues rather than processing inline, plus (b) thin delegation to the extracted service; retire the 30s recurring loop in steady state. Eventually fold entirely into `PostProcessingService`.
- `src/AgentTaskboard.Shared/Models/TaskModels.cs` (`LifecyclePhases` / `LifecycleSnapshot`): no shape change needed; the post-processing phases and check list already exist. Add the writer that populates them.
- `backend/Services/Runner/AutoReviewStatusSnapshot.cs`: replace the per-tick global with a per-task status projection (or keep it for the boot sweep only and add a per-task surface).
- `backend/Program.cs`: register `PostProcessingService` (singleton) + `PostProcessingWorker` (hosted). `AspectRunnerService` is already a singleton.
- Config: `ReviewDecisionOrchestrator:Enabled` becomes the pipeline enable flag. Flipping it default-on is a behavior change and must be gated by the new ADR and a per-project setting; keep the `AspectsEnabled` kill-switch.

## 7. Phased implementation plan (epic sub-tasks)

Each phase ships with tests and a doc update. Worker CLIs do not commit; the platform owns the commit/push boundary.

1. Extract the decision engine. Move the aspect-plus-decision and deterministic branches out of `ReviewDecisionOrchestrator` into an injectable `ReviewDecisionService`. No behavior change; the poll still calls it. Existing `ReviewDecisionOrchestratorTests` and `ReviewDecisionOrchestrator_NoWrapperCardTests` must stay green.
2. Event-driven trigger. Add the per-project queue + hosted worker; enqueue from the finalize/transition path; the worker calls the Phase-1 service. Keep the poll as a boot/backstop only. Make status per-task. Regression tests: a DONE task is reviewed within a small bound without waiting a poll interval; the runner picks the next task while review runs.
3. Lifecycle-phase modelling. Write `post-processing-running` / `post-processing-blocked` and populate `PostProcessingChecks`; surface per-task review status on the API. (Frontend lane work is a separate roadmap item.)
4. Retire the poll. Replace the recurring loop with a boot-resume pass; add crash-recovery resume of mid-flight items; flip the enable flag default-on behind the ADR + per-project setting.
5. (Optional) Converge on Option B. Collapse `4-auto-review` into the `3-progress` post-processing phase; align drift / lint / regression-radar ordering and the Overview pipeline view with the phase model.

## 8. Test strategy

- Determinism: enqueue-at-boundary leads to review starting without a poll wait (assert a tight time bound or a deterministic test seam, not a sleep).
- Decoupling: extend `ParallelLanesPickupTests` / `PipelineExecutionParallelTests` to assert the runner picks the next coding task while a review item is in-flight on the executor.
- Per-task status: counters and phase reflect one task's pipeline, not a reset-every-tick global.
- Crash recovery: a restart mid-post-processing resumes the item exactly once.
- No regression: the extracted decision service reproduces every existing decision-orchestrator test verdict (accept / accept-with-concerns / reissue-on-block / NoOp / Blocked / NoCompletionSignal / NeedsInput).

## 9. Acceptance criteria (refined from the draft)

- `4-auto-review` (or the `post-processing-running` phase) is drained deterministically: a DONE task enters review within a small bound of the run finishing, not on the next 30s poll. Per-task counters move.
- Review is triggered synchronously (event-driven) by the orchestrator post-processing, not a recurring side-tick. The recurring poll is retired or demoted to a boot/backstop.
- Review is its own task/step: its own phase, its own pipeline-execution record, and a per-task status surfaced via the API.
- After the runner-owned steps (commit-attribution; drift is independent/optional), the coding runner is free: the next coding task starts while review runs. The decoupling test is green.

## 10. Epic / roadmap mapping

- This belongs under the ROADMAP "Expanded Lifecycle Lanes" theme, specifically "Orchestrator Post Processing" (ASS-176). The queued `post-processing-orchestrator-lane` task is the umbrella; this document is the auto-review-consolidation design slice within it.
- It supersedes the "decoupled background review tick" decision recorded in ADR-0025/0026/0027 with "event-driven post-processing pipeline". When Phase 2 lands, add an ADR (or supersede ADR-0025) recording the change and the preserved invariant (one coding CLI per project) versus the relaxed one (post-processing overlaps the next execution).
- The contract-bounded-agent rule (ADR-0032) is preserved: aspects classify, the rule engine decides; the agent never decides to halt or requeue directly.

## 11. Open questions

- Lane vs phase: keep `4-auto-review` as a durable lane (Option A) or collapse it into a `3-progress` phase (Option B)? Recommendation: A as a stepping stone, B as the target under the lifecycle-lanes epic.
- Default-on vs per-project opt-in for the synchronous pipeline once the poll is gone.
- Where the rate/cost budget lives once enqueue replaces poll, and how a burst of completions is smoothed (queue depth, per-project fairness).
- One shared post-processing worker versus one worker per project. A shared worker is simpler; a per-project worker gives clean fairness and matches the per-project runner model.

## 12. Out of scope (intentionally)

- Implementing the pipeline. That is the follow-up tasks above.
- Changing the multi-aspect prompt content or the `[[ORCHESTRATOR_DECISION]]` grammar.
- Frontend lane grouping / collapse (a separate roadmap item).
- Per-task parallel coding work or branch/worktree management (hard product boundaries).

End of design.
