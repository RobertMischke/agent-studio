# AGENTS.md

> **Single source of truth for agent instructions.** Read natively by Codex CLI, Claude Code, and the GitHub Copilot coding agent. The files [.github/copilot-instructions.md](.github/copilot-instructions.md) and [CLAUDE.md](CLAUDE.md) are 3-line compatibility shims that point here. Frontend-scoped rules live in [frontend/AGENTS.md](frontend/AGENTS.md) and apply only to changes under `frontend/`.

## Project Overview

Agent-Taskboard is a local AI work monitor: a .NET 10 backend plus an Angular 21 frontend that watches external job folders and displays agent progress as a Kanban board.

Keep the product boundary clear:
- This repository contains the taskboard app source code, prompts, and docs.
- Job folders live in watched target projects under `.orchestrator/jobs/`.
- The app observes external jobs; it should not store runtime job artifacts in this repository.

## Architecture

- `backend/` - ASP.NET Core API on `http://localhost:5030`, with a SignalR hub for real-time push.
- `backend.Tests/` - backend test project.
- `frontend/` - Angular standalone components, signals-based state, PWA, runs on `http://localhost:4010`.
- `docs/filesystem-contract.md` - job folder contract and templates.
- `docs/autopilot-prompt.md` - canonical orchestrator workflow copied into watched target projects.
- `.github/prompts/` - reusable repo prompts.
- `api.ps1` - backend lifecycle script.

## Backend Control

Use the `api.ps1` script at the repo root for backend lifecycle operations:

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

## Build and Test

- Backend build: `dotnet build`
- Backend run: `dotnet run --project backend`
- Frontend dev server: `npm start --prefix frontend`
- Frontend build: `npx ng build --prefix frontend`

Run the relevant build, test, or visual verification checks for the files you change. If a check cannot run in the current environment, document the concrete reason.

## Windows Shell Compatibility

- Do not assume `pwsh.exe` is available.
- Prefer commands that work in Windows PowerShell or direct executable calls.
- Avoid shell-specific file creation syntax when a portable tool or existing script is available.

## Job Folder Contract

Each job folder contains:

- `job.json` - metadata such as id, title, state, order, and agent.
- `prompt.md` - task description.
- `status.md` - processing protocol or log.
- `logs/` - optional log files.

States:

```text
1-preparation -> 2-ready -> 3-progress -> 4-review -> 5-completed
```

See `docs/filesystem-contract.md` for full details.

## Code Conventions

- Frontend uses Angular signals for state.
- Frontend components are standalone; do not introduce NgModules.
- Keep the existing dark Catppuccin-inspired UI direction.
- Keep the detail view as a simple protocol view, without tabs or metrics grids unless the product direction changes.
- Prefer small, scoped changes and avoid rewriting unrelated code.

## Orchestrator Instructions

This repository uses the orchestrator pattern for dependent projects. The shared autopilot workflow is defined in `docs/autopilot-prompt.md`; treat it as the single source of truth.

When onboarding or resyncing a watched target project, use `.github/prompts/sync-target-instructions.prompt.md`. Target projects should receive an `AGENTS.md` with the orchestrator workflow. Add a lightweight `.github/copilot-instructions.md` only as a compatibility shim when that project still needs Copilot Chat repository instructions.
