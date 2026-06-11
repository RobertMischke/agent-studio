# Getting started

Manual install and configuration for agent-orchestrator. Most users do not need to read this — see the README for the recommended path of letting your coding agent install and run it from the AGENTS.md instructions.

## Running

Backend on `http://localhost:5030`, frontend on `http://localhost:4010`. Agents must use the `sh` variant.

```sh
./api.sh start                       # backend
npm start --prefix frontend          # or VS Code task "Frontend: Start"
```

## Dev vs. stable checkout

All code edits happen in the **dev** checkout. The stable checkout exists for reference and gets changes via `git pull` from `main`, never via direct edits. The dev checkout marks itself visually so the two never get confused:

- An orange "DEV" stripe is pinned to the top of the window.
- The PWA install icon and favicon use an orange variant with a "DEV" corner ribbon.
- The window title becomes `agent-orchestrator (DEV)`.

These markers activate when the backend serves `/api/environment` with `{ isDev: true }`, which it does iff a local-only `backend/appsettings.Local.json` file is present:

```json
// backend/appsettings.Local.json, gitignored, dev checkout only
{
  "Environment": {
    "IsDev": true
  }
}
```

The file is gitignored so it stays per-checkout. Stable lacks the file, so the same code produces the un-marked appearance there.

## Configuration

```json
// backend/appsettings.json
{
  "WatchPaths": [
    {
      "Name": "Runbook",
      "RootPath": "C:\\Projects\\Runbook\\App",
      "RepositoryPath": "C:\\Projects\\Runbook"
    },
    { "Name": "My Other Project", "RootPath": "C:\\Projects\\OtherApp" }
  ]
}
```

`RootPath` is the CLI working directory and the place where the board reads `<RootPath>/.orchestrator.yml` for `projectKey`. Jobs then resolve under `agent-taskboard-workspace/projects/<projectKey>/`.

`RepositoryPath` is optional. Use it when the Git repository root differs from the CLI working directory, for example a monorepo or a source app under a parent repository. Git status, diff, commits, and the VS Code handoff use `RepositoryPath`; when it is omitted, they fall back to `RootPath` and still ask Git for the work-tree top-level.

### Keep the system awake during runs

```json
// backend/appsettings.json (or appsettings.Local.json)
{
  "KeepAwakeDuringRuns": true
}
```

`KeepAwakeDuringRuns` (default `true`) makes the backend hold a Windows **power request** for as long as at least one agent run is active, so the host does not drop into idle sleep mid-run. The request is coupled to the live active-run count in one place (the runner loop): it is acquired when the first run starts and released when the count returns to zero. It asserts `PowerRequestSystemRequired` only, so the **display may still sleep** — only the system is kept awake. On Windows the request is named ("Agent Studio: N aktive Agent-Runs") and shows up under:

```sh
powercfg /requests
```

On non-Windows hosts the setting is a harmless no-op. Set it to `false` to opt out entirely.

This is prevention, not a guarantee: a `PowerRequestSystemRequired` request stops the **idle** sleep timer but does **not** override a lid-close or a manual sleep (especially on Modern Standby machines). For those cases the runner is also **sleep-aware**: when the host wakes, the runner detects the suspend (the wall clock jumped forward while a monotonic clock stayed frozen) and **resets the silence clocks** of every active run instead of letting the watchdog mistake the nap for agent silence and kill the run. A wake is logged as `[power] system slept ~X min; reset silence clocks for N run(s)`.

> Operator note (informational, not configured here): on battery (DC) you may additionally want to raise or disable the OS standby timeout. That is a host power-plan setting, separate from `KeepAwakeDuringRuns`.

### Orchestrator + supervisor toggles (UI-configurable)

The hosted-service flags that gate the auto-review orchestrator, the orchestrator-prep lane, the Layer-2 supervisor passes, the Layer-2.5 meta-cycle, and the auto-intervention policy used to require hand-editing `backend/appsettings.Local.json`. These are now reachable from the header `⋮` menu under "Orchestrator config". The drawer reads `GET /api/admin/config/orchestrator`, writes via `PUT` (X-Client-Id required), and the changes land in `backend/appsettings.Local.json` for the running checkout. All flags require a backend restart to take effect; the drawer surfaces a "Restart required" banner after every successful save.

## Job organization through the API

Agents and scripts must organize jobs through the application API, not by directly creating, moving, deleting, or reordering folders under `agent-taskboard-workspace/projects/<projectKey>/`.

Use the API for normal job operations:

- `GET /api/watch-paths` to discover the correct `watchPath`.
- `POST /api/tasks` with `CreateJobRequest` to create a job. Set `targetState` when a job should land directly in `1-preparation` or `2-ready`.
- `POST /api/tasks/{jobId}/move?watchPath=...` to move a job.
- `POST /api/tasks/reorder` to reorder jobs.
- `DELETE /api/tasks/{jobId}?watchPath=...` to delete a job.
- `DELETE /api/tasks/orphan-folder` with body `{"watchPath":"...","lane":"7-archive","folder":"..."}` to delete a scanner-invisible terminal-lane residue folder that has no `job.json`. The route refuses non-terminal lanes and folders that contain `job.json`, and logs `task-orphan-folder-deleted` or `task-orphan-folder-delete-failed`.

On create, `agent` and `cliType` must be valid CLI values (`claude`, `codex`,
`copilot`, or `gemini`) and should match. Do not use `agent: "human"` to keep
a card visible; choose a non-running lane such as `0-backlog` or
`5-human-review`.

Direct filesystem edits are reserved for backend implementation, migrations, recovery work, and tests that deliberately exercise the filesystem contract. They are not the normal operating path for agents. The API is the boundary that keeps ownership, client identity, validation, live updates, and future Task Access behavior in one place.

## Supported CLIs

Claude Code, Codex, GitHub Copilot, Gemini. The contract every CLI must satisfy, including process lifecycle, session model, model selection, quota probing, logging, and cancellation, is in [supported-clis.md](../../cli/supported-clis.md).

## Keeping target projects in sync

When the agent task contract or folder schema changes, run the `/sync-target-instructions` prompt against each target.
