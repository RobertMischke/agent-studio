# Tasks Domain Map

Version: 2026-07-23
Status: System-of-record map for task storage, lanes, and API mutation changes.

Use this when a change touches job folders, lane states, task metadata,
workspace registry records, task CRUD, ordering, review evidence, run timeline,
or commit attribution.

## Execution modes

- `coding` is the default source-mutating mode.
- `planning` and `research` are report-only modes. They run without git
  mutation steps and must finish with a clean product checkout.
- `concept` is document-first. It uses an isolated worktree but may change only
  one `docs/operations/<topic>/` Workbench. The published document, not
  `status.md`, is the promotion source.
- A delivered concept waits in `5-human-review` with a
  `concept-sight-review` marker. This is a successful delivery state, including
  when the agent reports `NEEDS_INPUT`; it is not an escalation.
- Sight-review acceptance moves the concept to `6-completed`. The concept
  promotion endpoint creates idempotent coding cards in `1-preparation` and
  relates them to the source concept.

## Entry Points

- [docs/system/contracts/filesystem.md](../contracts/filesystem.md) defines the durable
  job-folder layout, lane catalog, and state strings.
- [docs/system/contracts/agent-task.md](../contracts/agent-task.md) defines what the app
  owns and what the CLI owns inside a task.
- [docs/system/contracts/protocol-style.md](../contracts/protocol-style.md) covers `status.md`, Activity Log
  markers, `attachments/`, `results/`, and image retention.
- [docs/system/architecture/runner-lanes/progress-lane-writers.md](../architecture/runner-lanes/progress-lane-writers.md)
  lists every writer for the `3-progress` lane.
- [docs/app/schemas/task-mutation-request.schema.json](../schemas/task-mutation-request.schema.json)
  and [docs/app/schemas/task-find-result.schema.json](../schemas/task-find-result.schema.json)
  pin task API shapes.

## Project onboarding contract

Project onboarding is one product workflow, not a configurable project-source
catalogue. The onboarding UI has no source-type selector and Workspace Settings
has no Project Sources administration page. A repository URL records the
project's repository location; it does not promise a managed clone or a cloud
workspace workflow.

`POST /api/projects` is the product onboarding mutation. It accepts project
identity plus optional `repositoryPath`, `repositoryUrl`, and
`executionRunner`, creates the central
`<TaskRepository>/projects/PROJ-NNN/tasks/` store, and activates registry-backed
scanning and watching without editing `WatchPaths` or restarting the backend.
The repository URL is stored as the well-known `repo` project URL. New projects
never place task data in the product checkout; legacy in-repository stores stay
in place until an explicit migration.

The same basic values remain editable after creation in Project Settings.
`PUT /api/projects/{PROJ-NNN}` is the canonical update mutation for display
name, short code, workspace, colour, repository checkout, working directory,
repository URL, default CLI/model, and execution runner. The request uses
optional patch fields plus explicit `clear*` flags for optional values. Registry
fields are validated together before they are persisted. The runner value is
delegated to `ProjectSettingsService`; it is not duplicated in the project
registry. The Project Settings UI continues to edit runner assignment through
the dedicated `PUT /api/projects/{projectName}/execution-runner` contract.
Project id, source type, storage location, creation time, and the task-key
counter are immutable and are never accepted as update fields.

The project registry is fail-closed. If an existing
`<TaskRepository>/.metadata/projects.json` is not deserializable, startup
terminates with `ProjectRegistryLoadException` and
`project-registry-load-failed`; no empty replacement state is exposed or
written. Legacy `WatchPaths` seeding is eligible only when the registry file
was absent at its initial load. Every persisted project-count reduction first
copies the current file to `projects.json.quarantine-<UTC timestamp>` and logs
`project-registry-shrink-quarantined`.

## API-First Task Organization

Agents must organize tasks through the application API, never by direct
filesystem mutation under `agent-taskboard-workspace/projects/**` or
`agent-taskboard-workspace/.metadata/**`.

- Required skill for scripted board mutation:
  [.agents/skills/task-api/SKILL.md](../../../.agents/skills/task-api/SKILL.md).
- The skill covers `watchPath`, `X-Client-Id`, lane names, creation, move,
  reorder, archive, and triage templates.
- The route is `/api/tasks` (the former `/api/jobs` alias was removed, ADR-0058).
  How the API identifies a project (raw `watchPath` today, `shortCode`/`projectId`
  target) is documented in
  [../concepts/api-project-identity-and-watchpath.md](../../concepts/api-project-identity-and-watchpath.md).
- If an operation is missing from the API, create a follow-up task instead of
  reaching around the API.
- Accepted integration commits that already exist in the project repository
  can be appended through
  `POST /api/tasks/{id}/commits/integration?watchPath=...` with
  `{ "sha": "<full-40-character-sha>" }`. The commit message must name the
  task key. The operation appends or refreshes that SHA in `commits[]`, mirrors
  it as the final singular `commit`, and never creates or rewrites Git history.
- Integration status accepts persisted abbreviated Git SHAs of at least seven
  hexadecimal characters when they match a reachable full SHA by prefix.
  Zero-file lifecycle entries whose subjects start with
  `wip(runner): salvage before teardown` or `chore: snapshot for review` remain
  visible attribution metadata but are not delivery expectations. A matching
  subject with changed files remains a real integration expectation.

Task creation can carry a structured `routing` request with the observed
surface, affected component, and navigation project. `ComponentRoutingService`
resolves that request against versioned ownership mappings stored on project
registry records. A confident cross-project match replaces the requested
destination, validates the ticket prefix, and appends consumer integration and
deployment acceptance criteria. Unknown, conflicting, or low-confidence
ownership returns `409` with a routing question instead of silently using the
navigation project.

Cross-project `change-project` is also a re-key operation. The state machine
reserves a destination-project key, stages source and destination folders under
hidden names, promotes the complete destination, and rolls back on failure.
This prevents the AGT-2166 archived-orphan/lost-task failure mode.

## Related Concepts

- [../concepts/completion-review-and-remote-runner-stability.html#provenance](../../concepts/completion-review-and-remote-runner-stability.html#provenance):
  why runner assignment is scheduling policy rather than historical fact, how a
  Task retains an ordered multi-runner route, and when a host change continues,
  blocks, or starts a new attempt.
- [../concepts/task-integration-and-merge-workflow.md](../../concepts/task-integration-and-merge-workflow.md):
  how a finished task's branch reaches `develop` (worktree, deferred merge, the
  `5-human-review -> 6-completed` accept trigger).
- [../concepts/task-integration-merge-config-analysis.html](../../concepts/task-integration-merge-config-analysis.html):
  why integration semantics should not depend on `maxParallelism`.
- [../concepts/auto-review-evidence-gate-analysis.html](../../concepts/auto-review-evidence-gate-analysis.html):
  why auto-review reissues good work ("Needs rework") and the evidence-gate fix.

## Files-tab document projection

`GET /api/tasks/{id}/artifacts` projects supported top-level task documents into
the Files tab: Markdown, self-contained `.html` / `.htm`, and structured
`aspect-*.json`. `status.md` remains owned by Result, and subfolders remain out
of scope. A file opens on its current content without historical controls. Its
History tab lists prior snapshots compactly by run, date, and grade; choosing a
row loads that snapshot. Arbitrary From/To comparison between review snapshots
is intentionally absent because it did not support an operator workflow. The
history list remains the basis if a concrete comparison need emerges. HTML
content is fetched through the existing task-file endpoint and
rendered through `srcdoc` with `sandbox="allow-scripts"`. The deliberate omission
of `allow-same-origin` keeps an opaque origin, so interactive artifacts cannot
read Studio cookies, storage, DOM, or APIs. Artifacts that require same-origin
or controlled network integration belong to the Workbench viewer described in
[Experimentier-Workbench](../../concepts/experimentier-workbench.md#5-viewer-interactive-html-and-project-previews).

## Task-tab refinement projection

The task-detail inspector orders its tabs as `Task | Activity | Result`.
`Task` renders the current `prompt.md`, followed by a quiet chronological
refinement history. `GET /api/tasks/{id}/runs` derives that history at read
time from existing evidence:

- `prompt-N.md` supplies full multiline operator refinements created through
  Extend mode.
- The run timeline supplies other operator follow-ups from the `[user]` rows
  in `logs/cli-output.log`; the paired run start is the displayed time and the
  session-event reason supplies the reason when present.
- `orchestrator-follow-up-history/*.md` supplies system reissues, including
  their recorded timestamp, reason or cause, and verbatim steering prompt.

The projection de-duplicates an Extend prompt when the nearby run-log
follow-up contains the same normalized text. It adds no task files and no
write-side contract. Legacy follow-ups that exist only as `[user]` log rows
therefore remain visible without inventing a second persistence mechanism.

## Project proposals

The Project Hub proposal queue is backed by dated Markdown generations under
`docs/concepts/proposals/<YYYY-MM-DD>/`. Structured frontmatter carries the finding,
evidence screenshot, proposed change, estimated effort, severity, durable
decision status (`proposed`, `approved`, `rejected`, or `spawned`), and the
spawned task key. `GET /api/projects/{projectName}/proposals` lists the complete
history. `POST .../{id}/decision` records rejection or creates one coding card
through `TaskMutationService` and then records its key as `spawnedTask`.

New generations use `scripts/generate-project-proposals.mjs`. The generator is
idempotent: an existing proposal document is preserved so a repeated survey run
cannot erase an operator decision.

## Epic lifecycle

- Epics are `kind=epic` task records; membership is the child's `epicId`.
- `GET /api/epics` is archive-inclusive. A finished epic remains queryable when
  all of its children are in `6-completed` or `7-archive`.
- The Epic overview separates active and completed rollups, shows `x / y done`,
  and expands to child lane status plus project identity.
- Manual Epic creation requires a title and goal description. If a planning
  run parses no sub-tasks, the runner records the failed decomposition and
  returns the Epic to `0-backlog` instead of leaving an empty completion.
- A remotely assigned Ready Epic is claimable for planning even though local
  auto-pickup still skips Epic containers. Local and remote completion both use
  `EpicDecompositionLifecycle`, `EpicDecompositionParser`, and
  `EpicSubTaskFactory`. Valid plans create child coding cards with the Epic's
  project, `epicId`, CLI, and model defaults, append
  `.metadata/spawned-tasks.jsonl` planning-spawn evidence, and move the Epic to
  `4-auto-review`. Empty, invalid, or source-mutating plans record a failed
  decomposition and return the Epic to `0-backlog`.
- Empty Epic cleanup uses the Task API to move records to `7-archive`; it never
  deletes the task folder. Archived zero-member cleanup records are omitted
  from the overview, while completed Epics with historical children remain.

## Key Code

- `task-server/TaskServerStore.cs` and `TaskServerEndpoints.cs`: separated
  control-plane task, run, lease, event, artifact, audit, and canonical replay
  store. The path-free
  `GET /api/v1/projects/{projectId}/tasks/{taskIdentity}/history` projection is
  the reconnect source for detached Studio clients.
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
- `backend/Features/TestRuns/TestRunService.cs`: project-wide test-run
  lifecycle plus the read-time card evidence projection derived from the
  latest task-owned commit and Git ancestry.
- `backend/Services/Tasks/ReviewEvidenceLog.cs` and
  `ScreenshotIndexService.cs`: review evidence and visual proof.
- `backend/Features/Registry/WorkspaceSettingsService.cs` and
  `WorkspaceSettingsEndpoints.cs`: per-workspace default orchestrator settings
  (`WorkspaceSettings` beside `WorkspaceRecord`, persisted to
  `.metadata/workspace-settings.json`), exposed via
  `GET /api/workspaces/{id}/settings`, `PUT .../orchestrator-model`, and
  `PUT .../autonomy` (404 on unknown id). This is the workspace tier of the
  two-tier orchestrator config; the precedence and read sites live runner-side
  (ADR-0061, see [runner.md](runner.md)).

## Invariants

- The durable lane sequence is
  `1-preparation -> 2-ready -> 3-progress -> 4-auto-review -> 5-human-review -> 6-completed -> 7-archive`.
- Task creation defaults to `0-backlog` only when `targetState` is absent.
  Every explicit valid `targetState` is authoritative, including review and
  terminal lanes used by operator or automation workflows. Invalid states fail
  creation instead of silently selecting another lane.
- `4-auto-review` remains the disk/API key even when the UI labels it Post
  Processing.
- `5-human-review` is where the user gets the final say. The orchestrator does
  not move a task directly from auto-review to completed.
- Moving a task from `6-completed` to `7-archive` in task detail requires a
  second confirmation while `integration.status` is anything other than
  `integrated`. This is an operator warning, not a server-side hard block.
- Only `2-ready` and `3-progress` tasks can be started. A `2-ready` card is
  additionally held back from auto-pickup while its `references.dependsOn`
  ("waits-on") targets are unfulfilled (AGT-2029); see the waits-on gate in
  [runner.md](./runner.md) and the `references` field in
  [../contracts/filesystem.md](../contracts/filesystem.md).
- Rendered task references resolve through `POST /api/tasks/reference-status`.
  Send `{ "keys": ["AGT-2050", "CAR-2"] }`; keys are trimmed, uppercased,
  deduplicated, and capped at 200. The response is `{ "items": [...] }`, where
  each item contains `key`, `exists`, `taskKey`, `title`, `lane`, project
  identity/colour, merge reachability and branch names, and `reviewGrade`.
  Known-project deleted keys return a ghost (`exists: false`); keys whose
  shortcode is absent from the project registry are omitted. Consumers must
  batch page/message references rather than issue one request per reference.
  The task scan and merge reachability inputs are cached, merge membership is
  computed once for the batch, and the frontend hydrator also caches resolved
  keys for its lifetime. The reusable consumer contract and CAC-3 chat host
  wiring are documented in [frontend/AGENTS.md](../../../frontend/AGENTS.md#task-reference-microcards).
- Successful CLI runs move from `3-progress` to `4-auto-review` through
  application code. Failed or stopped runs remain inspectable.
- Direct filesystem access by app code is restricted to the bounded service
  layer and covered by architecture tests.
- Test evidence is never persisted on a task. A successful run proves a card
  only when its commit equals the card commit or contains its change. Direct
  ancestry proves ordinary commits; a reachable curated `merge(KEY)` or
  `merge-recut(KEY)` integration anchor proves rewritten task commits only when
  that integration postdates the card's current attributed commit.
  Missing commit timestamps disable this fallback rather than reusing historical
  key-only evidence. Planned and running matches are pending evidence; an older
  green run remains visible as `diff not included` and never turns the card green.

## Execution location on task reads

`GET /api/tasks`, `GET /api/tasks/grouped`, task detail, and SignalR task
updates carry `TaskInfo.executionLocation`. Consumers must use its actual
`runnerId`, `executionKind`, lease state, and heartbeat for live attribution.
The project `executionRunner` setting appears only as `configuredRunnerId` for
comparison and queued-remote display. Cards in `3-progress`, the detail header,
and Overview share the same projection, so concurrent cards each retain their
own lease owner rather than inheriting a project-wide runner status.

Settled session events preserve the same projection as historical run evidence.
Historical entries never reuse disconnected warning treatment. See the
[execution location schema](../schemas/task-execution-location.schema.json).

## Outcome issue presentation contract

Task reads derive `TaskInfo.outcomeIssue` from typed runner markers in
`logs/cli-output.log`. `summary` remains a bounded compatibility value for
compact consumers. `technicalDetails` carries the complete normalized source
line and is restricted to explicit technical-details surfaces. User-facing
failure cards map the issue `kind` to a complete human sentence; they never use
`summary` or `technicalDetails` as primary copy. Unknown kinds use a generic
failure sentence while retaining the full diagnostic under the disclosure.

## Verification

- API and mutation changes need endpoint tests plus task-access or service tests
  that prove optimistic concurrency, lane state, and side effects.
- Any new direct filesystem path construction must pass the architecture
  isolation test and justify why it belongs inside the bounded layer.
- Visual-evidence, status, or protocol changes need fixtures that prove old and
  new task folders still render.
