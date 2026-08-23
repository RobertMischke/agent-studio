# Cycle-Time Stage Model

Status: implemented (local build, 2026-08-23). Owner surface:
`backend/Features/Projects/CycleTime/`, frontend feature
`frontend/src/app/features/project-cycle-time/`.

The Cycle Time rail of the project Deck answers one question per project: where
does the time of a completed task go, stage by stage, and how much of it sits in
the build/test gate and in integration. The view is a read model over evidence
the platform already records; it adds no new writer.

Endpoint: `GET /api/projects/{project}/cycle-time?window=7d|30d|all`. The
project handle accepts the watch-path name, the registry id (`PROJ-NNN`), or the
short code. An invalid window returns `400`, an unknown project `404`.

## Which tasks count

- Only terminal tasks (`6-completed` or `7-archive`) with a known completion
  time. The completion time is the last `lane_changed` ledger row with
  `to = 6-completed`; a `6-completed` card without that row falls back to
  `enteredLaneAt` (`completionSource = lane-entry`).
- The window filters on the completion time (`since = now - window`); `all`
  takes every completed task. In-flight tasks are counted in the coverage block
  but never enter the aggregates, because cycle time is defined on completion.
- Epics and fixtures are excluded. A task archived without ever recording a
  `6-completed` entry is counted as `excludedNoCompletionTimestamp`; for a bounded
  window this counter covers only the tasks whose terminal-lane entry lies inside
  the window, because older terminal tasks are skipped without reading their
  ledger.

## The stages

Every instant between creation and the final completion belongs to exactly one
of the additive stages, so their per-task sum equals the lead time. The rollups
overlap them and are reported separately.

| Stage | Definition | Primary source | Fallback |
|---|---|---|---|
| `preparation` | Time in `0-backlog`, `1-preparation`, `1a-orchestrator-prep` | `lane_changed` rows (`from`/`to`), initial lane from `prompt_created.details.targetState` | first `lane_changed.from`, else `0-backlog` |
| `queueWait` | Time in `2-ready` (first wait and every re-queue wait) | `lane_changed` | - |
| `coding` | Time in `3-progress` (plus `3a-failed-pickup`, `3b-code-not-complete`), all runs | `lane_changed` | - |
| `reviewWait` | Time in `4-auto-review` not covered by a review attempt | stay minus review activity (below) | whole stay when no activity is recorded (`review-start-unknown`) |
| `testGate` | Build/test gate executions inside the review run | `post_step_finished` rows with `pipelineStepId = post-build-test-gate` (`durationMs`, else finished minus started) | `pipeline-execution.json` step `post-build-test-gate` (`durationMs`, current and previous attempts) |
| `reviewOther` | Remainder of the review run: aspect reviews, grade, decision, step overhead, idle time inside one attempt | review run minus `testGate` minus integration inside the stay | - |
| `integration` | Delivery integration spans, wherever they occur | `integration_started` to `integration_succeeded` / `integration_failed` / `integration_overridden`; else the `post-merge-into-develop` (+ `-push`) step whose end lies within three minutes of the outcome row (each step pairs with one outcome). An acceptance outcome recorded after the completion move still belongs to the final stay when its start does; the span is clipped at completion | unknown duration is reported as `integration-duration-unknown`, the attempt still counts |
| `humanReview` | Time in `5-human-review`, `5e-escalated`, and a non-final `6-completed` stay, minus integration spans inside it | `lane_changed` | - |
| `unattributed` | Lead-time remainder no lane interval explains | computed | whole lead time when the ledger is missing (`no-ledger`) |
| `reviewRun` (rollup) | Review activity inside `4-auto-review`: from the first step of an attempt to the lane change; several attempts in one stay are summed, idle time between them is `reviewWait` | `post_step_*` rows grouped by `attemptId`, pipeline post steps grouped by pipeline attempt | - |
| `leadTime` (rollup) | `prompt_created` (else `task.json.createdAt`) to final completion | ledger, `task.json` | - |
| `cycleTime` (rollup) | First entry into `3-progress` to final completion | `lane_changed`; else `agent_run_started`; else `provenance.transitions` | null when never claimed |

Counts per task:

| Count | Definition |
|---|---|
| `codingRuns` | Entries into `3-progress` (retries = runs - 1) |
| `reviewRounds` | Entries into `4-auto-review` |
| `bounceRounds` | Transitions from a review lane (`4-auto-review`, `5-human-review`, `5e-escalated`, `6-completed`) back to a work lane (`0-backlog` ... `3-progress`): quality-loop reopen, operator requeue, integration recovery rounds |
| `integrationAttempts` | `integration_succeeded` / `integration_failed` / `integration_overridden` rows, excluding `delivery-gate-failed` (the review failed, no merge was tried). A row that repeats the previous row's kind and outcome within 120 s without an `integration_started` in between is the same attempt (the acceptance backstop and the recovery path both record one failure; a retry loop repeats one outcome every few seconds: AGT-2575 carries 137 such repeats) |
| `integrationOutcome` | `details.outcome` of the last integration row (`Merged`, `AlreadyMerged`, `MergedAfterRebase`, `AlreadyIntegrated`, `GateFailed`, `Conflict`, `Error`, `delivery-gate-failed`, ...), also when that row lands after the completion move (acceptance moves the card first and records the outcome a second later), plus `integrationStage` (`pre-human-review` for integrate-on-delivery, `acceptance` for the human accept transaction; derived from the lane that held the task when no explicit stage is recorded) |

Lane stays are half-open (`[entered, left)`); a row stamped exactly at a lane
change is attributed to the lane entered at that instant, never to both stays.
The final stay owns its end.
Timestamps are normalized to UTC; a `createdAt` after the completion is clamped
and flagged `clock-skew`.

## Aggregation

Per stage: `count`, `p50` (classic median), `p90` (nearest rank), `max`, `mean`,
and `total`. Duration statistics are occurrence-based: a task contributes to a
stage only when the stage occurred (seconds > 0), so `count` tells how many tasks
passed through it and the percentiles describe "when it happens". Count
statistics (`codingRuns` and friends) use every task. The composition bar
renders the stage medians side by side; medians are not additive, so the bar is
a relative profile, not the lead-time median.

## Lane transitions

Stages answer "how long"; the transition view answers "which way did the task
move, and why did it fall back". Every `lane_changed` row becomes one
transition with:

| Field | Source |
|---|---|
| `from`, `to`, `at` | `lane_changed.details.from/to`, row timestamp; a missing `from` uses the previous row's `to` |
| `direction` | lane levels: backlog 0, preparation 1 (incl. `1a`), ready 2, progress 3 (incl. `3a`/`3b`), post processing 4, human review and escalated 5, completed 6, archive 7. `backward` = lower level, `lateral` = same level (Escalated to Human Review), else `forward` |
| `dwellSeconds` | time since the previous lane change (or creation) = time spent in `from`; null when the stay start is unknown |
| `actor`, `actorKind` | ledger actor; `runner` (`remote-runner*`, `remote-claim*`), `review` (`remote-review*`), `human`, `orchestrator`, `system`, `external` |
| `cause`, `causeDetail` | classification below, with the detail the ledger carries |
| `reworkSeconds` | backward only: time until the task next reached the level it fell from or higher; null when it never did |

Causes are classified from the cause rows the platform writes within 120 s of
the lane change, then from actor and lane pair:

| Cause | Rule | Detail |
|---|---|---|
| `gate-failure` | `quality_loop_reopened` with cause `build-test-gate-fail` | the cause |
| `quality-loop` | other `quality_loop_reopened` rows (multi-aspect-block, completion-gate, evidence-gate, needs-input, solution-quality-gate, code-review-council, ...) | the cause |
| `integration-recovery` | `integration_recovery_queued`, or `integration_failed` near a system move (Conflict, AgentRoundRequired, ...) | outcome |
| `review-infrastructure` | `review_attempt_superseded` / `review_infrastructure_repeat_diagnosed` nearby, or a reason naming ReviewInfra | reason |
| `lease-recovery`, `claim-environment-retry` | actor `remote-runner-lease-recovery`, `remote-claim-environment-retry` | - |
| `acceptance-integration-failed` | `6-completed` back to a review lane with `integration_failed` nearby | outcome |
| `completed-reopen` | any other move out of `6-completed` to an earlier lane | reason |
| `escalation-requeue` | human move or `operator_requeued` from `5e-escalated` | reason |
| `operator-requeue` | human move or `operator_requeued` from `5-human-review` / `4-auto-review` (a failed acceptance integration nearby is kept as detail `after integration <outcome>`) | reason |
| `operator-move` | any other human backward move (Progress to Ready, Ready to Backlog) | reason |
| `runner-requeue` | runner or system hands a claimed task back to Ready/Preparation without a cause row (pick reverted, no run) | actor |
| `unclassified` | everything else | actor |
| forward: `promoted`, `claimed`, `delivered`, `external-completion`, `review-verdict` (detail: integration outcome), `escalated` (detail: escalation summary), `operator-decision` (Escalated to Human Review), `accepted`, `archived`, `operator-move`, `system-move` | lane pair and actor | |

The project summary aggregates the transitions of the window's tasks:
`lanes` and `cells` (from x to counts with direction), `laneDwell` (stays,
median, p90, max per lane), `bounceCauses` (moves, distinct tasks, rework known,
rework median/p90/total, top five details), and `topLoops` (eight tasks with the
most backward moves and their cause mix). Per-task transition lists are omitted
from the list response unless `detail=transitions` is set;
`GET /api/projects/{project}/cycle-time/tasks/{taskKey}` returns one task with
its full history independent of the window, which the drill-down row loads on
demand.

Real-data reading on 2026-08-23 (Agent Studio, last 30 days, 416 tasks): 5842
moves, 1187 backward in 259 tasks. Operator requeue (354 moves, rework median
1.9 h, 95 d in total) and escalation requeue (187, 52 d) dominate the leaked
time; build/test gate failures (112, rework median 10 min) and quality-loop
reopens (163, 12 min) are frequent but cheap; runner lease recovery (263) costs
seconds. Over all time the July pickup flapping shows as thousands of
`runner-requeue` moves on a few cards.

## Performance

The service enumerates tasks through the index-cache-backed scanner
(`ScanAllAutomationJobsWithArchive`, never a cold walk of its own), reads
`logs/timeline.jsonl` and `pipeline-execution.json` once per task, and memoises
the per-task row against both file stamps plus lane and `enteredLaneAt`. A
bounded window skips terminal tasks whose `enteredLaneAt` (the terminal-lane
entry, which follows completion) lies before the window. The per-project result
is cached for 15 seconds per window (`7d`, `30d`, `all` each have their own
entry, so the coverage counts of a window never depend on which window ran
before). Measured on the Agent Studio store (1668 tasks, in-process): about
1.2 s cold for `30d`, about 200 ms with warm per-task memos (two `FileInfo`
probes per examined task, 3336 probes in 340 ms). A request through the live API
can still take 1 to 3 s when it arrives while the shared task index cache is
refreshing (read-through refresh, not a cost of this read model).

## Known gaps

- Tasks that predate the `lane_changed` ledger kind (roughly the first half of
  the Agent Studio archive) have no recoverable completion time and are excluded;
  `provenance.transitions` only records pickup anchors. A one-time backfill that
  derives completion from the archive entry or the last `status.md` write would
  make them visible, at the cost of a less precise timestamp.
- The review claim is approximated by the first projected step row of the
  attempt. Recording the ReviewAttempt lease acquisition
  (`AttemptLeaseDto.AcquiredAt`) as a ledger row would separate queue wait from
  worktree materialization exactly.
- A gate step that is rerun inside one attempt is projected once (the final
  run); the earlier run lands in `reviewOther`. Projecting every execution, or
  the attempt's own start and end, would sharpen the gate number.
- The tail between the last projected step of an attempt and the lane change
  (grade, decision, merge, or a stuck attempt) belongs to `reviewOther`, because
  the attempt that moved the card still owned it; AGT-2604's final round carries
  4.7 h of such tail. An attempt-end row would separate work from that wait.
- Rows that land after the completion move (`6-completed` then an outcome
  row, or a `7-archive` stay before a reopen) are outside every stage; the
  archive stay shows up as `unattributed`.
- The acceptance transaction records `integration_started`, but integrate-on-
  delivery does not; its duration comes from the pipeline merge step. Emitting
  `integration_started` there as well would remove the pairing heuristic.
- Local post-processing (the in-process worker) writes pipeline steps but no
  `post_step_*` ledger rows; the remote projector writes both. Teeing the local
  steps into the ledger would make the ledger the single source for both flows.
- Transition causes are inferred from rows within 120 s of the lane change.
  Writing the cause onto the `lane_changed` row itself (`details.cause`,
  `details.reason` for every automatic move, not only operator moves) would make
  the taxonomy exact and remove the remaining `unclassified` residue (18 of
  8181 backward moves over all time).

## Living knowledge log

- 2026-08-23 (review): adversarial review against the live store. Fixed:
  repeated outcome rows counted as attempts (AGT-2575 reported 156, now 17),
  one pipeline merge step paired with every nearby outcome row (13 tasks),
  the acceptance outcome recorded a second after the completion move was
  ignored (22 rows showed Conflict/Error instead of Merged/AlreadyMerged, and
  the acceptance span up to completion went to human review), and the 7d
  coverage counts depended on whether a 30d run was cached. Per-task stage sums
  verified against lead time on all 684 rows; three real tasks (AGT-2674,
  AGT-2672, AGT-2604) recomputed by hand.
- 2026-08-23 (later): lane transitions added after the operator asked for the
  lane changes in the analysis: per-task transition history with dwell, actor,
  cause, and rework; project matrix, per-lane dwell, bounce taxonomy, top loops.
  First reading: operator and escalation requeues leak the most time, gate and
  quality-loop reopens are frequent but recover within minutes.
- 2026-08-23: first build. Agent Studio, last 7 days (17 completions): queue
  wait median 28 min, coding 1.7 h, post-processing wait 1.2 d, build/test gate
  2.1 h, review aspects 3.2 h, integration 2.2 min, human review 2.1 d; lead time
  median 3.8 d. The post-processing wait and human review dominate; the gate is
  the largest compute-bound stage.
