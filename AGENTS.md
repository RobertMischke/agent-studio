# AGENTS.md

> **Single source of truth for agent instructions.** Read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent. The files [.github/copilot-instructions.md](.github/copilot-instructions.md) and [CLAUDE.md](CLAUDE.md) are 3-line compatibility shims that point here. Frontend-scoped rules live in [frontend/AGENTS.md](frontend/AGENTS.md) and apply only to changes under `frontend/`.

## Project Overview

Agent-Taskboard is a local AI work monitor: a .NET 10 backend plus an Angular 21 frontend that watches external job folders and displays agent progress as a Kanban board.

Keep the product boundary clear:
- This repository contains the taskboard app source code, prompts, and docs.
- Job folders live in watched target projects under `.orchestrator/jobs/`.
- The app observes external jobs; it should not store runtime job artifacts in this repository.

## Architecture

| Layer | Path | Notes |
|-------|------|-------|
| Backend API | `backend/` | ASP.NET Core, runs on `http://localhost:5030`, SignalR hub for live push. |
| Backend tests | `backend.Tests/` | xUnit. |
| Frontend | `frontend/` | Angular 21 standalone components, signals state, PWA, runs on `http://localhost:4010`. |
| E2E tests | `frontend/e2e/` | Playwright. See [frontend/e2e/README.md](frontend/e2e/README.md). |
| Filesystem contract | `docs/filesystem-contract.md` | Job folder layout. |
| Orchestrator prompt | `docs/autopilot-prompt.md` | Canonical workflow copied into watched targets. |
| Repo prompts | `.github/prompts/` | Reusable prompt templates. |
| Backend lifecycle | `api.ps1` | start / stop / restart / status. |

### Service & data layout (backend)

- `Services/Cli/` — one driver per CLI: `ClaudeCliService`, `CodexCliService`, `CopilotCliService`, all extending `CliExecutionServiceBase`. `CliRouter` picks the right one by `cliType`.
- `Services/Cli/SessionRegistry.cs` — discovers sessions on disk and builds the `/api/cli/usage` report.
- `Services/Quota/*QuotaProbe.cs` — per-CLI quota probes. `QuotaService` aggregates and serves `/api/cli/quota` (with background refresh).
- `Services/Pty/` — PTY-based slash-command probes (used for parsing `/usage`, `/status`).
- `Models/` — DTOs: `JobInfo`, `JobDetail`, `CliExecution`, `CreateJobRequest`, `StartJobRequest`, etc.
- `Endpoints/JobEndpoints.cs` — all routes. Read here first when wiring a new feature.

### Key REST endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/jobs` | List all jobs (flat). |
| GET | `/api/jobs/grouped` | Jobs grouped by state. |
| GET | `/api/jobs/{jobId}?watchPath=...` | One `JobDetail` (info + prompt + status + log). |
| POST | `/api/jobs` | Create job (`CreateJobRequest`). |
| POST | `/api/jobs/{jobId}/start?watchPath=...` | Start CLI execution. |
| POST | `/api/jobs/{jobId}/stop?watchPath=...` | Cancel running execution. |
| POST | `/api/jobs/{jobId}/continue?watchPath=...` | Resume with new prompt (same session). |
| GET | `/api/jobs/{jobId}/output?watchPath=...` | CLI stdout/stderr buffer. |
| GET | `/api/cli/usage` | Sessions + versions for all CLIs. |
| GET | `/api/cli/quota` | Per-CLI quota windows (used%, reset times). |
| GET | `/api/cli/{cliType}/models` | Model catalog for one CLI. |
| GET | `/api/watch-paths` | Configured workspaces. |

`jobId` (URL slug) + `watchPath` (project root) is the addressing scheme — `jobKey` is `watchPath::jobId` and is used internally only.

### Watched workspaces

Default dev configuration watches:
- `C:\Projects\agent-taskboard-workspace\projects\agent-taskboard` (this app's own task folders)
- `C:\Projects\agent-taskboard-workspace\projects\runbook`

Use `/api/watch-paths` to enumerate at runtime — never hardcode paths in tests; read them from there.

## Backend Control

Use the `api.ps1` script at the repo root:

```powershell
.\api.ps1 start
.\api.ps1 stop
.\api.ps1 restart
.\api.ps1 status
```

Only restart the backend when backend code changed. Skip backend restarts for pure frontend or docs changes.

## Frontend Control

Start the frontend dev server with the VS Code task `Frontend: Start` when that task runner is available.

Fallback command:

```powershell
npm start --prefix frontend
```

## Build, Test, Verify

| Action | Command |
|--------|---------|
| Backend build | `dotnet build` |
| Backend run | `dotnet run --project backend` |
| Backend tests | `dotnet test` |
| Frontend dev server | `npm start --prefix frontend` |
| Frontend build | `npx ng build --prefix frontend` |
| Frontend unit tests | `npm --prefix frontend run test` |
| **E2E (Playwright)** | `npm --prefix frontend run e2e` |
| E2E interactive UI | `npm --prefix frontend run e2e:ui` |
| E2E single spec | `npm --prefix frontend run e2e -- e2e/cli-usage.spec.ts` |
| Skip billable specs | `SKIP_BILLABLE=1 npm --prefix frontend run e2e` |

Run the relevant build, test, or visual verification checks for the files you change. If a check cannot run in the current environment, document the concrete reason.

### Visual & behavioural changes — Playwright is mandatory

After **every change with visual or behavioural impact** in the frontend (layout, styling, component templates, interaction states, new buttons, new flows), you must:

1. Run the relevant Playwright spec(s) under `frontend/e2e/` and confirm they pass.
2. If the change isn't covered by an existing spec, **add or extend one** before declaring the task done. Regression tests are the deliverable, not an afterthought.
3. For changes that touch CLI execution paths (Claude / Codex / Copilot), run `claude-hello-world.spec.ts` (or the equivalent for the affected CLI) end-to-end. It is `@billable` — uses real quota — but cheap (one Haiku call, ~10s).

Skip Playwright only if the change is provably non-visual and non-behavioural (pure rename, comment edit, dependency bump with no API surface change). Document the reason in the PR description if you skip.

The full E2E setup, conventions, and authoring rules live in [frontend/e2e/README.md](frontend/e2e/README.md).

## Windows Shell Compatibility

- Do not assume `pwsh.exe` is available.
- Prefer commands that work in Windows PowerShell or direct executable calls.
- Avoid shell-specific file creation syntax when a portable tool or existing script is available.

## Job Folder Contract

Each job folder contains:

- `job.json` — metadata (id, title, state, order, agent, cliType, model, sessionName).
- `prompt.md` — task description.
- `status.md` — processing protocol or log.
- `logs/` — optional log files (CLI stdout/stderr lives here as `cli-output.log`).

States:

```text
1-preparation -> 2-ready -> 3-progress -> 4-review -> 5-completed
```

Only jobs in `2-ready` or `3-progress` can be started via `/api/jobs/{id}/start`. New jobs default to `1-preparation`; the create endpoint accepts an optional `targetState` to land directly in `2-ready`.

See `docs/filesystem-contract.md` for full details.

## Code Conventions

- Frontend uses Angular signals for state.
- Frontend components are standalone; do not introduce NgModules.
- Keep the existing dark Catppuccin-inspired UI direction.
- Keep the detail view as a simple protocol view, without tabs or metrics grids unless the product direction changes.
- Prefer small, scoped changes and avoid rewriting unrelated code.

### Selectors in Playwright tests

Prefer `data-testid="..."` for stable test hooks. If a feature you're touching lacks one and a spec needs it, add it to the component rather than reaching for a CSS-class selector.

## Orchestrator Instructions

This repository uses the orchestrator pattern for dependent projects. The shared autopilot workflow is defined in `docs/autopilot-prompt.md`; treat it as the single source of truth.

When onboarding or resyncing a watched target project, use `.github/prompts/sync-target-instructions.prompt.md`. Target projects should receive an `AGENTS.md` with the orchestrator workflow. Add a lightweight `.github/copilot-instructions.md` only as a compatibility shim when that project still needs Copilot Chat repository instructions.
