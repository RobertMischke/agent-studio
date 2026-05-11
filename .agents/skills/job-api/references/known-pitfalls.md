# Known Pitfalls

Recorded from real sessions where past-me lost time to a non-obvious failure
mode. Read this if a script "should work" but does not.

## 1. `409 "Job already exists or invalid input"`

By far the most common failure. Three causes, in order of frequency:

### 1a. `watchPath` mismatch

The `watchPath` in the body **must equal** the `path` field from
`GET /api/watch-paths` for the target project. Common mistakes:

- Passing `rootPath` (the source-tree path) instead of `path`
  (the workspace-resolved job-folder root).
- Hard-coding the path from one environment and running against another
  (dev vs stable have different `TaskRepository` settings).

Fix: read `/api/watch-paths` first.

### 1b. Slug collision

A job with the same `id` already exists in any lane. Slugs are
project-wide unique. Check 7-archive too; archived jobs still occupy the
slug.

Fix: pick a different `id`, or delete the existing job first.

### 1c. Missing required field

`targetState`, `watchPath`, or `id` not provided. The server collapses all
input-validation failures into the same 409 message which makes this hard
to diagnose. Triple-check the body before assuming a real conflict.

## 2. `401 Unauthorized` (or no body returned)

Missing `X-Client-Id: local-default` header on a mutation. The middleware
returns 401 with no body; the calling code may swallow it silently.

This bit two frontend `fetch()` call-sites in production
(`create-job-form.service.ts`, `markdown-rich-editor.ts`) and triggered the
`frontend-fetch-xclientid` drift rule.

Fix: always include the header on mutations.

## 3. Backslash escaping in shell-quoted JSON

Inline curl with a Windows path like `C:\Projects\...` in the body breaks
shell quoting. Two attempts in the 2026-05-11 session both produced unparseable
JSON.

Fix: use the Node template in `../scripts/create-job.js`. Backslashes in JS
string literals are escaped at compile time (`\\`); the resulting JSON has
proper `\\` escapes and the server parses it cleanly.

## 4. `PUT /api/runner/{project}/mode` returns 400 "Invalid project or mode"

The project has no runner registered. Happens when a watch-path is added to
`appsettings.Local.json` after backend boot. The config hot-reloads (the new
project shows up in `/api/watch-paths`), but `TaskRunnerService.ExecuteAsync`
only creates runners once at startup.

Fix: restart the backend (`stop-stable.sh && start-stable.sh`). Tracked as
`fix-runner-mode-rejects-newly-added-projects` for the durable fix that
creates runners on watch-path change.

## 5. Auto-review reissue race

When auto-review decides "reissue" after the runner already moved the job
to `4-auto-review`, there is a window (~1.5 min of aspect-runs) where the
3-progress lane appears empty. The runner can pick up the next ready job,
and then the reissue moves the original job back to `3-progress` → two jobs
in the same lane.

Status: fixed by routing reissue to `2-ready order=0` instead of `3-progress`
(see `fix-auto-review-reissue-must-go-to-ready-not-progress`, 2026-05-11).

If you see two jobs in 3-progress: probably this race; check `cli-output.log`
of each for "Decision: reissue".

## 6. Aspect-runner false-positive `concerns`

Before 2026-05-11, the aspect-runner CLI invocation used `-p <multi-KB prompt>`
as argv on Windows, which silently failed → empty CLI response → all 4
aspects defaulted to "Concerns: Aspect runner produced no parseable verdict".

The 100+ tasks in `5-human-review` lane on 2026-05-11 mostly had this exact
pattern. When triaging, filter out aspects whose summary matches
`/Aspect runner produced no parseable verdict/i`.

Fix landed: `ICliOneShot` service (stdin-piped) replaced the bug class.

## 7. Empty shell folders in lanes

Sometimes a lane folder exists with just a `logs/` subdirectory and no
`job.json` or `prompt.md`. Caused by orchestrator crashes mid-transition or
multi-lane race-conditions.

These cannot be moved via the API (no `job.json` → server cannot find them).
Delete the folder directly:

```js
fs.rmSync(folderPath, { recursive: true, force: true });
```

## 8. Codex-specific (in flight)

These bugs are documented in the queue as `bug-codex-*`:

- Codex token-usage frames do not reach the bus (`turn.completed` is parsed
  but `EmitTokenUsageRichAsync` is not called).
- Codex `thread.started.thread_id` is captured by the adapter but not
  persisted as a session id, so every Continue starts a fresh session.
- The Windows Codex sandbox (`[windows] sandbox = "elevated"` in
  `~/.codex/config.toml`) blocks every shell command with OS error 1312.
  Set to `workspace-write` to bypass.

When working with Codex, expect these until the fixes land.
