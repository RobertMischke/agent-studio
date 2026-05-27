# Filesystem Contract

## Where Jobs Live

Jobs belong to the watched target project, not to the agent-orchestrator source repository.

```
<target-project>/
  .orchestrator/
    jobs/
      feature-login/
      bugfix-navbar/
      ...
```

Modern targets may use a pointer file instead:

```yaml
# <target-project>/.orchestrator.yml
projectKey: my-project
```

With that pointer, jobs are resolved under the configured `TaskRepository` at `projects/<projectKey>/`. The CLI still runs in the configured `RootPath`; the job folders are task metadata and evidence.

`RootPath` is the implementation working directory for CLI runs and pointer lookup. A watch entry may also set `RepositoryPath` when Git operations should run from a different directory, such as a parent repository that contains the app folder. If `RepositoryPath` is omitted, Git operations fall back to `RootPath` and resolve the Git work-tree top-level from there.

### Project + workspace registry (ADR-0042)

In parallel with the legacy `<projectKey>` slug layout above, projects also live as records in `<TaskRepository>/.metadata/projects.json` with immutable identifiers (`PROJ-001`, `PROJ-002`, …) and a workspace membership in `<TaskRepository>/.metadata/workspaces.json`. At boot, every `WatchPaths` entry without a matching record is auto-registered. The id is monotonic and never re-used; the display name and storage location can change freely without breaking jobKeys derived from the id.

Per-project task counters move out of the sidecar `.task-counter.json` and onto the project record (`NextTaskKeySeq`). Display-keys like `ATP-130` are formatted as `<ShortCode>-<seq>` (e.g. `ATP` for "Agent Software Studio" + sequence `130`).

The full layout migration (jobs sharded under `projects/PROJ-XXX/jobs/<bucket>/<slug>/`, jobKey moved to `PROJ-NNN::<slug>`) is tracked under F45c and is not active yet; jobs continue to live in the lane-folder layout shown below until that ships. New code that needs to address a project should prefer the registry id over the watch-path string; the resolver in [`backend/Services/Jobs/JobKeyResolver.cs`](../backend/Services/Jobs/JobKeyResolver.cs) accepts either form.

## Operational Boundary

The filesystem layout is the storage contract, not the normal operating interface. Agents and scripts must create, move, delete, and reorder jobs through the API:

- `GET /api/watch-paths`
- `POST /api/jobs`
- `POST /api/jobs/{jobId}/move?watchPath=...`
- `POST /api/jobs/reorder`
- `DELETE /api/jobs/{jobId}?watchPath=...`

Direct folder edits are reserved for backend implementation, migrations, recovery, and tests that deliberately exercise this contract. Normal queue management goes through the API so validation, owner identity, SignalR updates, and the Task Access layer remain authoritative.

## Job Folder Layout

Each visible state is a folder, and each job is a subfolder inside one state:

```text
0-backlog/
1-preparation/
2-ready/
3-progress/
4-auto-review/
5-human-review/
6-completed/
7-archive/
```

`0-backlog` is the triage staging area: it is the default landing lane for new jobs created via `POST /api/jobs` without an explicit `targetState`. Auto-pickup never reaches into the backlog; promoting a job to `1-preparation` or `2-ready` is an explicit user action.

The two review lanes are explicit (ADR-0025): `4-auto-review` is the orchestrator's machine pass; `5-human-review` is the lane that actually waits for the user. The pre-ADR-0025 numbered lanes (`4-review`, `5-completed`, `6-archive`) are migrated automatically on backend boot.

Each job folder uses this structure:

```
<job-name>/
  job.json          # Metadata owned by the application
  prompt.md         # Task description for the CLI agent
  status.md         # Generated review protocol
  lifecycle.json    # Optional: richer phase history (intake / post-processing checks)
  attachments/      # Input files supplied with the task
  results/          # Output evidence such as screenshots
                    # Optional: results/review-evidence.jsonl (audit / review findings)
  logs/             # CLI output, including logs/cli-output.log
```

## Template Files

### job.json

```json
{
  "id": "<job-name>",
  "title": "<description>",
  "createdAt": "<ISO-8601>",
  "state": "0-backlog",
  "order": 1,
  "agent": "claude",
  "cliType": "codex",
  "taskType": "chore",
  "tags": ["architecture"]
}
```

**States:** `0-backlog` -> `1-preparation` -> `2-ready` -> `3-progress` -> `4-auto-review` -> `5-human-review` -> `6-completed` -> `7-archive`

**Optional fields:**

- `agent` - auto-pickup eligibility marker. Values matching a CLI backend (`claude`, `codex`, `copilot`, `gemini`) are eligible for auto-pickup; `human` means the job is skipped by the runner and must be started manually. On creation, the API materializes `agent`, `cliType`, and `model` from the owner client's defaults when not explicitly provided, so new jobs carry concrete values rather than the old misleading `agent: "human" / cliType: null / model: null` triple. See `AgentTypes` in `backend/Models/CliTypes.cs`.
- `taskType` - structural classification, one of `bug`, `feature`, or `chore` (default for legacy and technical work). Drives the small chip rendered on the kanban card and the type filter pill in the header. Legacy `user-story` values on disk are silently normalised to `feature` on read; no bulk re-write is performed.
- `tags` - string array of workspace tag ids. The label and colour for each id come from `<workspace>/tags.json` served by `GET /api/tags`. The registry seeds seven default tags on first read (`ui-ux`, `performance`, `quality`, `architecture`, `security`, `docs`, `observability`), each carrying a `description` field that surfaces in tooltips and the filter dropdown. On boot, missing seed ids are merged into an existing registry by id; user-customised rows are never overwritten. Unknown ids on a job (registry entries that were soft-deleted) render as a faint ghost chip on the card.

The application owns transitions between these states. Successful CLI runs move from `3-progress` to `4-auto-review`; the orchestrator's review pass then either reissues (back to `3-progress`), accepts-as-done (forward to `5-human-review`), or escalates (also forward to `5-human-review` with a `[supervisor]` chat-note). The user always confirms the move from `5-human-review` to `6-completed`. Failed or stopped runs stay in `3-progress` for inspection, restart, or continuation.

**Optional substate:** `job.json` may also carry a `"phase"` string that distinguishes orchestrator-driven substates within the same folder-level state. The hybrid V1 model (see `docs/research/expanded-lifecycle-lanes-plan-2026-05.md`) keeps the seven folder-level states above as the durable skeleton and uses `phase` plus the optional sidecar `lifecycle.json` for the Intake / Post Processing lanes the kanban projects on top.

| State          | Allowed phase values                                                                                                                |
|----------------|-------------------------------------------------------------------------------------------------------------------------------------|
| `2-ready`      | `human-ready` (default), `intake-running`, `intake-blocked`                                                                          |
| `3-progress`   | `execution-running`, `execution-stalled`, `post-processing-running`, `post-processing-blocked`, `awaiting-review`                    |
| other states   | none (the state already says enough; the field should be absent or `null`)                                                          |

`phase` is **application-owned and optional**. Existing job folders that predate the field continue to render in the default lane of their state without any rewrite: `2-ready` falls back to `human-ready`, `3-progress` with a running CLI falls back to `execution-running`, `3-progress` with a generating summary falls back to `post-processing-running`, and stopped or failed runs in `3-progress` keep rendering as the execution lane. Unknown or out-of-state values are dropped on read with a warning. Boot-time scans never rewrite a phase-less `job.json` to add a default; lazy defaulting happens in code.

### lifecycle.json (optional)

```json
{
  "version": 1,
  "phase": "intake-running",
  "phaseEnteredAt": "2026-05-06T10:12:00Z",
  "blockingReason": null,
  "intakeChecks": [
    { "name": "duplicate-detection", "status": "passed", "startedAt": "...", "finishedAt": "..." },
    { "name": "clarity-probe",       "status": "running" }
  ],
  "postProcessingChecks": []
}
```

Optional sidecar carrying the richer phase history that does not fit on the wire-level `phase` field: which intake or post-processing checks were scheduled, when the current phase was entered, and the last blocking reason. Absent on legacy job folders; the wire-level `phase` field is the source of truth. The follow-up tasks `ready-orchestrator-intake-lane` and `post-processing-orchestrator-lane` populate this file.

### results/review-evidence.jsonl (optional)

Append-only JSON-Lines file holding **task-level review evidence**: findings produced by security audits, code-review passes, task checks, or notes written by a reviewer. Lives next to the screenshots in `results/` so it travels with the job folder and stays out of the app source repository.

```jsonl
{"id":"e1","source":"security-audit","severity":"high","title":"Token logged in plaintext","body":"`AuthService.LogIn` writes the bearer token to `logs/cli-output.log`.","createdAt":"2026-05-08T12:34:00Z","runIndex":2,"fileRefs":["backend/Services/AuthService.cs:142"],"artifacts":["results/playwright/auth-spec/screenshot.png"]}
{"id":"e2","source":"code-review","severity":"warn","title":"Defensive null check missing","body":"`JobScanner.GetJobDetail` dereferences `info.FolderPath` without a null guard.","createdAt":"2026-05-08T12:34:11Z"}
{"id":"e3","source":"human-note","severity":"info","title":"Visual regression spotted","body":"Compose box border looks 1px off when the steer pill is active.","createdAt":"2026-05-08T12:36:00Z","artifacts":["results/screenshots/compose-steer.png"]}
```

Schema, per line:

| Field            | Type                        | Required | Notes |
|------------------|-----------------------------|----------|-------|
| `id`             | string                      | yes      | Stable identifier; producers may use a uuid or short slug. |
| `source`         | string                      | yes      | One of `security-audit`, `code-review`, `task-check`, `human-note`, `other`. Unknown values fall back to `other` on read. |
| `severity`       | string                      | yes      | One of `info`, `warn`, `high`. Unknown values fall back to `info`. |
| `title`          | string                      | yes      | Single-line headline rendered in the panel. |
| `body`           | string                      | no       | Free-form Markdown (kept short — the panel does not virtualize). |
| `createdAt`      | ISO-8601 string             | yes      | UTC. |
| `runIndex`       | integer                     | no       | The 1-based run index this finding belongs to (matches `runs[].index` from `/api/jobs/{id}/runs`). |
| `artifacts`      | string array                | no       | Paths relative to the job folder, e.g. `results/foo.png` or `results/playwright/spec/file.png`. |
| `fileRefs`       | string array                | no       | Repository-relative file references, optionally `path:line`. Treated as opaque text. |
| `acknowledged`   | boolean                     | no       | True when a reviewer has marked the finding as seen. Latest entry per `id` wins (see "Mutating an existing finding" below). |
| `followupJobId`  | string                      | no       | Set by the "Create follow-up task" action; references the queued task id in the same workspace. |

Hard rules:

- **The endpoint and the UI must never break on a malformed line.** Skip non-parseable JSON and missing required fields with a warning; surface the rest.
- **No state-machine effects.** Findings are review evidence, not blockers. `JobTransitionService` does not consult this file. The user can still move the job through `4-auto-review -> 5-human-review -> 6-completed` while findings are open.
- **Mutating an existing finding** (acknowledging it, attaching a follow-up id) is done by appending a new line with the same `id` and the updated fields. Readers fold the file into latest-per-id; the file stays append-only.
- **Storage location.** Inside the job folder, never inside `agent-taskboard-dev/` itself. Meta-level documentation (decisions, ADRs, doctrine) goes in source; task-level evidence stays beside the job.

### prompt.md

```markdown
# Job Prompt

Describe what the coding agent should build or change.

## Goal
Build feature X.

## Acceptance Criteria
- Criterion 1
- Criterion 2

## Constraints
- Work on the selected task only.
- Put review evidence under the job folder's results/ directory when needed.
```

### status.md

`status.md` is generated by the application from `logs/cli-output.log` after a run. Agents may read it for recovery context, but durable evidence should live in logs or `results/`.

```markdown
# Status

- Result: Success
- Duration: 4 min

## What Was Done
- Implemented the requested change.
```

## Quick Start: Create A New Job

```bash
# Legacy in-target layout:
mkdir -p .orchestrator/jobs/1-preparation/my-new-job/logs

# Then create job.json and prompt.md from the templates above.
```
