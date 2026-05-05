# Orchestrator preparation lane + autonomy scale - Mockup

Design exploration. **A click-dummy plus a taxonomy.** Goal: settle the lane shape and the autonomy semantics for the orchestrator's pre-execution review loop before any code lands, so the configurable knobs and bounce paths are not invented twice.

This folder is the spec the implementation refers back to. ADR-0026 adopts it.

## What this adds

A new lane between "human writes the task" and "queue picks it up": the orchestrator inspects every task before it becomes executable, and either sharpens it, hands it to the runner, or bounces it back to a human-review lane for clarification. The bounce vs. accept decision is gated by a per-project autonomy scale (0..4). At the top of the scale the orchestrator never bounces and the queue cannot stall on ambiguity. At the bottom it never moves anything forward without a human click.

The lane is a Layer 1 augmentation, not a new loop layer. It runs inside the orchestrator's pickup loop and respects the same sequential-per-project rule (ADR-0001).

## Lane shape (chosen migration: additive, no renames)

The prompt offered an illustrative renumbering (`2-orchestrator-prep`, `3-needs-human-review`, ..., `9-archive`). We chose a **purely additive** migration instead: two new lanes slot between `1-preparation` and `2-ready` using lexicographic sort keys, so every existing folder, code reference, frontend column id, and test fixture stays valid. The renumbering ripple through the codebase would have been the dominant cost of this change for no semantic gain.

```
1-preparation              human writes the task                           (unchanged)
1a-orchestrator-prep       NEW orchestrator validates, sharpens, iterates
1b-needs-human-review      NEW optional, hide-when-empty, low-autonomy bounce
2-ready                    queued for the runner                           (unchanged)
3-progress                 active CLI run                                  (unchanged)
4-auto-review              orchestrator reissue/escalate/accept            (unchanged)
5-human-review             optional, hide-when-empty                       (unchanged)
6-completed                                                                (unchanged)
7-archive                                                                  (unchanged)
optional: failed-pickup    only when items live there                      (unchanged)
```

Sort keys `1a-` and `1b-` order correctly after `1-preparation` (the dash, ASCII 45, sorts before `a`, ASCII 97) and before `2-ready` (`1` before `2`). The visible kanban order is the same as the prompt's numbering proposal.

UI labels stay short (one word):

| Folder                 | Label       | Phase    | Owner         | Icon |
|------------------------|-------------|----------|---------------|------|
| `1-preparation`        | Prep        | Backlog  | human         | clip |
| `1a-orchestrator-prep` | OrchPrep    | Backlog  | orchestrator  | bot  |
| `1b-needs-human-review`| NeedsClar   | Backlog  | human         | flag |
| `2-ready`              | Ready       | Backlog  | human         | box  |
| `3-progress`           | Progress    | Active   | agent         | run  |
| `4-auto-review`        | AutoRev     | Active   | orchestrator  | bot  |
| `5-human-review`       | HumanRev    | Active   | human         | eye  |
| `6-completed`          | Done        | Done     | human         | tick |
| `7-archive`            | Archive     | Done     | system        | box  |

`1b-needs-human-review` follows the same hide-when-empty rule as `failed-pickup` and `5-human-review` from the kanban spec.

## Autonomy scale

Per-project setting `Orchestrator:AutonomyLevel`, persisted in `project-settings.json` and exposed as a slider in the project header. The slider has five stops; the next pickup tick honours the new value.

| Level | Name        | Bounce policy                                                     | Iterate policy                                | Queue floor          |
|-------|-------------|-------------------------------------------------------------------|-----------------------------------------------|----------------------|
| 0     | manual      | Never moves a task out of `1-preparation` without a human click.  | Disabled.                                     | May be empty.        |
| 1     | cautious    | Bounces every ambiguous task to `1b-needs-human-review`.          | One internal sharpening pass, then commit.    | May be empty.        |
| 2     | balanced    | Bounces only the genuinely-unclear (clarity score below default). | Up to 3 internal passes.                      | Refill below 2 items.|
| 3     | confident   | Bounces only when the orchestrator would invent a major scope.    | Up to 5 internal passes.                      | Refill below 2 items.|
| 4     | fully-auto  | Never bounces. Every "unclear" verdict becomes "ready" with note. | Up to 5 internal passes; no escape to human.  | Never allowed empty. |

Default is level 2 (`balanced`). The slider tooltip carries each stop's one-line description; the long form is here.

### What "ambiguous" means

A clarity score on each task, `0..1`, computed once per orchestrator-prep iteration:

- 0.00..0.39: ambiguous (bounce candidate at level 1, retain at level 4).
- 0.40..0.69: thin but actionable (bounce candidate at level 1, accept at level 2+).
- 0.70..1.00: clear (accept at every level >= 1).

Clarity inputs (kept in the typed bounce reason): missing acceptance criteria, unstated dependencies, conflicting language, out-of-scope artifacts, lack of "Read first" references, and disagreement with the immediate predecessor or successor task in the queue. The first slice computes the score from the heuristic checks listed in [taxonomy.md](taxonomy.md). A future slice may upgrade to a fast-model verdict (Haiku-class) when token budget allows.

### What "iterate" means

The orchestrator may rewrite the prompt in `1a-orchestrator-prep` up to N times (per autonomy level). Each iteration:

- Records the previous prompt as `prompt-iter-N.md` in the job folder.
- Updates `prompt.md` with the sharpened version.
- Increments `OrchestratorPrepIteration` in `job.json`.
- Appends a typed `[orchestrator-prep]` chat-note describing the change.

After N iterations without convergence, the task either ships to `2-ready` (autonomy 4) or bounces to `1b-needs-human-review` (autonomy 1..3) carrying a typed reason and a suggested rewrite.

## Orchestrator's primary mandate

After this task lands, the orchestrator's first job is **keeping the pipeline moving**. The autonomy scale is the policy knob; the lanes are the surface; the prep loop is the worker. Together:

- **Queue refill.** When `2-ready` drops below the refill floor (default 2), the orchestrator pulls the next eligible task from `1-preparation` into `1a-orchestrator-prep`. At autonomy 0 the floor is ignored.
- **Stuck-task scan.** Anything stuck in any lane longer than the configured threshold gets a supervisor advisory. At autonomy >= 3 the advisory may upgrade to an auto-intervention (under the existing `Supervisor:AutoInterventionEnabled` flag).
- **Typed bounce.** When bouncing to `1b-needs-human-review`, the bounce carries a structured reason plus a suggested rewrite, written to `prompt-suggested.md` next to the original. The card in the kanban shows the reason inline so the human sees what to clarify.
- **Failure repair.** Failed pickups (see `pickup-failures-loud-not-archived`) trigger the orchestrator's reissue path before any human action is required, except at autonomy 0 / 1.

## Where it sits

```
Layer 0  CLI agent loop                    (vendor-owned, opaque)
Layer 1  Runner job-pickup loop            (deterministic; one task per project)
         [1a/1b]: orchestrator-prep loop    NEW; runs above the runner, before the runner picks up
Layer 2  Supervisor                        (advisory; rare emergency primitives)
Layer 2.5 Meta-cycle                       (per-batch pause/inspect/resume)
Layer 3  External system review            (stable-only, hours cadence, off-app)
```

The prep loop is **not** a new layer. It runs inside the orchestrator's existing pickup process, between the user dropping a task in `1-preparation` and the runner pulling from `2-ready`. The supervisor still owns mid-run advisories. The meta-cycle still owns per-batch pause/resume.

## Hard boundaries (read before extending)

- **Sequential-per-project still holds.** ADR-0001 is intact. The prep loop never runs concurrently with the runner on the same project; it only runs at quiet boundaries (between jobs, or when the runner is idle).
- **Token budget guard.** The prep loop reuses the rate limit from `SoftReasoningHostedService` (per-hour calls per project) plus an explicit `Orchestrator:PrepCallsPerHour` cap. Disabled by default.
- **No source edits.** The prep loop edits `prompt.md` inside the job folder and may write a `prompt-suggested.md`. It never edits source code, ADRs, or tests.
- **No backend restart.** `update-stable.sh` is out of scope here.
- **No escape to `2-ready` at level 0.** At autonomy 0 the only path forward is a human click in the kanban.
- **English UI, no em dashes.**

## Files

- [taxonomy.md](taxonomy.md) - the configurable knobs, the clarity-score inputs, the per-level transition matrix, and the typed bounce reasons.
- [ui.html](ui.html) - clickable dummy with two states: low-autonomy board (`1b-needs-human-review` visible, queue thin) and fully-auto board (`1b-needs-human-review` hidden, queue full, items flowing). Toggle via the slider in the header.
- [`docs/architecture-decisions.md`](../../architecture-decisions.md) ADR-0026 - the ADR that adopts this mockup as the spec.

## First implementation slice

Mirrors the deliverables in the parent task prompt:

1. Mockup folder (this folder) settled.
2. ADR-0026 references this folder as the spec.
3. State-machine change: two new lanes added, idempotent boot-time creation, no renames.
4. Backend `OrchestratorPrepHostedService` behind `Orchestrator:PrepEnabled`, off by default. First slice uses the heuristic clarity score; a fast-model upgrade is a follow-up slice.
5. API: `GET/PUT /api/projects/{name}/autonomy`, `POST /api/jobs/{id}/move` accepts the new lane names.
6. Frontend: autonomy slider in the project header (5 stops, tooltip per stop), hide-when-empty for `1b-needs-human-review`, iteration counter and last-decision message rendered on cards in `1a-orchestrator-prep`.
7. Tests: state machine creates the two new folders idempotently; per-level decision rules; API roundtrip on the autonomy endpoint.

## Why mockup-first

Because the autonomy scale is small (5 values) but its blast radius is large (the orchestrator's primary mandate moves into this lane). Locking the surface in a clickable dummy keeps the implementation honest about what the slider promises.
