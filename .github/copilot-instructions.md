# Project Guidelines

## Architecture

Local AI Work Monitor: .NET 10 backend + Angular 21 frontend watching external job folders.

- `backend/` — ASP.NET Core API on `http://localhost:5030`, SignalR hub for real-time push
- `frontend/` — Angular standalone components, signals-based state, PWA, runs on `http://localhost:4010`
- Jobs live in external projects under `.orchestrator/jobs/`, configured via `WatchPaths` in `appsettings.json`

## Frontend Control

To start the frontend dev server, run the VS Code task **"Frontend: Start"** (via `create_and_run_task`). Do NOT use `npm start` directly in the terminal.

## Backend Control

Use the `api.ps1` script at the repo root for all backend lifecycle operations:

```powershell
.\api.ps1 start     # Start API (skips if already healthy)
.\api.ps1 stop      # Stop the running API
.\api.ps1 restart   # Full restart
.\api.ps1 status    # Check health
```

Only restart the backend (`.\api.ps1 restart`) when backend code was changed. Skip for pure frontend changes.

## Build and Test

- Backend: `dotnet build` / `dotnet run` from `backend/`
- Frontend: `npm start --prefix frontend` (dev server with proxy)
- Frontend build: `npx ng build --prefix frontend`

## Job Folder Contract

Each job folder contains:
- `job.json` — metadata (id, title, state, priority, agent)
- `prompt.md` — task description
- `status.md` — processing protocol / log
- `logs/` — optional log files

States: `1-preparation` → `2-ready` → `3-progress` → `4-review` → `5-completed`

See `docs/filesystem-contract.md` for full details.

## Conventions

- Frontend uses Angular signals (not RxJS subscriptions) for state
- All components are standalone (no NgModules)
- Dark theme UI with Catppuccin-inspired color palette
- Detail view is a simple protocol view (no tabs, no metrics grids)
