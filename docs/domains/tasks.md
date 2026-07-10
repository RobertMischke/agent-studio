# Tasks Domain Map

Version: 2026-07-10
Status: System-of-record map for task storage, lanes, and API mutation changes.

Use this when a change touches job folders, lane states, task metadata,
workspace registry records, task CRUD, ordering, review evidence, run timeline,
or commit attribution.

## Entry Points

- [docs/contracts/filesystem.md](../contracts/filesystem.md) defines the durable
  job-folder layout, lane catalog, and state strings.
- [docs/contracts/agent-task.md](../contracts/agent-task.md) defines what the app
  owns and what the CLI owns inside a task.
- [docs/contracts/protocol-style.md](../contracts/protocol-style.md) covers `status.md`, Activity Log
  markers, `attachments/`, `results/`, and image retention.
- [docs/architecture/runner-lanes/progress-lane-writers.md](../architecture/runner-lanes/progress-lane-writers.md)
  lists every writer for the `3-progress` lane.
- [docs/schemas/task-mutation-request.schema.json](../schemas/task-mutation-request.schema.json)
  and [docs/schemas/task-find-result.schema.json](../schemas/task-find-result.schema.json)
  pin task API shapes.

## API-First Task Organization

Agents must organize tasks through the application API, never by direct
filesystem mutation under `agent-taskboard-workspace/projects/**` or
`agent-taskboard-workspace/.metadata/**`.

- Required skill for scripted board mutation:
  [.agents/skills/task-api/SKILL.md](../../.agents/skills/task-api/SKILL.md).
- The skill covers `watchPath`, `X-Client-Id`, lane names, creation, move,
  reorder, archive, and triage templates.
- The route is `/api/tasks` (the former `/api/jobs` alias was removed, ADR-0058).
  How the API identifies a project (raw `watchPath` today, `shortCode`/`projectId`
  target) is documented in
  [../wiki/concepts/api-project-identity-and-watchpath.md](../wiki/concepts/api-project-identity-and-watchpath.md).
- If an operation is missing from the API, create a follow-up task instead of
  reaching around the API.

## Related Concepts

- [../wiki/concepts/task-integration-and-merge-workflow.md](../wiki/concepts/task-integration-and-merge-workflow.md):
  how a finished task's branch reaches `develop` (worktree, deferred merge, the
  `5-human-review -> 6-completed` accept trigger).
- [../wiki/concepts/task-integration-merge-config-analysis.html](../wiki/concepts/task-integration-merge-config-analysis.html):
  why integration semantics should not depend on `maxParallelism`.
- [../wiki/concepts/auto-review-evidence-gate-analysis.html](../wiki/concepts/auto-review-evidence-gate-analysis.html):
  why auto-review reissues good work ("Needs rework") and the evidence-gate fix.

## Epic lifecycle

- Epics are `kind=epic` task records; membership is the child's `epicId`.
- `GET /api/epics` is archive-inclusive. A finished epic remains queryable when
  all of its children are in `6-completed` or `7-archive`.
- The Epic overview separates active and completed rollups, shows `x / y done`,
  and expands to child lane status plus project identity.
- Manual Epic creation requires a title and goal description. If a planning
  run parses no sub-tasks, the runner records the failed decomposition and
  returns the Epic to `0-backlog` instead of leaving an empty completion.
- Empty Epic cleanup uses the Task API to move records to `7-archive`; it never
  deletes the task folder. Archived zero-member cleanup records are omitted
  from the overview, while completed Epics with historical children remain.

## Key Code

- `backend/Endpoints/Tasks/*`: task CRUD, runner, files, git, review evidence,
  merge, pipeline, and query endpoints.
- `backend/Services/TaskAccess/*`: typed read/list/mutate/transition/subscribe
  boundary for task state.
- `backend/Services/Tasks/TaskScannerService.cs`: task scan and projection.
- `backend/Services/Tasks/TaskMutationService.cs`: metadata edits.
- `backend/Services/Tasks/TaskTransitionService.cs`: lane transition owner and
  side effects.
- `backend/Services/Tasks/TaskStateMachine.cs`: durable lane state handling.
- `backend/Services/Tasks/TaskJsonFile.cs`: `job.json` persistence.
- `backend/Services/Tasks/LaneMutexRegistry.cs`: per-project lane serialization.
- `backend/Services/Tasks/CommitAttributionService.cs` and
  `CommitAttributionRunner.cs`: deterministic commit-to-task binding.
- `backend/Services/Tasks/ReviewEvidenceLog.cs` and
  `ScreenshotIndexService.cs`: review evidence and visual proof.

## Invariants

- The durable lane sequence is
  `1-preparation -> 2-ready -> 3-progress -> 4-auto-review -> 5-human-review -> 6-completed -> 7-archive`.
- `4-auto-review` remains the disk/API key even when the UI labels it Post
  Processing.
- `5-human-review` is where the user gets the final say. The orchestrator does
  not move a task directly from auto-review to completed.
- Only `2-ready` and `3-progress` tasks can be started. A `2-ready` card is
  additionally held back from auto-pickup while its `references.dependsOn`
  ("waits-on") targets are unfulfilled (AGT-2029); see the waits-on gate in
  [runner.md](./runner.md) and the `references` field in
  [../contracts/filesystem.md](../contracts/filesystem.md).
- Successful CLI runs move from `3-progress` to `4-auto-review` through
  application code. Failed or stopped runs remain inspectable.
- Direct filesystem access by app code is restricted to the bounded service
  layer and covered by architecture tests.

## Verification

- API and mutation changes need endpoint tests plus task-access or service tests
  that prove optimistic concurrency, lane state, and side effects.
- Any new direct filesystem path construction must pass the architecture
  isolation test and justify why it belongs inside the bounded layer.
- Visual-evidence, status, or protocol changes need fixtures that prove old and
  new task folders still render.
