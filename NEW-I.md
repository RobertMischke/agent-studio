# NEW-I: Local AI Work Monitor
> Initiative ID: NEW-I-2026-04-18-local-ai-work-monitor

## Mission
Build a local .NET + Angular application that watches workspace job folders and turns agent filesystem activity into a Kanban-style task dashboard with drag-and-drop state transitions and a simple processing protocol view.

## Product Type
This is a local control and visibility layer.

It is explicitly not:
- an agent orchestrator
- a scheduler for internal Copilot execution
- a replacement for Copilot or other coding agents

## Architektur-Trennung (KRITISCH!)

```
agent-taskboard/      = Source-Code der App (dieses Repo)
<Ziel-Projekt>/       = Wo der Coding-Agent arbeitet
  .orchestrator/
    jobs/
      1-preparation/  = Jobs in Vorbereitung
      2-ready/        = Bereit zur Bearbeitung
      3-progress/     = In Bearbeitung
      4-review/       = Zur Prüfung
      5-completed/    = Abgeschlossen
```

**Die App enthält KEINE Jobs.** Sie beobachtet Jobs in externen Projekten via konfigurierbarem Watch-Path. Ein Orchestrator kann mehrere Projekte gleichzeitig beobachten.

## Operating Model
- Worker: Copilot (or any coding agent) — arbeitet im Ziel-Projekt
- Contract: Folder and file conventions — leben im Ziel-Projekt unter `.orchestrator/jobs/`
- Control Tower: Agent-Taskboard app — eigenständige App, liest/beobachtet von außen

## Filesystem Contract
Watch path (konfigurierbar):
- `<Ziel-Projekt>/.orchestrator/jobs/`

Each job folder must contain:
- `job.json` — metadata (id, title, state, priority, agent)
- `prompt.md` — task description
- `status.md` — processing protocol / log
- `logs/` — optional log files

## Job States
`1-preparation` → `2-ready` → `3-progress` → `4-review` → `5-completed`

Jobs live inside numbered state folders. Moving a job = moving its folder.

## Core Capabilities
1. Folder watcher using .NET FileSystemWatcher + SignalR real-time push
2. Kanban board with 5 columns and drag-and-drop
3. Simple detail panel with prompt, status/protocol, and log entries
4. PWA support for pinned desktop use

## UI Surfaces
- Dashboard: 5-column Kanban (Preparation, Ready, Progress, Review, Completed)
- Detail panel: Resizable side sheet with prompt, status, and protocol log
- Dark theme (Catppuccin-inspired)

## Tech Stack
- Backend: ASP.NET Core 10 Web API (`http://localhost:5030`)
- Frontend: Angular 21, standalone components, signals-based state (`http://localhost:4010`)
- File Watching: FileSystemWatcher
- Real-time: SignalR

## Critical Rule
Filesystem first.
Convention over integration.
Visibility over control.

## Success Criteria
- A new job folder appears and is auto-discovered on the board
- Jobs can be dragged between columns (state transitions)
- Detail panel shows prompt and processing protocol at a glance
- Dependent projects receive agent instructions for the autopilot workflow
