# Orchestrator — Autopilot Workflow

This project is monitored by the Agent-Taskboard. Job folders live under `.orchestrator/jobs/` in numbered state folders.

## State Folders

```
.orchestrator/jobs/
  1-preparation/   ← Jobs being defined (human creates these)
  2-ready/         ← Ready for agent pickup
  3-progress/      ← Agent is working on it
  4-review/        ← Done, awaiting human review
  5-completed/     ← Reviewed and accepted
```

## Autopilot Loop

When running in autopilot/continuous mode, follow this loop **strictly one job at a time**:

1. **Scan** `.orchestrator/jobs/2-ready/` for job folders
2. **Pick the FIRST job** (by `order` field in `job.json`, lowest first). Process only ONE job — never start multiple jobs in parallel.
3. **Read** `prompt.md` inside the job folder for the task description
4. **Move** the job folder from `2-ready/` to `3-progress/` (physically move the directory)
5. **Update** `job.json` field `"state"` to `"3-progress"`
6. **Update** `status.md` — add a line like `- Started: <timestamp>`
7. **Execute** the task described in `prompt.md` — work in the project source tree, NOT inside the job folder
8. **Update** `status.md` with progress notes as you work (what was done, what was changed)
9. **When done**, move the job folder from `3-progress/` to `4-review/`
10. **Update** `job.json` field `"state"` to `"4-review"` and `status.md` — add `- Completed: <timestamp>`
11. **Repeat** from step 1 — pick the next job from `2-ready/`. If empty, stop.

### CRITICAL: Sequential Processing

- **ONE job at a time.** Never pick up multiple jobs simultaneously.
- Complete the current job (move to `4-review`) before scanning for the next one.
- This allows the human to re-prioritize, insert urgent tasks, or intervene between jobs.
- The `order` field in `job.json` determines which job is picked first (lowest order = highest priority).

## Job File Contract

Each job folder contains:
- `job.json` — metadata (id, title, state, order, agent). Do NOT modify `id` or `createdAt`.
- `prompt.md` — the task description. **Read-only** — never modify this file.
- `status.md` — processing protocol. **Agent updates this** with concise bullet points.
- `logs/` — optional folder for build outputs or log files.

## Rules

- Only pick jobs from `2-ready/` — never from `1-preparation/`, `4-review/`, or `5-completed/`
- Never modify `prompt.md`
- Always update `status.md` before starting and after completing work
- Work in the project source tree (`src/`, `lib/`, etc.), not inside the job folder
- Keep `status.md` concise — bullet points, not essays
- Update `job.json` field `"state"` to match the current folder
