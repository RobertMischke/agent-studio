# Run-Liveness and Slot Semantics

Status: Implemented. Slice A implemented (2026-07-10); Slices B and C
implemented (2026-07-11).

## Problem

Every run-carrying lane in this product promises one thing: a card in
`3-progress` means *work is happening right now*. The overnight logs of
2026-07-08/09 showed the promise broken in the most damaging way - **zombies**:
cards stranded in `3-progress` with no live process behind them. Each backend
restart left a fresh wave (AGT-1811/1914, later 1914/1941). Because pickup only
scans `2-ready`, a zombie in `3-progress` is never retried and never reviewed: it
just sits, occupying a coding seat that looks busy but is dead.

Worse, the naive fix - "on restart, requeue every `3-progress` card to
`2-ready`" - creates two new failures:

1. **Launch-fail cascades.** The requeued run tries to *resume* the session the
   dead process was building. That session is gone, so the CLI aborts with
   "No conversation found" (claude) / "no rollout found" (codex), which the
   classifier treats as a launch failure, which cascades (AGT-1945/1929/1930/1939).
2. **Re-running finished work.** A run can finish *and merge* and then have only
   its post-processing die with the backend (AGT-1932). Blindly demoting it to
   `2-ready` re-runs a completed, already-integrated agent run for nothing.

## Rule 4: the run-liveness invariant

> Every `3-progress` card MUST have a live **run-heartbeat**. A card whose
> owning run process is gone is a zombie and MUST leave `3-progress` within
> **60 seconds** - demoted by the runner itself, not by a human or a
> meta-cycle escalation.

Two properties make this safe rather than destructive:

### The heartbeat source is phase-aware

A run has two phases inside `3-progress`, and the heartbeat is a different thing
in each:

- **Execution:** the live **CLI process** (or an active loop step) is the
  heartbeat.
- **Post-processing:** the CLI process is already gone. The heartbeat is the
  **post-step executor** and its aspect/review child processes.

If the monitor keyed liveness on "is a CLI process alive" alone, every healthy
card in post-processing would be demoted. So the implemented signal is not the
CLI process directly but the **owning run**: the runner's active-run latch, a
live tracked CLI process, or a live owning-runner lease
(`.pickup-lock.json`, same-host pid alive / remote lease unexpired). That signal
is true throughout both phases of a healthy run and false only when the owner is
genuinely dead.

### The recovery is phase-aware

When a card has no heartbeat past the grace window, what to do depends on
whether the core agent run already finished (a durable signal:
`agent_run_finished` in `logs/timeline.jsonl`, a surviving `completion-marker.json`,
or `phase == post-processing-running`):

| Situation | Belegt | Action | Reason code |
|---|---|---|---|
| No heartbeat, run **never finished** | AGT-2006 | Demote `3-progress -> 2-ready`; **clear the session-resume pointer** | `process-lost` |
| No heartbeat, run **already finished** | AGT-1932 | Re-trigger post-processing (`3-progress -> 4-auto-review`); do not re-run the agent | `post-processing-lost` |

**Clearing the resume pointer** (`sessionName` nulled + `sessionChain`
tombstoned with `(recovery)`) is what breaks the launch-fail cascade: the retry
starts a *fresh* session instead of resuming a dead one. Both writes are
required - clearing `sessionName` alone lets `RunPlanner` re-derive the id from
the chain tail.

**Never lose work (AGT-1945).** A demotion is not a teardown. The monitor never
removes a worktree, so the task-owned `task/<id>` worktree + branch (and any
uncommitted work in it) survive untouched and are reused by the reissue
(`PrepareOrReuse`). The AGT-1945 safety-commit path in
`WorktreeTaskLifecycle.TeardownIfIntegrated` remains the guard for the *terminal*
teardown; run-liveness demotion simply does not tear down.

## Slice A (implemented)

Heartbeat + process-lost demotion. Two entry points over one pure policy
(`RunLivenessPolicy.Decide`, `backend/Shared/Runner/RunLivenessPolicy.cs`):

- **Boot adoption scan** (`RunLivenessMonitor.AdoptOnBootAsync`, run
  synchronously at boot in `Program.cs` *before* `CrashRecoveryService`): after a
  restart every `3-progress` card without a live process is acted on immediately
  (grace = 0). This replaces the meta-cycle `stuck-in-progress` escalation and
  the project-pause that used to babysit it. It runs before the legacy
  stale-lock requeue so the phase-aware decision (not a blanket requeue) is
  authoritative for the AGT-1932 case.
- **Uptime sweep** (`RunLivenessMonitorHostedService`, default 15s cadence,
  30s grace): demotes a card within the 60s budget when its owning run dies while
  the backend stays up (e.g. a foreign backend sharing the workspace crashed).

Every decision is appended to `<workspace>/logs/run-liveness.jsonl`.

### Key code

| Concern | Symbol | File |
|---|---|---|
| Pure decision core (the invariant) | `RunLivenessPolicy.Decide` | `backend/Shared/Runner/RunLivenessPolicy.cs` |
| Boot adoption + uptime executor | `RunLivenessMonitor` | `backend/Features/Runner/RunLivenessMonitor.cs` |
| Uptime cadence | `RunLivenessMonitorHostedService` | `backend/Features/Runner/RunLivenessMonitorHostedService.cs` |
| "Did the core run finish?" signal | `RunFinishedSignal.CoreRunFinished` | `backend/Features/Runner/RunFinishedSignal.cs` |
| Heartbeat probe (live owner) | `PickupLockFile.HasLiveOwner` | `backend/Features/Runner/PickupLockFile.cs` |
| Resume-pointer clear | `TaskSessionLog.MarkSessionChainRecovery` + `SetJobSessionName(null)` | `backend/Features/Tasks/TaskSessionLog.cs` |

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `Runner:RunLiveness:Enabled` | `true` | Master switch for the boot scan and the uptime sweep. |
| `Runner:RunLiveness:IntervalSeconds` | `15` | Uptime sweep cadence (clamped 5..55). |
| `Runner:RunLiveness:GraceSeconds` | `30` | Uptime silence tolerated before a missing heartbeat counts as process-lost (clamped 0..55). Boot uses 0. |

## Slice B (implemented)

Steer-timeout: **no steered / NeedsInput card waits indefinitely.** Slice A
demotes a card whose *process* is gone; a steered card is different - it is
waiting on purpose (so it is excluded from the Slice A heartbeat check) and needs
its own bounded wait plus recovery.

Belegt (2026-07-10 evening): three cards (2062/2067/2068) hung in parallel
~5 hours on steer questions whose answer was already knowable from the branch
state ("is this already implemented?" - their work was long since merged). The
runs waited unbounded because the NeedsInput wait had no timeout; the loss was
invisible because no lane moved (the watcher only sees transitions). 15
slot-hours lost.

When an auto-mode run asks a steer / `[[TASK_NEEDS_INPUT]]` question the
orchestrator cannot answer on its own (it STEERs, BLOCKs, declines, or hits the
auto-loop circuit breaker), the runner drops a durable `steer-pending.json`
marker recording when the wait started, stamps the visible `steer-pending` phase
("waiting for answer since mm:ss"), and tees an `orchestrator_steered` timeline
event. A short-cadence sweep then enforces a bounded wait over one pure policy
(`SteerTimeoutPolicy.Decide`):

| Situation | Action | Reason code |
|---|---|---|
| Attended (manual mode) | leave it - a human is answering | `attended-wait` |
| Inside the timeout | keep waiting (card shows the wait pill) | `within-timeout` |
| Timed out, answer derivable from context | auto-answer + resume the run | `auto-answered` |
| Timed out, no confident answer | route to a blocked `5e-escalated` escalation | `steer-unanswered` |

The **auto-answer** is the named 2067 case: for an "is this already
implemented?" question, the resolver checks the branch/develop state - if the
task's `task/<id>` branch is already an ancestor of the integration branch, it
answers "already integrated, finalize" and hands the answer back as a Continue
(via a queued pending intent + demote to `2-ready`). Every other question shape,
and every uncertain/errored resolve, is ambiguous -> a normal blocked
escalation with category `steer-unanswered`. "When unsure, escalate; never wait forever."

### Key code

| Concern | Symbol | File |
|---|---|---|
| Pure decision core (the bounded-wait invariant) | `SteerTimeoutPolicy.Decide` | `backend/Shared/Runner/SteerTimeoutPolicy.cs` |
| "Is this already implemented?" classifier | `SteerQuestionClassifier` | `backend/Shared/Runner/SteerQuestionClassifier.cs` |
| Durable steer-pending marker | `SteerPendingMarker` / `SteerPendingRecord` | `backend/Features/Runner/SteerPendingMarker.cs` |
| Uptime sweep executor | `SteerTimeoutMonitor` | `backend/Features/Runner/SteerTimeoutMonitor.cs` |
| Sweep cadence | `SteerTimeoutMonitorHostedService` | `backend/Features/Runner/SteerTimeoutMonitorHostedService.cs` |
| Branch-state auto-answer resolver | `SteerTimeoutResolver` / `ISteerTimeoutResolver` | `backend/Features/Runner/SteerTimeoutResolver.cs` |
| Mark a run steer-pending (marker + phase + timeline) | `ProjectRunner.MarkSteerPending` | `backend/Features/Runner/ProjectRunner.cs` |
| Blocked escalation funnel | `HumanReviewEscalation.EscalateAsync` (category `steer-timeout`) | `backend/Features/Runner/HumanReviewEscalation.cs` |

Every decision is appended to `<workspace>/logs/steer-timeout.jsonl` and mirrored
to the card's timeline (`steer_timeout_resolved`).

### Configuration

| Key | Default | Meaning |
|---|---|---|
| `Runner:SteerTimeout:Enabled` | `true` | Master switch for the sweep. |
| `Runner:SteerTimeout:TimeoutSeconds` | `120` | Bounded wait before an unanswered steer times out. |
| `Runner:SteerTimeout:IntervalSeconds` | `20` | Sweep cadence (clamped 5..55). |

## Slice C (implemented)

`execution-running`, `loop-waiting`, `steer-pending`, and
`post-processing-running` are first-class lifecycle phases. The board and task
detail show the phase; intentional waits include their elapsed time.

Execution-slot ownership follows the coding CLI process, not `3-progress`
membership. `ActiveRuns` retains the run record for finalisation after process
exit but releases its execution seat exactly once. Loop waits, steer waits, and
post-processing therefore occupy no coding slot. A loop continuation goes back
through normal admission; if another live CLI won the seat, its pending intent
is persisted and remains visibly `loop-waiting` until pickup acquires a slot.

The liveness policy pins the complementary invariant: after the grace window a
`3-progress` card without a live run heartbeat is legal only when it carries an
explicit `loop-waiting` or `steer-pending` phase. Otherwise it is recovered as a
zombie within the existing 60 second budget.
