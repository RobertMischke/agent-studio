# Escalated Lane + Decision Surface: separate "needs my decision" from "ready to accept", and drive escalation down

Status: research / design proposal. No production code lands in this task.
Scope: pure design. Maps the current escalate/accept paths, surfaces the one architectural fork that must be decided before any code is written (folder-lane vs lifecycle-phase substate), and slices the feature into shippable tasks.
Origin: ASS-1652. References the operator's "SOLL/IST-Konzept (Escalated-lane)" and "auto-review-entscheidung-konzept-v2" framings.

## 1. Problem

`escalate` and `accept` verdicts land in the same lane, `5-human-review`. Observed mix: ~13 escalate : 3 accept. Two distinct operator needs collapse into one column:

- "This needs MY decision" (a credential, a strategic call, a real conflict) - the escalate cases.
- "This is clean, I just need to nod it through" - the accept cases.

The operator cannot tell them apart at a glance, and the ratio itself is wrong: auto-review escalates far more than it accepts. Over-escalation is the disease; the mixed lane is the visible symptom. A fix that only splits the lane would tidy the symptom while leaving auto-review handing the operator work it should have resolved itself.

## 2. Current code map (with anchors)

### 2.1 Lanes are folders; substates are `phase` fields

- Folder lanes: `TaskStates` ([src/AgentTaskboard.Shared/Models/TaskModels.cs:2657](../../src/AgentTaskboard.Shared/Models/TaskModels.cs)). `AutoReview = "4-auto-review"`, `HumanReview = "5-human-review"`, plus `TaskStates.All` (the canonical ordered list every consumer reads).
- Substates inside a lane: `LifecyclePhases` ([TaskModels.cs:307+](../../src/AgentTaskboard.Shared/Models/TaskModels.cs)) - e.g. the Ready group's `intake-running` / `intake-blocked`, the Progress group's `post-processing-running` / `awaiting-review`. A substate is a `job.json.phase` field, not a folder move; it renders as an ephemeral lane only when populated (the Preparation lane is exactly this trick).

This distinction is the keystone of section 4.

### 2.2 Every escalation funnels through one place

`HumanReviewEscalation` ([backend/Services/Runner/HumanReviewEscalation.cs](../../backend/Services/Runner/HumanReviewEscalation.cs)) is the single system-initiated path into `5-human-review`. It (a) moves the folder and (b) writes a `ReviewDecisionKind.Escalate` record into the per-project decision journal so the board can explain WHY a card is parked. `HumanReviewVerdictDriftTest` mechanically forbids any other code from moving a card into the lane. Categories live in `HumanReviewEscalationCategories` (watchdog-kill, permission-blocked, environment-blocker, context-overflow, quarantined, agent-git-violation, human-decision-needed, ...).

The orchestrator's own decision escalations live in `ReviewDecisionOrchestrator` ([backend/Services/Runner/ReviewDecisionOrchestrator.cs](../../backend/Services/Runner/ReviewDecisionOrchestrator.cs)): `HandleEscalateAsync`, `EscalateNoOpAsync`, `EscalateNoCompletionSignalAsync`, `HandleAgentBlocked`. Each currently does `_stateMachine.MoveJob(current.Id, TaskStates.HumanReview, ...)` and records `ReviewDecisionKind.Escalate`. Accept lands in the same lane via `HandleAcceptAsDone` (it stamps the `orchestrator-moved` provenance tag).

So escalate and accept already diverge as **verdicts** (the decision journal distinguishes them, and `OrchestratorVerdict` is derived from it in `TaskEndpointHelpers.BuildOrchestratorVerdictLookup`); they only converge at the **destination lane**.

### 2.3 The metric source already exists

The decision journal (`ReviewDecisionLog` / `ReviewDecisionRecord`, kind `Escalate` vs `Accept`) is an append-only per-project ledger. An escalation-rate metric (AC#4) is a read-side aggregation over it - no new write path needed. This is the same shape `AutoReviewStatusSnapshot` already counts per tick ("A accept, B reissue, C escalate").

## 3. The over-escalation root cause (AC#5)

Two escalation classes, and only one of them should shrink:

1. **System escalations** (`HumanReviewEscalationCategories`: watchdog-kill, permission-blocked, context-overflow, quarantined, agent-git-violation): these are genuine "a human must look" events. They are correct and should NOT be suppressed.
2. **Orchestrator-decision escalations** (the aspect fan-out folds to `escalate` rather than `accept`/`accept-with-concerns`): this is where the 13:3 ratio is inflated. The aspect prompts and the final-verdict decision are escalate-leaning where they should be accept-leaning.

The fix for the ratio is therefore scoped to class 2: make the aspect prompts and the `[[ORCHESTRATOR_DECISION]]` aggregation prefer `accept` / `accept-with-concerns` for non-blocking concerns, and reserve `escalate` for the cases class 1 already names (credential, strategic decision, true conflict, repeated-unsafe automation). The consolidation research ([research/auto-review-postprocessing-consolidation-2026-06.md](auto-review-postprocessing-consolidation-2026-06.md) section 12) explicitly left aspect-prompt content out of its scope - this is the task that owns it.

Hard constraint: per AGENTS.md "Prompt-template changes: live probe required", changing any `prompts/runtime/` aspect template is a behavioral change against the CLI and must be live-probed (`@billable`) before it is claimed safe. This slice cannot be shipped blind from a managed run with no quota.

## 4. The fork that must be decided first: folder-lane vs phase-substate

The card proposes a new folder-lane `5e-escalated`. But the live architectural direction is moving the other way:

- [research/auto-review-postprocessing-consolidation-2026-06.md](auto-review-postprocessing-consolidation-2026-06.md) (ASS-176 epic) recommends collapsing `4-auto-review` from a durable folder-lane into a `3-progress` lifecycle **phase** (its Option B / open question 11).
- [research/orchestrator-prep-as-active-pipeline-step-2026-06.md](orchestrator-prep-as-active-pipeline-step-2026-06.md) retires the `1a-orchestrator-prep` folder-lane and converges it into a phase/pipeline step.
- ADR-0051 drain-era already retired `3a-failed-pickup` and `1a-orchestrator-prep` from the board (`GroupedJobs` keeps the fields as empty retired-lane plumbing).

Adding a brand-new hard folder-lane now cuts directly against that grain and would carry the full folder-lane blast radius (a new `TaskStates` constant ripples through `TaskStates.All`, the boot migration, the ~40 frontend files that name lanes, the drift services' lane lists, `BackendBaselineTests`, the kanban column set, lane glyph/doc/sort/filter, and the `OrchestratorVerdict` read side that today assumes escalate cards sit in `5-human-review`).

### Recommendation: model "escalated" as a substate, not a folder

Express escalated as a `5-human-review` **phase substate** (e.g. `awaiting-decision` vs `awaiting-acceptance`), rendered as an ephemeral split column the way Preparation already splits `2-ready`. This:

- gives the operator the visible split the card asks for (two columns / two groups), and
- stays on the lifecycle-phase rails the rest of the system is converging onto, so it does not need to be unwound when `4-auto-review` collapses into a phase.

Folder vs phase is a product/architecture call the operator should make explicitly before code is written, because the two paths have wildly different blast radius and the folder path conflicts with two in-flight design docs. This is the AGENTS.md "request implies an out-of-scope / direction-conflicting item, surface before implementing" case.

## 5. Decision Surface (AC#3)

Per escalated entry, four actions:

| Action | Effect | Gate |
|---|---|---|
| Verwerfen (discard) | Move to `7-archive` (or `6-completed` rejected) with a reason. | always |
| Weitermachen (reissue) | Re-issue the work back to `3-progress`/`2-ready` with the operator's steer, ticking the completion-loop budget (loop-inventory). | always |
| Manuell loesen (resolve manually) | Operator takes it outside the pipeline; card parks until they return. | always |
| Annehmen (accept) | Move to `6-completed`. | **only after integration** - disabled until the task's commits are merged into the integration branch |

The "Annehmen after integration" gate is the load-bearing constraint: the existing deterministic commit-attribution + git state (ADR-0050, `CommitAttributionService`) already knows whether a task's commits are on the integration branch, so the gate can be computed server-side and surfaced as a disabled/enabled button without a new data source.

All four route through `TaskTransitionService` / `TaskStateMachine` (single-writer rule); none bypass the funnel. Frontend work is mandatory-Playwright per AGENTS.md and carries the optimistic-UI default (ADR-0046).

## 6. Escalation-rate metric + alarm (AC#4)

- Compute `escalate / (escalate + accept + accept-with-concerns)` over a trailing window from the decision journal (section 2.3).
- Surface it on the auto-review lane header / a project metric, alongside the existing per-tick counters.
- Alarm (a `SupervisorAdvisory`, the existing Layer-2 channel) when the rate exceeds a configurable threshold, so over-escalation is loud rather than silently normalised. This is the guardrail that keeps AC#5's gains from regressing.

## 7. Slicing plan (each slice shippable + verifiable)

0. **Decide the fork (section 4).** Operator picks folder-lane vs phase-substate. Blocks all downstream slices. (This task.)
1. **Split surface.** Implement the chosen split (recommended: `5-human-review` substate `awaiting-decision` vs `awaiting-acceptance`); route orchestrator-decision `escalate` to the decision group and `accept` to the acceptance group. Backend transition + frontend split column + Playwright. System escalations (class 1) keep landing in the decision group.
2. **Decision surface.** The four actions + the integration gate on Annehmen. Frontend component + transition endpoints + Playwright.
3. **Metric + alarm.** Read-side escalation-rate aggregation + the advisory threshold. Backend unit tests.
4. **Accept-leaning v2 (billable).** Tune the aspect prompts + the final-verdict aggregation to prefer accept/accept-with-concerns for non-blocking concerns; live-probe (`@billable`) before shipping. Watch the slice-3 metric drop toward a healthy ratio.

Slices 1-3 are non-billable and independently testable; slice 4 needs quota and a live probe.

## 8. Out of scope / non-goals

- Suppressing class-1 system escalations (watchdog/permission/context-overflow/quarantine). Those are correct.
- Bypassing the `HumanReviewEscalation` funnel or the single-writer state machine.
- Re-deciding the `4-auto-review` lane-vs-phase question for the rest of the pipeline; this design only adds the escalate/accept split and aligns with whichever direction ASS-176 lands.

End of design.
