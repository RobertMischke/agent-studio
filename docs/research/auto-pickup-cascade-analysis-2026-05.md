# Auto-pickup cascade — post-mortem & fix (2026-05-03)

## What the user reported

> "In Stable lief die Autocue, und dann wurde der erste Task noch korrekt
> abgearbeitet. Dann hat das Ding angefangen, andere Tasks zu bearbeiten,
> und da sind mehrere gefailt. Es wurden auch mehrere Tasks in die
> Progress Pipeline gezogen ... Warum sind da einige gefailt? Warum sind
> da so viele nachgezogen, die anscheinend noch gar nicht gestartet wurden?"

Plus a side-ask: "wenn ich das nächste Mal arbeite, soll nicht aus
Ready nachgezogen werden, sondern als allererstes geguckt werden, ob
in Progress irgendwas ist, was weiter bearbeitet werden kann."

## Reconstructed timeline

Source: `agent-taskboard-workspace/projects/agent-taskboard/3-progress/<job>/logs/{cli-output.log, session-events.jsonl}` and `job.json` of the eight jobs sitting in 3-progress on 2026-05-03 evening.

| Wallclock | Job | Event | Duration | Exit | Notes |
|-----------|-----|-------|---------:|------|-------|
| 17:49:14 | das-sortieren-ist-buggy | start | — | — | sessionId 250a8957 captured |
| 17:50:13 | das-sortieren-ist-buggy | exit  | 58.9s | stopped/-1 | Mid-flight: agent was actively running Read / grep / find. External Stop call. |
| 17:50:15 | chat-progress-indikator | start | — | — | autopickup, no session captured |
| 17:50:16 | chat-progress-indikator | exit  | 0.8s | stopped/-1 | Killed before init completed. |
| 17:50:17 | chat-read-grep-…        | start | — | — | autopickup; resumed sessionId de594f85 |
| 17:50:19 | chat-read-grep-…        | exit  | 2.1s | failed/-1 | Init frame seen, then died. |
| 17:50:19 | projekt-dimensionen-…   | continue | — | — | resumed sessionId 754aa520 |
| 17:50:22 | sortieren-put-to-top    | start | — | — | autopickup, no captured id |
| 17:50:23 | chat-wechsel-…          | start | — | — | autopickup; resumed sessionId e039f210 |
| 17:52:09 | chat-wechsel-…          | exit  | 105.6s | failed/-1 | Active tool-use stream killed. |

Five fresh job folders entered 3-progress in ~10 seconds. None made it to Review. All exited with code -1.

## Why it cascaded

Two structural defects in `ProjectRunner`:

1. **No failure circuit breaker.** [ProjectRunner.TickAsync](../../backend/Services/Runner/ProjectRunner.cs) calls `GetNextReadyJob()` every tick the runner is in an auto mode and not currently busy. After each `OnCliFinishedAsync` releases the active-job latch, the next tick picks up the next ready job regardless of whether the previous N runs all exited non-success. So one user-initiated mid-flight Stop (or one watchdog kill) cleanly cascades into "every ready job gets briefly visited and abandoned."

2. **No progress-first ordering.** `GetNextReadyJob()` only scans state `2-ready`. Jobs left in `3-progress` after an external kill — which by design carry their captured session UUID in `job.json` and are *exactly* the resumable ones — are invisible to auto-pickup. They'll only restart if the user manually clicks Continue.

Combined: a single bad event doesn't just lose one run, it burns through the queue. And reopening the tool tomorrow with auto-mode on would skip past every interrupted-mid-flight job and start picking from Ready instead.

## Fix

Two small, independent additions to `ProjectRunner`:

### A. Progress-first pickup

`TickAsync` first asks `GetNextResumableProgressJob()` for the oldest job in `3-progress` with a non-empty `sessionChain` (i.e. the previous run captured a UUID). If found, it issues `RunIntent.AutoPickup` against that job — `RunPlanner.PlanRun` already produces a `--resume` plan when an AutoPickup-against-Progress job carries a session id. Only when no resumable progress job exists does it fall through to `GetNextReadyJob()`.

Resumable progress jobs are scoped to this runner's project so the per-project busy guard remains the only concurrency control.

### B. Consecutive-failure halt

A new field `_consecutiveAutoFailureCount` increments in `OnCliFinishedAsync` whenever an auto-issued run (intent was `AutoPickup`) does *not* satisfy `RunCompletionPolicy.ShouldMoveToReview`. On a successful run the counter resets to 0. When the counter reaches `AutoFailureHaltThreshold = 3`, the runner reverts to `manual` mode (via `SetMode` so it persists) and writes a chat decision message naming the three jobs.

Three was chosen so a single transient dead-UUID + immediate retry (e.g. on cold cache) does not flap the runner; three in a row is structural and warrants user attention.

## What this does NOT fix

- The original `das-sortieren-ist-buggy` mid-flight kill at 58.9s. That looked like an external Stop, not a watchdog kill, so PhaseAwareWatchdog tuning is not the lever. If a future post-mortem shows another mid-flight kill at exactly ~60s, treat it as a watchdog regression and re-tune.
- The 0.8s and 2.1s rapid-exit cases on `chat-progress-indikator` and `chat-read-grep-…`. These look like the user clicking Stop in rapid succession after seeing the cascade start. Circuit-breaker logic short-circuits the cascade so the user doesn't *need* to keep clicking Stop.
- Cross-process races between two backends watching the same workspace (Stable + dev). Out of scope here; tracked separately under instance-locking.

## Tests

`TaskRunnerPlanTests` (or equivalent) gets two new assertions:

1. `TickAsync_PrefersResumableProgressJob_OverReadyQueue` — stage one job in 3-progress with a sessionChain and one in 2-ready; assert the progress job is the one started.
2. `TickAsync_HaltsAutoModeAfter3ConsecutiveFailures` — drive three non-success completions and assert mode flipped to manual.

A success-resets-counter case completes the matrix.

## Files touched

- `backend/Services/Runner/ProjectRunner.cs` — `_consecutiveAutoFailureCount`, `GetNextResumableProgressJob`, `TickAsync` reordering, halt branch in `OnCliFinishedAsync`.
- `backend.Tests/TaskRunnerPlanTests.cs` (or similar) — three new tests.

No ADR — this is bug-class containment, not an architectural decision.
