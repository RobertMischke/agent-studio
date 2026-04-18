# Builder Prompt (Historical)

> **Note:** This was the original scaffold prompt used to bootstrap the app. The actual system has evolved significantly. See `.github/copilot-instructions.md` for current guidelines.

## What was built

Local .NET 10 + Angular 21 Kanban board that watches external project job folders via `WatchPaths` config.

- Backend: ASP.NET Core API on `http://localhost:5030`, SignalR hub
- Frontend: Angular standalone components, signals, PWA on `http://localhost:4010`
- 5-column Kanban: Preparation → Ready → Progress → Review → Completed
- Drag-and-drop state transitions (physically moves job folders)
- Resizable detail side sheet with prompt, status/protocol, and log
- FileSystemWatcher for real-time updates

## Architecture Separation

The app (backend + frontend) lives in this repo. The jobs it watches live in **external projects** under:
```
<Ziel-Projekt>/.orchestrator/jobs/
```

## Job Folder Contract

```
<job-name>/
  job.json    — metadata (id, title, state, priority, agent)
  prompt.md   — task description
  status.md   — processing protocol
  logs/       — optional log files
```

States: `1-preparation` → `2-ready` → `3-progress` → `4-review` → `5-completed`

## Principle
Filesystem first. Convention over integration. Visibility over control.