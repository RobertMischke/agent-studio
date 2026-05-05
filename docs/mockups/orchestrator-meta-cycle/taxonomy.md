# Meta-Cycle Taxonomy

The configurable knobs, inspection checks, health rules, action vocabulary, and override surface for the per-project orchestrator meta-cycle. The implementation is bound by this document; if the code drifts, this file is the source of truth and the code is wrong.

## 1. Cycle length

| Knob | Default | Range | Where |
|------|---------|-------|-------|
| `cycleLengthN` | `2` | `1..20` | per-project override; falls back to `Supervisor:MetaCycleDefaultCycleLength` |

Definition: pause the project's runner after **N jobs** have moved into `4-review` since the last cycle's resume. Counted by lane transitions observed at runner status ticks; **not** by `[[TASK_DONE]]` count, because the runner is the single authority for lane membership.

If `N = 1`: every completed job triggers an inspection. Useful when the user is scrutinising agent behaviour after a contract change.

If `N >= 5`: long batches before inspection. Useful when the project is well-trained on the current contract and the user wants to walk away.

## 2. Inspection scope

Each cycle reads a fixed envelope of artefacts. The set is small on purpose so the cycle is bounded, deterministic, and cheap.

### 2.1 Default checks (always on)

| Check | What it reads | Why |
|-------|---------------|-----|
| `commit-log-diff` | `git log <last-cycle-sha>..HEAD --pretty=...` in the working repo | Counts commits, flags zero-commit batches when `4-review` count > 0. |
| `last-crash-marker` | `<workspace>/logs/last-crash.json` (if present) | A backend crash marker since the last cycle is always a fix-trigger. |
| `supervisor-advisories` | `<workspace>/logs/meta/<project>/observations.jsonl`, tail since last cycle, severity ≥ threshold | Aggregates Layer 2 findings into the cycle's verdict. |
| `stuck-in-progress` | Runner status: any job in `3-progress` older than `stuckInProgressMinutes` | Catches a batch that "completed" with one job still wedged. |
| `expected-artefacts` | Per-task: `results/`, `logs/cli-output.log`, summary file | Zero-artefact tasks raise an `expected-artefact-missing` finding. |
| `runner-mode-drift` | Persisted vs current `RunnerMode` | The meta-cycle is the only writer that should change mode while a cycle is active; any mid-cycle mode change from another source is a finding. |

### 2.2 Extension hooks (per-project, optional)

| Hook | Shape | Notes |
|------|-------|-------|
| `extraGlobs` | `string[]` of glob patterns | Files matching the glob are surfaced in the report under `inspection.extras`. The presence of a match is informational; whether it triggers a fix depends on `extraGlobAction`. |
| `extraAdvisoryTopics` | `string[]` of supervisor advisory topics | When any topic in the list appears in the window, raise it from informational to fix-trigger regardless of severity. |
| `extraGlobAction` | `inform` (default) / `fix-trigger` | Controls whether a glob match alone queues a fix. |

Hooks are read-only; they cannot inject new actions, only escalate existing ones.

## 3. Health rules

A cycle is **healthy** when every condition holds:

- Zero advisories at severity `High` since last cycle from sources `HardCheck` or `SoftReasoning`.
- Zero crash markers since last cycle.
- For the N completed jobs: every job has at least one commit attributable to its run window, **OR** the project's `auto-commit` setting is off (in which case zero is acceptable).
- No job stuck in `3-progress` past `stuckInProgressMinutes`.
- No mid-cycle runner-mode drift.

A cycle is **fix-triggering** when:

- Any condition above fails, **OR**
- Any extension hook escalates a finding to fix-trigger.

A cycle is **escalation-only** (not auto-fixable) when:

- The fix-trigger touches contracts the meta-cycle cannot template safely: ADR drift, prompt-template regressions, supervisor logic changes, anything tagged `needs-human` in the advisory.
- In this case the action is `escalate-to-user`; the meta-cycle queues a `1-preparation` task with the cycle report attached, never `2-ready`.

## 4. Action vocabulary

Four actions, each with a typed reason. Recorded in `interventions.jsonl` (reusing the existing supervisor channel with `Source = AutoIntervention` and a `MetaCycle` discriminator on the topic prefix) and in the cycle's report.

| Action | Effect | When |
|--------|--------|------|
| `resume` | `SetMode("auto-continuous")` via `SupervisorInterventionService.ResumeAsync` | Cycle is healthy. |
| `update-stable-then-resume` | Invoke `update-stable.sh` (sh helper, external process), wait for exit, then `resume`. Disabled per project by default; enabled only on the project that is `agent-taskboard` itself. | Cycle is healthy and `runUpdateStableOnHealthy` is on. |
| `queue-fix` | Materialise a templated job folder under `<workspace>/projects/<project>/1-preparation/auto-fix-<topic>-<timestamp>/`, then `resume`. The job lands in `1-preparation` so a human reviews before it is moved to `2-ready`. **Default placement is always `1-preparation`.** A future per-project flag may opt specific templated topics into `2-ready`; first cut keeps the human gate. | Cycle is fix-triggering and the topic has a known template. |
| `escalate-to-user` | Materialise a `1-preparation` task with the report and a "review needed" prompt, **do not resume**. The runner stays paused until the user resumes. | Cycle is escalation-only, or `queue-fix` template missing. |

The action **never** is "edit source and continue". Source edits are always a separate task picked up by a regular runner.

### 4.1 Action ordering

Each cycle picks exactly one action. Tie-breaking order: `escalate-to-user` > `queue-fix` > `update-stable-then-resume` > `resume`.

## 5. User-controlled overrides

Per-project overrides live in `project-settings.json` under a new `MetaCycle` block. Schema (loose):

```json
{
  "MetaCycle": {
    "Enabled": false,
    "CycleLengthN": 2,
    "StuckInProgressMinutes": 30,
    "AdvisorySeverityThreshold": "Warn",
    "RunUpdateStableOnHealthy": false,
    "ExtraGlobs": [],
    "ExtraAdvisoryTopics": [],
    "ExtraGlobAction": "inform",
    "MaxFixesPerHour": 2
  }
}
```

`MaxFixesPerHour` is a circuit breaker. If the meta-cycle has queued the rate-limit's worth of fixes in the trailing hour, the next fix-trigger is converted to `escalate-to-user` with reason `auto-fix-rate-limit`, and the user is forced to review what is going on before more templated work lands.

Global defaults live under `appsettings.json` `Supervisor:MetaCycle*`:

```
Supervisor:MetaCycleEnabled              false   // master switch; off in shipped config
Supervisor:MetaCycleDefaultCycleLength   2
Supervisor:MetaCycleDefaultStuckMinutes  30
Supervisor:MetaCycleDefaultSeverity      Warn
Supervisor:MetaCycleMaxFixesPerHour      2
```

## 6. Report shape

One cycle = one `MetaCycleReport` written to `<workspace>/logs/meta/<project>/meta-cycle/<timestamp>.json` and mirrored to a tail in `meta-cycle.log`. Schema is normative: see [`docs/schemas/meta-cycle-report.schema.json`](../../schemas/meta-cycle-report.schema.json).

Report structure (prose; the JSON schema is the validator):

- `cycleId`: ULID, lexically sortable.
- `project`, `startedAt`, `completedAt`.
- `cycleLengthN`, `jobsObserved`: ids and titles of the jobs that closed since last cycle.
- `inspection`: each default check's result + extras.
- `findings`: typed list of findings (topic, severity, evidence pointer).
- `verdict`: `healthy` | `fix-triggering` | `escalation-only`.
- `action`: the chosen action plus its typed reason.
- `followUpJobId`: when `queue-fix` or `escalate-to-user` ran, the new `1-preparation` (or `2-ready`) job's id.

Reports are append-only. A cycle that aborts mid-flight (backend restart, observation read failure) writes a `verdict: aborted` report with the partial findings so the timeline never has a silent gap.

## 7. Activity log surface

Each cycle emits two lines to the existing `[supervisor]` participant in the per-job orchestrator chat log when the cycle touches that job:

- A start line: `meta-cycle: pause after N=2; inspecting <jobs>`.
- A close line: `meta-cycle: <verdict>; action=<verb>; report=<cycleId>`.

These reuse the already-rendered `[supervisor]` participant rather than adding a sixth participant kind. Adding a new participant would force a frontend release for a feature that already fits inside an existing surface.

## 8. What is intentionally out of scope

- **No mid-run intervention.** The meta-cycle never cancels a running CLI. That is `SupervisorInterventionService.CancelRunAsync`'s job; the meta-cycle never invokes it.
- **No multi-project rollups.** Each project runs its own meta-cycle independently. Cross-project review is Layer 3.
- **No source code edits.** Templated fix-tasks describe what should be fixed; a regular CLI run does the editing.
- **No bypass of `JobStateMachine`.** Lane transitions for the queued fix-task happen the normal way (create at `1-preparation`, user moves to `2-ready`).
- **No silent behaviour changes.** Every action is logged with reason and a report id. The user can reconstruct what the meta-cycle did and why from the report file alone.
- **No second pause mechanism.** Pause and resume route through `SupervisorInterventionService.PausePickup/Resume` so there is exactly one pause implementation.
