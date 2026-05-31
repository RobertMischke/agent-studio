# Eliminating the failed-pickup lane

Status: done (supersedes the loud-dead-letter doctrine of ADR-0028 / ADR-0029; see ADR-0051). The lane is never populated by any live path, the boot drain empties historical folders, and the board no longer renders a lane, banner, toast, or dot.

The position that drove this work: a task failing pickup is always a bug in the
pickup path, not a state the user should ever have to triage. `3a-failed-pickup`
was a dead-letter lane that surfaced those bugs. The fix is to remove the bugs at
the source so the lane is never populated, then retire the lane.

This document enumerates every code path that used to route a folder into
`3a-failed-pickup`, the file that produced it, and where it routes now. The
guiding rule for the new routing:

- A folder that carries a `job.json` is a real task. A real task is never
  dead-lettered: an interrupted run is retried (back to `2-ready`), and a task
  that genuinely cannot be started after a bounded number of attempts is
  escalated to a human (`5-human-review`), never parked in a dead-end lane.
- A folder with no `job.json` is not a runnable task. It is debris (a Windows
  file-handle race skeleton, an empty directory, a hand-made folder). Debris is
  cleaned up: deleted when the real job is provably elsewhere, otherwise archived
  to `7-archive` with its evidence intact. Debris never produces a card the user
  has to look at.
- A broken CLI (spawn failure) is infrastructure, not a task fault. The task
  waits in `2-ready` with a clear status and the runner pauses so it does not
  spin; it resumes when a human fixes the CLI.

## Every source of a failed-pickup verdict

| # | Cause | File / method | Old destination | New destination |
|---|---|---|---|---|
| 1 | Boot sweep: stale `3-progress` folder **with** `job.json`, past the resume window, no completion sentinel | `StaleProgressArchiver.SweepAsync` -> `MoveToFailedPickup(kind: Orphan)` | `3a-failed-pickup/<slug>-orphan-<date>` | **`2-ready`** (requeue the same task) |
| 2 | Boot sweep: stale `3-progress` folder that is **empty** (no `job.json`, no logs) | `StaleProgressArchiver.SweepAsync` -> `MoveToFailedPickup(kind: Empty)` | `3a-failed-pickup/<slug>-empty-<date>` | **`7-archive`** (debris, evidence kept) |
| 3 | Boot sweep: stale `3-progress` folder with a completion sentinel in the tail | `StaleProgressArchiver.RecoverViaTransitionAsync` | `4-auto-review` | `4-auto-review` (unchanged) |
| 4 | Live loop: `3-progress` folder with **no** `job.json`, real job exists downstream | `ProjectRunner.HandleStaleProgressOrphan` (twin branch) | best-effort delete | best-effort delete (unchanged) |
| 5 | Live loop: `3-progress` folder with **no** `job.json`, no downstream twin | `ProjectRunner.HandleStaleProgressOrphan` (orphan branch) | `3a-failed-pickup/orphan-<slug>-<date>` | **`7-archive`** (debris) |
| 6 | Live loop: real task (`job.json`) that exhausted `PickupFailureThreshold` silent runs because the CLI never spawned | `ProjectRunner.RerouteOverBudgetFolder` (spawn-failure cause) | `3a-failed-pickup` | **`2-ready`** + runner paused (CLI unavailable) |
| 7 | Live loop: real task (`job.json`) that exhausted `PickupFailureThreshold` silent runs although the CLI did spawn | `ProjectRunner.RerouteOverBudgetFolder` (task-shaped cause) | `3a-failed-pickup` | **`5-human-review`** (runner keeps going to the next task) |
| 8 | Live loop: session-less zombie folder that exhausted `ZombieResumeFailureThreshold` resume attempts | `ProjectRunner.RerouteOverBudgetFolder` (zombie path) | `3a-failed-pickup` | **`5-human-review`** (runner keeps going to the next task) |
| 9 | Operator action: queue-health repair sweeping no-`job.json` folders out of any lane | `ProjectSnapshotEndpoints` `/queue-health/repair` | `3a-failed-pickup/orphan-...` | **`7-archive`** (debris) |
| 10 | Existing on-disk `3a-failed-pickup` folders from before this change | (data, not a code path) | stays in `3a-failed-pickup` | drained on boot: real task -> `2-ready`, debris -> `7-archive` |

Already-correct paths that needed no change (they were the model for the fix):

- `CrashRecoveryService` Phase 1 finishes a missed transition (completion marker).
- `CrashRecoveryService` Phase 2 attaches uncommitted changes to the original
  `3-progress` job and commits them with the `chore(crash-recovery)` tag.
- `CrashRecoveryService` Phase 3 requeues an interrupted run (stale pickup lock)
  from `3-progress` back to `2-ready`. This is exactly the "a crashed run = retry
  the same task, not a new orphan card" behavior, already shipped.

## Why this does not spin forever

Removing a dead-letter lane raises the obvious question: what stops a task that
truly cannot run from looping `2-ready -> 3-progress -> fail -> 2-ready` forever?
Three bounded escalations terminate every loop:

1. **Cross-slug infra circuit breaker** (`CrossSlugInfraCircuitBreaker`): when a
   CLI fails to spawn for distinct tasks, the runner flips to `manual` and the
   tasks wait in `2-ready`. Nothing spins; a human fixes the CLI and resumes.
2. **Spawn-failure budget pause** (cause 6): a single task that burns its silent
   budget against an unspawnable CLI also pauses the runner, so even one broken
   task cannot loop.
3. **Human-review escalation** (causes 7, 8): a task whose CLI *does* spawn but
   which never produces output, or a session-less zombie, is moved to
   `5-human-review` after the bounded retry budget. That is terminal: the loop
   ends with a human, not with a dead-end lane. This escalation does **not**
   pause the runner - the folder has left `3-progress`, so there is no spin, and
   pausing would let one stuck task stall the whole project queue. (Contrast
   cause 6, where the task stays runnable in `2-ready` against a broken CLI, so
   the runner *must* pause to avoid spinning.)

The net effect is the same safety the dead-letter lane provided (a runaway never
drains the queue) without the construction-site lane the user could never make
disappear.
