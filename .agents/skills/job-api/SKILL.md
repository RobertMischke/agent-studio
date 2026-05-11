# Skill: Job-API

Programmatic interaction with the agent-taskboard job API. Read this before
creating or moving tasks via HTTP, especially when the task you are doing
includes "add to the queue", "reissue this job", "drop the task to archive",
"sort the job to the top", or "create a follow-up task".

The board mostly drives state via the UI, but agents (including you, future-me)
need a reliable scripted path because:

- The board cannot create 30 follow-up tasks in a row efficiently.
- Triage of 100+ items belongs in a script, not a click marathon.
- Codex, Claude Code, Copilot, and Gemini all need to be able to do this
  identically; the convention lives here so every CLI picks it up.

## When to invoke

- The user asks for "lege einen Job an" / "create a task" / "queue a follow-up".
- A triage step needs to move many jobs between lanes.
- You want to surface a finding as a new ticket the orchestrator will pick up.
- You need to promote a hot bug to the top of the queue.

## Server contract

Stable backend listens on `http://127.0.0.1:5031`. Dev backend on
`http://127.0.0.1:5030` (when running). The two have separate workspaces; pick
the one whose project list contains the project you are targeting.

Every mutating request **must** carry the `X-Client-Id: local-default` header.
The `ClientIdentityMiddleware` rejects mutations without it as 401. Read
requests do not need the header but it is harmless to include.

## Common pitfall: the watchPath quirk

Every mutation that targets a specific job needs a `watchPath` query parameter.
This is **not** the project's source-tree path; it is the resolved job-folder
root that `GET /api/watch-paths` returns under the `path` field.

For the agent-taskboard project the call is `GET /api/watch-paths`:

```json
[
  {
    "name": "Runbook",
    "path": "C:\\Projects\\agent-taskboard-workspace\\projects\\runbook",
    "rootPath": "C:\\Projects\\Runbook\\App",
    "repositoryPath": "C:\\Projects\\Runbook"
  },
  {
    "name": "Agent Task Processor",
    "path": "C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard",
    "rootPath": "C:\\Projects\\agent-taskboard-devspace\\agent-taskboard-dev"
  }
]
```

Use the `path` field, not `rootPath`. The server resolves jobs against that
path; using `rootPath` returns `409 Job already exists or invalid input` even
when the slug is unique.

For self-contained projects (no `.orchestrator.yml` pointer) the `path` is
`<rootPath>\.orchestrator\jobs`. Always read the live response; never hard-code.

## Hard rules

1. **Always include `X-Client-Id`.** Even on GETs. The Angular interceptor
   adds it for browser requests; the drift rule `frontend-fetch-xclientid`
   exists because three sites forgot it.
2. **Always read `/api/watch-paths` first** if you do not already know the
   exact `path` for your project. Caching it in a script run is fine.
3. **Use Node.js, not curl.** Windows backslashes in JSON bodies break shell
   quoting. The reference scripts in [`scripts/`](scripts) handle this; copy
   them rather than hand-rolling curl.
4. **One job per request.** No bulk-create endpoint. Loop in your script.
5. **Slugs are stable.** Do not include timestamps in `id` unless you actually
   want a fresh per-attempt artifact. Stable slugs keep history linkable.
6. **Lane targets use the full lane name**, e.g. `2-ready` or `6-completed`
   (not `ready` / `completed`). See [`references/states.md`](references/states.md).
7. **Failed-pickup orphans are not regular tasks.** Do not move them with the
   state API if they have no `job.json`. Delete the folder directly instead.

## Process: creating one job

```js
const http = require('http');

const watchPath = 'C:\\Projects\\agent-taskboard-workspace\\projects\\agent-taskboard';

const job = {
  id: 'my-stable-slug',
  title: 'Human-readable title (shown on the card)',
  targetState: '2-ready',     // initial lane
  order: 0,                    // position within lane; 0 = top of new batch
  taskType: 'bug',             // or feature/refactor/analysis/chore
  agent: 'claude',             // or codex/copilot/gemini
  cliType: 'claude',           // usually matches agent
  watchPath,
  promptMarkdown: [
    '## Context',
    '',
    'Markdown lines as a string array, joined with \\n.',
  ].join('\n'),
};

const body = JSON.stringify(job);
const req = http.request({
  hostname: '127.0.0.1', port: 5031, path: '/api/jobs', method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'X-Client-Id': 'local-default',
    'Content-Length': Buffer.byteLength(body),
  },
}, res => {
  let d = '';
  res.on('data', c => d += c);
  res.on('end', () => console.log('status:', res.statusCode, '| body:', d.slice(0, 200)));
});
req.on('error', e => console.log('ERR', e.message));
req.write(body); req.end();
```

A 200 with `{"id":"..."}` means success. A 409 with `"Job already exists or
invalid input"` is the most common failure; nine times out of ten the cause is
the `watchPath` not matching `/api/watch-paths`.

The full template is in [`scripts/create-job.js`](scripts/create-job.js).

## Process: moving a job between lanes

`PUT /api/jobs/{jobId}/state?watchPath=...` with body `{"targetState":"6-completed"}`.

```js
const body = JSON.stringify({ targetState: '6-completed' });
const path = `/api/jobs/${encodeURIComponent(jobId)}/state` +
             `?watchPath=${encodeURIComponent(watchPath)}`;
// PUT, same headers as create
```

Common targets:
- `6-completed` (logically completed; physically lands under `7-archive`)
- `2-ready` (re-queue / reissue)
- `1b-needs-human-review` (parking lot for "I need your call")
- `7-archive` (drop without ceremony)

The full template is in [`scripts/move-state.js`](scripts/move-state.js).

## Process: promoting to the top of `2-ready`

`POST /api/jobs/{jobId}/move-to-top?watchPath=...` with no body.

Use this when a hot bug needs to be picked up before the existing queue, but
the job is already in `2-ready` or `0-backlog`. The state machine handles the
atomic reorder on disk.

Reference: [`scripts/move-to-top.js`](scripts/move-to-top.js).

## Process: bulk triage

Triage rolls do not fit in one mutation. Pattern:

1. Read the folder contents via `fs.readdirSync`, classify each entry
   (status.md + aspect-*.md) into action buckets.
2. For each bucket, loop the mutation script.
3. Idempotent prompt-append: when reissuing, check if the prompt.md already
   contains your "Human Review Note" marker so re-running the script does not
   stack duplicate notes.

The full reusable triage script is in [`scripts/triage-lane.js`](scripts/triage-lane.js).

## Output expectations

When you run any of these scripts, report the count of successes + failures
and any unusual statuses. A silent `200` is fine; a `409` or `404` needs to be
shown to the user with the offending slug so they can dedupe.

For larger triage operations, also produce a one-line summary per bucket
("moved 45 to completed, 12 reissued to ready, 3 unclear").

## Anti-patterns

- **Curl with inline JSON strings.** Backslashes in Windows paths break shell
  quoting. The two times I tried it in the same session both failed; the Node
  template never failed.
- **Hard-coding `rootPath` as `watchPath`.** Returns 409 every time.
- **Forgetting `X-Client-Id`.** The 401 returns no body, and the error message
  is dropped on the floor in some calling code paths.
- **Bypassing the API by editing folders directly.** The in-memory cache stays
  stale until invalidation; only do this for shell folders that the API
  cannot see (e.g. an orphan with no `job.json`).
- **Hand-rolling YAML escapes for prompt content.** Use the `[...].join('\n')`
  pattern so multiline markdown stays readable in source.
- **Creating duplicate slugs for "test" tasks.** They land as 409 conflicts.
  Add a suffix only if you want a fresh attempt artifact, never as a habit.

## Reference

- [`scripts/create-job.js`](scripts/create-job.js) - create one job (template)
- [`scripts/move-state.js`](scripts/move-state.js) - move job to another lane
- [`scripts/move-to-top.js`](scripts/move-to-top.js) - promote to head of `2-ready`
- [`scripts/triage-lane.js`](scripts/triage-lane.js) - bulk-classify + move
- [`references/states.md`](references/states.md) - full lane vocabulary
- [`references/endpoints.md`](references/endpoints.md) - full endpoint surface
- [`references/known-pitfalls.md`](references/known-pitfalls.md) - bugs and
  drift patterns observed in past sessions
