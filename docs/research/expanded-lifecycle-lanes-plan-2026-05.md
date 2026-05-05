# Expanded Lifecycle Lanes Concept

Status: research, not implemented.
Author target: future implementer of the queued tasks `ready-orchestrator-intake-lane`, `post-processing-orchestrator-lane`, `kanban-lane-grouping-collapse`, and `lifecycle-substate-migration-compatibility`.
Scope: pure design. No production code lands in this task.

## 1. Problem

The current six-state lifecycle (`1-preparation -> 2-ready -> 3-progress -> 4-review -> 5-completed -> 6-archive`) collapses two distinct kinds of signal into one column:

- Human intent. The user has decided this task is shaped well enough to run, or has decided the result is finished.
- System processing. The orchestrator, a supporting agent, or a check is currently doing work that is not the main coding run.

Today `2-ready` means both "the human says this can run" and "the runner may pick it up". `3-progress` means both "a coding CLI is editing the repo" and "any system-driven work happens here". This breaks down once the product wants:

- Intake checks before pickup (duplicate detection, scope clarity, missing context, executability).
- Post-processing after the main run (QA, security, design council, runtime observability, supporting agent feedback) before the human gets the card.
- A clear seat for the human at both ends: "I marked it ready" and "I am reviewing the result".

The board should expose those phases without inviting parallel coding work and without exploding the filesystem contract.

## 2. Lane vocabulary

Five conceptual phases, mapped onto two-axis lane groups (Human vs Orchestrator/AI). The leftmost column stays preparation; the rightmost stays archive.

| # | Lane           | Owner         | Meaning                                                                                                              |
|---|----------------|---------------|----------------------------------------------------------------------------------------------------------------------|
| 1 | Preparation    | Human         | Drafting. Not committed yet.                                                                                         |
| 2 | Human Ready    | Human         | The user says this is ready to run.                                                                                  |
| 3 | Intake         | Orchestrator  | The orchestrator (or a supporting agent) is checking whether the task can be picked up safely.                       |
| 4 | Execution      | AI / coding CLI | The main coding CLI is doing the implementation. Single active run per project.                                    |
| 5 | Post Processing| Orchestrator  | Supporting checks: QA, security review, design council, runtime observability, summary generation.                    |
| 6 | Human Review   | Human         | The user reviews the final evidence and decides accept / continue / send back.                                       |
| 7 | Completed      | Human         | The user accepted the result.                                                                                        |
| 8 | Archive        | (system)      | Long-term storage.                                                                                                   |

Naming notes:

- Use `Human Ready` (not `User Ready`) and `Human Review` (not `User Review`) so the board reads as a workflow, not a user-permissions matrix.
- Use `Intake` and `Post Processing` (not `Pre Run` / `Post Run`) so the words describe what is happening, not when.
- `Execution` is the public name. Internally it is still the same thing as the existing `3-progress` "an active CLI is or was working" state; the lane label changes, the data does not.
- The roadmap currently uses `Orchestrator Intake` and `Orchestrator Post Processing`. The shorter forms above are recommended for the column header; the long forms can sit in the in-product help popover and in this concept doc. See section 12 for the proposed roadmap delta.

Roles by axis:

- Human lanes: Preparation, Human Ready, Human Review, Completed.
- Orchestrator/AI lanes: Intake, Execution, Post Processing.
- Both: Archive (system, but accessed by humans).

## 3. Ready intake flow

Trigger: the user drags a card from Preparation into Human Ready, or creates a new task with `targetState=2-ready`.

Phase entry: the card sits in `Human Ready`. The runner does not pick up tasks that are still in Human Ready unless intake has been declared not required for the project.

Intake checks (sequential, per-project, advisory by default):

1. Duplicate detection. Compare against existing tasks in `2-ready` and recent `5-completed` for near-duplicate prompts.
2. Clarity probe. Quick model pass to decide whether the prompt is executable (acceptance criteria, scope, constraints) or whether it should ask the user a question.
3. Context resolution. Confirm the watch path, repo, attachments, and skill links resolve. Surface broken references.
4. Executability shape. Check that the prompt does not request out-of-scope behavior (parallel coding work, branch management, cross-project state changes).

Outcomes:

- Pass. Card auto-promotes to Execution as soon as the project pickup loop is free.
- Hold with question. Card stays in Intake with a "needs input" badge; orchestrator writes a meta message into the chat. Human can answer in chat (re-runs intake) or drag the card back to Human Ready or Preparation.
- Hold with hard block. Card stays in Intake with a "blocked" badge plus a typed reason. The user must decide what to do. The runner must not pick up the card.

Hard rule: intake is not the runner. Intake checks may run in the same process or as supporting CLI runs, but they never start the main coding run themselves. Promotion `Intake -> Execution` is the orchestrator's decision; the runner reads the post-intake "ready for execution" signal and picks up.

V1 default is "intake passes through". If no intake check is configured for the project, the orchestrator immediately marks the card "ready for execution" and the lane behaves as a single-tick flash. The lane is still visible (counter, transient badge in the activity log) so the user understands the model.

## 4. Task Execution flow

Trigger: intake passed.

Phase entry: the runner picks the card up. The card moves to Execution with a live execution badge. This matches today's `3-progress` plus `execution.status = running`.

Phase work:

- Main coding CLI runs. One active run per project, as today.
- The runner emits sentinels (`[[TASK_DONE]]`, `[[TASK_BLOCKED:...]]`, `[[TASK_NEEDS_INPUT:...]]`, `[[TASK_NOOP]]`).
- Stop, continue, recovery, and fail behavior is unchanged.

Phase exit:

- Successful sentinel: card moves to Post Processing.
- Blocked / needs-input: card stays in Execution with the existing badges; the user answers and the run resumes.
- Stopped / failed: card stays in Execution for inspection; the user can restart, continue, or send back to Human Ready.

This lane is still the only place a coding CLI edits the repo. Intake and Post Processing must not edit source code; they may write evidence into the job folder.

## 5. Post Processing flow

Trigger: Execution emitted `[[TASK_DONE]]` (or its heuristic equivalent).

Phase entry: the card moves to Post Processing. The runner records the execution outcome and starts post-processing work. The auto-commit and summary already happen at this boundary today; they become the first two named post-processing steps.

Post-processing kinds (any subset, sequential, all advisory):

- Auto-commit (existing).
- Haiku review summary (existing `JobSummaryState`).
- Task Check skills: QA, lint, structural review, test-quality probe.
- Security review skill.
- Design council critique (when the change has a UI surface).
- Product runtime log capture and runtime observability summary.
- Token-spend rollup for the run.

Hard rule: post-processing must not edit source code. Findings are written as evidence under the job folder (`results/`, `logs/`, sidecar files). Findings may queue follow-up tasks in `1-preparation`. They never move job folders themselves.

Phase exit:

- All scheduled post-processing finished: card moves to Human Review.
- Post-processing flagged a hard problem: card moves to Human Review with a "blocked / requires attention" badge; the user decides whether to retry the task, queue a follow-up, or accept.
- Post-processing crash or hang: same as Execution stalls. The card stays in Post Processing with a typed error; the user can re-run a single check, skip it, or move the card by hand.

V1 default is "post-processing equals what the app already does today" (auto-commit + summary). Each additional check (Task Checks, security, design, observability) lands as a separate task and only moves the card's residence within the same `3-progress` filesystem state.

## 6. Human Review handoff

Trigger: post-processing finished or flagged.

Phase entry: card lands in Human Review. The "auto-reviewing" pill clears. The summary is ready. Findings chips are visible.

Human options:

- Accept: card moves to Completed.
- Continue: writes a follow-up turn; card returns to Execution with the saved follow-up applied.
- Send back to ready: drag back to Human Ready; intake re-runs.
- Send back to preparation: drag to Preparation.
- Queue a follow-up task from a finding chip.

This matches today's `4-review` lane behavior. The visible difference is that the card cannot land here until post-processing has finished or has explicitly punted, so the reviewer is not staring at a half-finished card with a stale summary.

## 7. Lane grouping and collapse

Eight columns is too many for a comfortable scan, especially on smaller screens. The board needs grouping.

Default visual model:

- Three lane groups, each with a header and counter:
  - `Preparation Group`: Preparation. (Always one column. Could be folded into the next group on narrow screens.)
  - `Ready Group`: Human Ready, Intake.
  - `Run Group`: Execution, Post Processing.
  - `Review Group`: Human Review, Completed.
  - `Archive`: Archive (always its own group, often collapsed).

(Four groups is the practical target. Preparation can sit beside the Ready Group with its own column rather than its own group on wide screens.)

Group behavior:

- Group header shows the group name plus an aggregated counter and a chevron.
- Expanded: each lane in the group renders as its own column.
- Collapsed: the group renders as a single slim column. Inside, lanes appear as horizontal "swim rows" with a per-lane counter, badge, and a small list of cards. Click expands.
- Drag and drop respects lanes, not groups. A card dropped onto a collapsed group lands in the lane that matches the drop's vertical position; if ambiguous, it lands in the leftmost lane.
- Counters always show the per-lane number; badges only highlight when something needs human attention.

Visibility chips on each card stay the same as today. The lane itself encodes most of the phase; the card chips encode CLI, owner, model, token bubble, badges.

Default user state:

- Wide screens (>= 1600 px): all groups expanded, eight columns visible.
- Medium screens (1200 - 1600 px): Ready Group and Run Group expanded; Preparation and Review Group expanded; Archive collapsed.
- Narrow screens (< 1200 px): Ready Group and Run Group collapsed by default into a slim left rail; Human Review remains expanded since it is where the user acts.
- The collapse state is per-user, persisted in localStorage. No backend change.

The "left rail" form factor matters because users will mostly be looking at Human Review and Execution, with intake and post-processing as ambient lanes that should not eat horizontal space when nothing is happening there.

## 8. Visibility versus column count tradeoff

The risk: too many columns make the board harder to scan and harder to drag into. The benefit: phases that the orchestrator drives become visible instead of hiding behind a single `3-progress` lane.

Concrete tradeoffs:

- Eight visible columns is uncomfortable. Four group headers plus optional drilldown is comfortable.
- Putting Intake and Post Processing as their own columns next to Execution makes the orchestrator's work legible. Hiding them behind a chip on the Execution card hides important state when the user has not opened the card.
- Drag targets degrade quickly when columns are narrow. A grouped lane that collapses to a swim-row form factor preserves drop targets without forcing the user to pan horizontally.
- Counters at the group level are necessary so users can ignore collapsed groups when they are quiet.

Recommendation: group by default, expand on demand, collapse aggressively when nothing is happening in the orchestrator lanes. The board should feel like the existing six-column layout for routine work and reveal more structure only when the orchestrator is busy.

Cards should also surface the active phase inline (a small phase chip on the card) so even a collapsed group communicates state in the swim rows. This avoids the failure mode where a collapsed Ready Group hides "Intake is waiting on a question" until the user opens the group.

## 9. V1 state model: hybrid

Four candidates were evaluated:

A. New filesystem states (Human Ready, Intake, Execution, Post Processing, Human Review folders).
B. Virtual lanes derived only from existing state plus an `execution.status` substate.
C. Sidecar lifecycle events (a typed JSONL stream the UI projects into lanes).
D. Hybrid: keep filesystem states as the durable skeleton, add a small sidecar / job.json `phase` field for the orchestrator-driven substates, and derive the lane from `(state, phase)` in code.

Tradeoffs:

- A is honest but expensive. It changes the filesystem contract, breaks every existing job, fragments state transitions across more folders, and tempts agents to "scan for ready" in more places. Migration is annoying. The agent task contract would need a major revision.
- B is cheap but inadequate. The current `JobInfo.Execution.Status` plus `JobSummaryState` already implies most of the lanes for free, but it cannot represent `Human Ready vs Intake` (both are filesystem `2-ready` with no execution yet) and it cannot cleanly represent `Execution succeeded but post-processing is mid-flight` other than by overloading `JobSummaryStatus`. New post-processing checks (security, design council, runtime observability) would all need their own ad-hoc fields.
- C is correct but premature. A typed lifecycle event stream is what the Agent Message Bus is becoming. Building a parallel stream just for kanban lanes duplicates that work.
- D is the smallest change that preserves the contract and supports growth. Filesystem states stay the durable, agent-readable skeleton (six folders, unchanged). A new `phase` field describes substates inside the orchestrator-driven lanes. The frontend computes the visible lane from `(state, phase, execution.status, summaryState, postProcessing)`. The Agent Message Bus, when it lands, becomes the durable record of phase transitions for free.

Recommendation: D, hybrid.

Concrete shape (proposal, subject to refinement during implementation):

- `JobInfo.state` continues to be one of `1-preparation | 2-ready | 3-progress | 4-review | 5-completed | 6-archive`. Folder semantics unchanged.
- Add `JobInfo.phase` (optional string, nullable) with values:
  - For `state = 2-ready`: `human-ready` (default) or `intake-running` or `intake-blocked`.
  - For `state = 3-progress`: `execution-running` or `execution-stalled` or `post-processing-running` or `post-processing-blocked` or `awaiting-review` (transient; promotes to `4-review` on the next runner tick).
  - For `state = 4-review`, `5-completed`, `1-preparation`, `6-archive`: null. The state already says enough.
- Add a sidecar file `lifecycle.json` in the job folder for richer phase data: which intake checks ran, which post-processing checks are scheduled, last phase transition time, current blocking reason. This file is optional. Its absence means "default phase for the state".
- Update `JobInfo` wire shape with `phase`, last phase transition time, and a small `phaseBadges` array so the frontend can render lane-specific chips without re-reading the sidecar.
- The frontend's lane projection is a pure function of `(state, phase, execution.status, summaryState)`. Existing jobs render as today because both `phase` and `lifecycle.json` are absent or default.
- The agent task contract gets one new sentence: "phase is owned by the application; agents must not write to it." No new sentinels.

This keeps the filesystem contract stable, preserves agent ergonomics, and makes the new lanes a frontend-and-orchestrator concern. The Agent Message Bus, once it lands, can subsume `lifecycle.json` by emitting `lifecycle` kind messages; the bus becomes the canonical event log and `lifecycle.json` becomes a derived snapshot.

## 10. Compatibility strategy

Existing job folders must keep rendering. Constraints:

- A job with no `phase` and no `lifecycle.json` renders in its state's default lane:
  - `2-ready` -> Human Ready.
  - `3-progress` with `execution.status = running` -> Execution.
  - `3-progress` with `summaryState.status = generating` -> Post Processing.
  - `3-progress` with neither -> Execution (matches today's behavior for stopped / failed runs).
  - `4-review` -> Human Review.
- The kanban grouped endpoint continues to return the existing six buckets. The lane projection is a pure UI concern; the wire keeps the six-state shape so dev / stable can disagree on UI without breaking server compatibility.
- Drag and drop semantics stay state-level. Dropping a card into the Intake lane writes `state = 2-ready`, `phase = intake-running`. Dropping into Human Review writes `state = 4-review`. The runner is the only producer of `phase = execution-*` and `phase = post-processing-*`.
- Agent task contract additions are advisory ("phase is application-owned"). Existing CLIs ignore the new field.
- No migration tool is required. The first time the new code reads a job without `phase`, it treats the state as authoritative and writes nothing to disk.

## 11. Recommended implementation order

The roadmap already lists five queued tasks. Suggested order, slightly tightened:

1. `expanded-lifecycle-lanes-concept` (this task). Decide naming, V1 model, compatibility.
2. `lifecycle-substate-migration-compatibility`. Land the wire-level `phase` field, the sidecar `lifecycle.json` shape, the `JobInfo` extension, and a backend test that confirms an existing job renders in the right default lane. No new lanes in the UI yet. This step is small and pays back immediately because existing post-processing (auto-commit, summary) starts populating `phase` so the frontend has data to render later.
3. `kanban-lane-grouping-collapse`. Add lane projection in the frontend, group / collapse model, swim-row form for collapsed groups, persistence in localStorage. Drives off the `phase` field landed in step 2. Existing jobs render in the default lane of their state. No orchestrator changes.
4. `ready-orchestrator-intake-lane`. Add the intake checks (start with duplicate detection and clarity probe), the orchestrator state machine for `human-ready -> intake-running -> intake-blocked / ready-for-execution`, and the chat output. The runner stops picking up `2-ready` cards that are still `human-ready` in projects where intake is enabled.
5. `post-processing-orchestrator-lane`. Generalize the existing summary / auto-commit step into a typed post-processing pipeline. Add a "different CLI identity" path so a security or design check can run as its own supporting agent. Add typed findings.
6. (Optional, after 4 and 5 settle.) `lifecycle-events-on-message-bus`. Emit `lifecycle` kind messages on phase transitions and start treating the bus as the source of truth for the phase history; keep `lifecycle.json` as a derived snapshot.

Acceptance gates between steps:

- Step 2 must ship with the "existing job folders keep rendering" regression test. This is the single hardest compatibility risk in the plan.
- Step 3 must ship with a wide-screen and a narrow-screen Playwright spec.
- Step 4 must default intake to "off / pass-through" per project; intake checks must not block pickup until the user enables them.
- Step 5 must keep auto-commit and summary as built-in post-processing kinds; adding a new check must not regress the existing review surface.

## 12. Roadmap delta

Current roadmap (`ROADMAP.md`, "Expanded Lifecycle Lanes") uses these phase names:

- Human Ready
- Orchestrator Intake
- Task Execution
- Orchestrator Post Processing
- Human Review

This document recommends shorter column labels for the board:

- Human Ready
- Intake
- Execution
- Post Processing
- Human Review

The long names stay in roadmap prose, in-product help, and in the concept doc. The short names land on the column headers and on the card phase chip. ROADMAP has been updated in this task to record the column-label split, point to the hybrid V1 state model, and re-order the queued tasks so migration-and-compatibility lands before the UI lane work.

## 13. Open questions

- Should Intake be allowed to run while another project is busy? Recommended yes: intake is not a coding run, it is a small orchestration probe. The single-active-run boundary is per project and applies to coding work, not to intake. Confirm before implementation.
- Should Post Processing be allowed to overlap with the next task's Execution in the same project? Recommended no for V1: keeping the project pipeline strictly sequential is simpler and matches the product boundary. Re-evaluate if post-processing turns into a slow check that frequently blocks the queue.
- Is "Human Review" the right name when the orchestrator may have already produced a structured verdict? The user is still the decider, so yes. Avoid "Final Review" because there can be multiple round trips.
- Where does `1-preparation` sit in the lane group model? Recommendation: its own column outside the Ready Group, because it is already a low-traffic lane and grouping it with Human Ready muddies the meaning.
- How does the existing `archiveAll` action interact with the new lanes? It still applies to `5-completed` only. No change.
- Should `phase` be a structured object instead of a string? Recommendation: start as a string for the wire, and let `lifecycle.json` carry the structure. Strings are friendlier to read in `job.json` and easy to migrate later.
- Should intake produce a normal queued task when it asks a clarification question? Recommendation: no. Intake should write a chat message and a badge; the user answers in chat (cheap). Only post-processing findings produce queued tasks.
- Lane drag-and-drop ergonomics: dropping into Intake or Post Processing is awkward (they are orchestrator-owned). Recommendation: allow drop, treat as a manual phase override (writes `phase` directly), but show a tooltip explaining that orchestrator-owned lanes usually move on their own.

## 14. Out of scope (intentionally)

- Per-task parallel coding work. Hard product boundary.
- Branch / worktree management. Hard product boundary.
- Replacing the Agent Message Bus with a separate lifecycle event stream. The bus, when it lands, will subsume the lifecycle log.
- A new filesystem state per phase. The hybrid model deliberately avoids that.
- Changing `4-review` semantics. Human Review is still `4-review`.
- Designing the specific intake or post-processing checks. They are queued as their own roadmap items.

## 15. Acceptance for follow-up tasks

Each follow-up task should leave behind, at minimum:

- A short note in `docs/research/` or the relevant concept folder.
- A regression test covering the case "existing job, no `phase`, renders in the right lane".
- A Playwright screenshot of the new lane state where it touches the UI.
- An update to `ROADMAP.md` if the task changes naming or the implementation order.
- An update to `docs/agent-task-contract.md` if the task changes what agents can do (it should not for any of the listed tasks).

End of concept.
