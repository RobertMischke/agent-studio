<<<<<<< HEAD
# Run liveness & slot semantics — "in progress" must mean alive

**Status:** concept 2026-07-09 (~05:40), from the operator's night-shift
complaint: *"Tasks landen in Progress, kommen in den Auto-Loop, ein anderer
Task wird gezogen, und die liegen da rum."* Companion to
[`post-processing-immediacy-and-parallelism.md`](post-processing-immediacy-and-parallelism.md)
(§2.4/2.5) and the AGT-1944 outcome taxonomy; work-loss protection is solved
(AGT-1945), this concept fixes the *state* semantics.

## 1. The observed failure shape (one night, many costumes)

- A run enters the **auto-loop / steer** machinery (continue prompts, steer
  questions) and waits — while the admission logic pulls the next card.
  Result: 5–8 cards in 3-progress, only ~3 live CLIs, the rest in limbo.
- **Steer questions in unattended runs wait forever** (AGT-1936 deadlock:
  "provide the task description" asked into the void).
- **Process death leaves the lane untouched** (backend restarts → zombie
  3-progress; the supervisor meta-cycle noticed, but its remedy was pausing
  the whole project).

## 2. The invariant

> **3-progress bedeutet: Es existiert JETZT ein lebender, arbeitender
> Prozess für diese Karte.** Alles andere ist ein anderer, sichtbarer
> Zustand — niemals stilles Herumliegen.

## 3. Rules

1. **Loop-/Steer-Wartezeiten sind eigene sichtbare Sub-Zustände**
   (`loop-waiting`, `steer-pending`) — auf Karte und Board erkennbar (Phase
   pill), nicht als normales Progress getarnt. Lifecycle-Phasen existieren
   bereits (execution-running/-stalled …) — sie werden erweitert und in der
   Slot-Logik ausgewertet statt nur angezeigt.
2. **Unattended Steer hat einen Timeout mit Fallback.** Wenn niemand die
   Steer-Frage beantwortet (unbeaufsichtigter Betrieb), gilt nach T (Default
   120 s): auto-answer aus prompt.md/Task-Kontext, wenn eindeutig; sonst
   Routing per Outcome-Taxonomie (AGT-1944): `blocked` mit klarem Grund.
   Nie unbegrenzt warten.
3. **Slot-Belegung folgt Prozess-Leben, nicht Lane.** Eine Karte in
   `loop-waiting`/`steer-pending` oder im Post-Processing hält KEINEN
   Execution-Slot; beim Fortsetzen wird der Slot neu erworben (oder sichtbar
   gewartet). Admission zählt lebende Prozesse, nicht Lane-Mitgliedschaft.
4. **Liveness-Heartbeat + automatische Demotion.** Jede 3-progress-Karte
   hat einen Run-Heartbeat. Stirbt der Prozess (Crash, Backend-Neustart,
   Kill), demotet der Runner die Karte selbsttätig binnen ≤60 s nach
   2-ready mit Grund `process-lost` (Session-Resume-Zeiger wird dabei
   geleert → kein Launch-Fail-Folgetod). Kein Zombie überlebt eine Minute;
   kein Meta-Cycle und keine Projekt-Pause nötig.
5. **Arbeit ist dabei immer sicher** — Demotion/Cancel erst nach
   Sicherungs-Commit-Pfad (AGT-1945-Invariante, deployed).

## 4. Implementation cut

| Slice | Scope |
|---|---|
| A | Heartbeat + `process-lost`-Demotion (Regel 4) — beseitigt Zombies strukturell; ersetzt den Meta-Cycle-Anwendungsfall "stuck-in-progress" |
| B | Steer-Timeout + Fallback (Regel 2) — beseitigt den 1936-Deadlock |
| C | Sub-Zustände sichtbar + Slot-Accounting auf Prozess-Leben (Regeln 1+3) — beseitigt "liegt rum, während andere gezogen werden" |

A ist unabhängig und zuerst; B klein; C berührt Admission/UI und kommt
zuletzt. Zusammen mit AGT-1944 (Routing) ergibt das: Jede Karte ist zu jedem
Zeitpunkt entweder *lebendig arbeitend*, *sichtbar wartend mit Grund und
Timeout*, oder *sauber zurücksortiert*.
=======
# Run-Liveness and Slot Semantics

Status: Concept. Slice A implemented (2026-07-10); Slices B and C are follow-up cards.

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

## Slices B and C (follow-up cards)

- **Slice B - Steer-timeout.** A run that is steered (a follow-up handed in)
  but never re-acknowledges the steer must not deadlock (belegt AGT-1936). Give
  the steer its own bounded heartbeat and recovery.
- **Slice C - Sub-states + slot accounting.** Make the `execution` /
  `post-processing` sub-states first-class and reconcile the coding-slot count
  against live run-heartbeats so a demoted/finished card frees its seat exactly
  once.
>>>>>>> origin/develop
