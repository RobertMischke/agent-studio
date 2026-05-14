# Orchestrator Meta-Cycle - Mockup

Design exploration. **A click-dummy plus a taxonomy.** Goal: settle the surface for the per-project pause-inspect-resume loop before any code lands, so the configurable knobs, action vocabulary, and UI are not invented twice.

This folder is the spec the implementation refers back to. It does not document existing behaviour; it documents behaviour the project is about to grow.

## What the meta-cycle is

A loop **above** the per-project runner (Layer 1) and **alongside** the supervisor (Layer 2). Today the supervisor session driving stable runs the same recipe by hand:

1. Set the project's runner to `auto-continuous`.
2. Watch the queue. After N jobs have moved to `4-review`, set the runner to `paused`.
3. Inspect artefacts: new commits, uncommitted snapshots, last-crash marker, supervisor advisories raised in the window, anything anomalous in the log tails.
4. If healthy: snapshot any uncommitted, push, run `update-stable.sh`, set the runner back to `auto-continuous`.
5. If something is off: write the observation, queue a fix-task in `2-ready` (or `1-preparation` if it needs human input), then resume.

The meta-cycle is the orchestrator's automation of those five steps, configurable per project.

## Where it sits

```
Layer 0  CLI agent loop                (vendor-owned, opaque)
Layer 1  Runner job-pickup loop        (deterministic; one task per project)
Layer 2  Supervisor                    (advisory; rare emergency primitives)
Layer 2½ Meta-cycle (this design)      (pause / inspect / resume / queue-fix)
Layer 3  External system review        (stable-only, hours cadence, off-app)
```

The meta-cycle is **not** Layer 2. It does not watch a *running* CLI; it watches the *batch* shape and only acts at quiet boundaries (between jobs, at the `4-review` lane). The supervisor still owns mid-run advice and the four pre-emptive primitives. The meta-cycle reuses the supervisor's existing primitives (`PausePickup`, `Resume`) rather than inventing a third pause mechanism.

The runner remains the single state-machine authority. The meta-cycle never moves a job between lanes, never writes to `job.json`, and never edits source code.

## Files

- [taxonomy.md](taxonomy.md) - configurable knobs, inspection checks, health rules, action vocabulary, override surface.
- [ui.html](ui.html) - clickable dummy showing the meta-cycle control panel. Open in a browser. Catppuccin-ish dark to match the real frontend.
- [`docs/architecture-decisions.md`](../../architecture-decisions.md) ADR-0022 - the ADR that adopts this mockup as the spec.
- [`docs/schemas/meta-cycle-report.schema.json`](../../schemas/meta-cycle-report.schema.json) - JSON shape of one cycle report.

## Hard boundaries (read before extending)

The meta-cycle must respect the same product boundaries as the supervisor:

- **Never edits source code.** It can queue a fix-task; a human or a regular runner picks it up.
- **Never restarts the backend itself.** `update-stable.sh` is invoked as an external sh process and only ever runs against `stable`, never against the dev process that hosts the meta-cycle. The dev backend owns its own lifecycle.
- **Never moves job state lanes.** Lane transitions stay with `JobStateMachine`.
- **Never bypasses the user.** Escalations land in `1-preparation` for human review, never in `2-ready` automatically. A `2-ready` queueing is reserved for fixes the meta-cycle is *certain* are well-scoped (e.g. a templated "rescue orphan changes" task whose contract is fixed).
- **Off by default.** The flag is `Supervisor:MetaCycleEnabled`, default `false` (declared in [`OrchestratorConfigService.KnownOptions`](../../../backend/Services/Configuration/OrchestratorConfigService.cs) and surfaced in the orchestrator config UI panel under **Supervisor → Layer-2.5 meta-cycle**). Flip it via the panel or by adding `"Supervisor": { "MetaCycleEnabled": true }` to `appsettings.json` (or a per-environment override file). Per-project enable lives in `project-settings.json`. "Off by default" is the deliberate end state, not an open item: the loop pauses, pushes, and queues fix-tasks, so it ships dormant and is opted into per project.

If a future request would relax any of those, surface the conflict before implementing.

## Why this is its own loop, not the supervisor's job

The supervisor is shaped around **per-tick, per-run** observation: every 10 s it asks "is the running CLI healthy?". The meta-cycle is shaped around **per-batch** observation: every N completed jobs it asks "does the batch look healthy enough to keep going?". Trying to fold the second job into the first conflates two cadences:

- Supervisor cadence is high-frequency, low-stakes (one advisory log line is cheap).
- Meta-cycle cadence is low-frequency, high-stakes (each tick may pause the whole project, push code, or queue a new task).

ADR-0022 captures the decision to keep them separate so a future contributor does not re-derive a parallel orchestrator inside the supervisor.

## First implementation slice

Mirrors the deliverables in the parent task prompt:

1. Mockup folder (this folder) settled.
2. ADR-0022 references this folder as the spec.
3. JSON schema at `docs/schemas/meta-cycle-report.schema.json` for one cycle's structured report.
4. Backend `MetaCycleHostedService` behind `Supervisor:MetaCycleEnabled`, off by default.
5. Frontend control panel section on the project detail page (status banner, last cycle's findings, override toggles, manual "run inspection now" button).
6. Tests: pure check rules; healthy 3-job batch; fix-triggering batch with a crash marker.

The slice does not include `update-stable.sh` invocation from inside the dev backend. That step is only called from the host context the meta-cycle is enabled in (stable seat, supervisor session). The hosted service shells out via a dedicated sh helper that the user can disable per project; the default action vocabulary stops one step before the helper.

## Resume verification

After every action that ends in resuming the runner (`Resume`, `UpdateStableThenResume`, and the `QueueFix` resume-after-fix) the cycle reads `TaskRunnerService.GetStatus()` back and only declares success when the project's mode has actually flipped to `auto-continuous`. On mismatch it retries the resume with exponential backoff (default 5 attempts, base 1 s, doubling); on persistent failure it raises a high-severity `cycle-resume-failed` advisory and a `[supervisor]` chat-note pinned to the most recent observed job. The control panel surfaces this as the **resume-verification** sub-status on the cycle banner: `verified` (first-try), `verified-after-retries` (recovered drift signal), and `failed` (paused, advisory raised, user must resume).

The same contract applies to any external `sh` helper that resumes the runner over HTTP (notably `scripts/supervisor/resume-runner.sh`, which `restart-stable-after-batch.sh` shells out to after `update-stable.sh`). External callers must additionally wait for `/healthz` to return 200 before the PUT and send `X-Client-Id` on every mutation; the in-process path skips both because it talks to the runner directly through `SetMode`.

## What this mockup is for

When the implementation lands, this folder should answer:

- What knobs the user can turn per project.
- What artefacts each cycle inspects.
- What the four action verbs do.
- What the report shape is.
- What the panel looks like at idle, inspecting, fix-queued, and escalated states.
- What is **explicitly not** in scope.

It is not the final design. It is the spec the first cut implements; future drift updates this folder before it touches code.
