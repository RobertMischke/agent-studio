# Job API Endpoint Reference

Every endpoint listed here is on the backend (`http://127.0.0.1:5031` for
stable, `:5030` for dev). Mutations require the
`X-Client-Id: local-default` header. Source: `backend/Endpoints/Jobs/*.cs`.

## Discovery

### `GET /api/watch-paths`

List of watched projects with the path you must pass as `watchPath` on every
mutation. **Always read this once** at the start of a script; the `path`
field is the canonical resolved path, not the `rootPath`.

Response shape:

```json
[
  { "name": "Agent Task Processor",
    "path": "C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard",
    "rootPath": "C:\\Projects\\agent-taskboard-devspace\\agent-taskboard-dev",
    "repositoryPath": "" }
]
```

### `GET /api/runner/status`

Per-project runner snapshot. Useful before mutating to check whether the
project is busy (avoid disturbing an in-flight run).

```json
{
  "projects": {
    "Agent Task Processor": {
      "mode": "auto-continuous",
      "activeJobId": "fix-bar",
      "activeExecution": { "status": "running", "startedAt": "..." },
      "queuedJobIds": ["..."]
    }
  }
}
```

## Listing jobs

### `GET /api/jobs/grouped`

Returns jobs grouped by lane. Heavy; prefer the per-lane folder scan when you
only need slugs.

### `GET /api/jobs/{id}`

Single job detail. Pass `?watchPath=...` to disambiguate when the slug exists
in multiple projects.

## Mutations (all require X-Client-Id)

### `POST /api/jobs`

Create a job. Body:

```json
{
  "id": "stable-slug",
  "title": "Card title",
  "targetState": "2-ready",
  "order": 0,
  "taskType": "bug",
  "agent": "claude",
  "cliType": "claude",
  "watchPath": "C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard",
  "promptMarkdown": "..."
}
```

Returns `200 {"id":"..."}` or `409 "Job already exists or invalid input"`.

### `PUT /api/jobs/{jobId}/state?watchPath=...`

Move a job to another lane. Body:

```json
{ "targetState": "6-completed" }
```

Returns `200` (empty body). 404 if slug not found at the resolved watchPath.

### `POST /api/jobs/{jobId}/move?watchPath=...`

Same effect as `PUT /state`; exists as an alternative verb for clients that
prefer POST for state changes.

### `POST /api/jobs/{jobId}/move-to-top?watchPath=...`

Promote a job to the head of `2-ready`. No body. Returns
`{ position: 0 }` on success, `404` if not found in a promotable lane.

### `POST /api/jobs/reorder`

Bulk reorder. Body:

```json
{ "jobIds": ["slug-a", "slug-b", "slug-c"] }
```

The given order becomes the new order on the lane the jobs share. All jobs
must be on the same lane.

### `DELETE /api/jobs/{jobId}?watchPath=...`

Delete a job and its folder. Returns `200` on success.

### `POST /api/jobs/{jobId}/restore-from-failed-pickup?watchPath=...`

Lift a folder out of `3a-failed-pickup` back into `2-ready` and drop the
`-pickup-failed-<utc>` suffix in one server-side step. Pass the dead-letter
slug as `{jobId}` (e.g. `foo-pickup-failed-2026-05-08`). Body is optional:

```json
{ "keepDeadLetterSlug": true }
```

retains the suffix; omit the body (or `false`) to recover the original slug.
Idempotent (`200 { "status": "no-op" }` when already restored). Appends a
`pickup-restored` row to `<workspace>/logs/pickup-failures.jsonl`. Use this
instead of a manual `mv` + rename.

### `PUT /api/jobs/{jobId}/title`, `.../task-type`, `.../agent`

Single-field edits. Body: `{ "title": "new" }` etc. Same auth headers.

### `POST /api/jobs/{jobId}/intake`

Trigger the orchestrator-intake gate for a single job (when configured).

## Runner mode

### `PUT /api/runner/{projectName}/mode`

Switch a project's runner mode. Body:

```json
{ "mode": "auto-continuous" }
```

Valid modes: `manual` | `auto-single` | `auto-continuous` | `paused`.

Note: returns `400 "Invalid project or mode"` when the project has no
registered runner. This currently happens when a watch-path is added after
backend start; restart picks it up. See
`fix-runner-mode-rejects-newly-added-projects` if you hit this.

## Bus / observability (read-only)

- `GET /api/bus/{project}/summary` - aggregate counts
- `GET /api/bus/{project}/recent?limit=N` - latest N messages
- `GET /api/bus/{project}/messages?...filters...` - filtered query
- `GET /api/bus/{project}/token-aggregate?since=&until=` - canonical token roll-up

Use these after triggering work to verify it actually landed.
