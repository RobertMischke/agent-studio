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
| `integration` | Delivery integration spans, wherever they occur | `integration_started` to `integration_succeeded` / `integration_failed` / `integration_overridden`; else the `post-merge-into-develop` (+ `-push`) step whose end lies within three minutes of the outcome row | unknown duration is reported as `integration-duration-unknown`, the attempt still counts |
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
| `integrationAttempts` | `integration_succeeded` / `integration_failed` / `integration_overridden` rows, excluding `delivery-gate-failed` (the review failed, no merge was tried) |
| `integrationOutcome` | `details.outcome` of the last integration row (`Merged`, `AlreadyMerged`, `MergedAfterRebase`, `AlreadyIntegrated`, `GateFailed`, `Conflict`, `Error`, `delivery-gate-failed`, ...) plus `integrationStage` (`pre-human-review` for integrate-on-delivery, `acceptance` for the human accept transaction) |

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

## Performance

The service enumerates tasks through the index-cache-backed scanner
(`ScanAllAutomationJobsWithArchive`, never a cold walk), reads
`logs/timeline.jsonl` and `pipeline-execution.json` once per task, and memoises
the per-task row against both file stamps plus lane and `enteredLaneAt`. A
bounded window skips terminal tasks whose `enteredLaneAt` (the terminal-lane
entry, which follows completion) lies before the window. The per-project result
is cached for 15 seconds. Measured on the Agent Studio store (1667 tasks): about
3 seconds cold for `all`, under 200 ms afterwards.

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
- The acceptance transaction records `integration_started`, but integrate-on-
  delivery does not; its duration comes from the pipeline merge step. Emitting
  `integration_started` there as well would remove the pairing heuristic.
- Local post-processing (the in-process worker) writes pipeline steps but no
  `post_step_*` ledger rows; the remote projector writes both. Teeing the local
  steps into the ledger would make the ledger the single source for both flows.

## Living knowledge log

- 2026-08-23: first build. Agent Studio, last 7 days (17 completions): queue
  wait median 28 min, coding 1.7 h, post-processing wait 1.2 d, build/test gate
  2.1 h, review aspects 3.2 h, integration 2.2 min, human review 2.1 d; lead time
  median 3.8 d. The post-processing wait and human review dominate; the gate is
  the largest compute-bound stage.
