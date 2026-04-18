# NEW-I: Local AI Work Monitor
> Initiative ID: NEW-I-2026-04-18-local-ai-work-monitor

## Mission
Build a local .NET + Angular application that watches workspace job folders and turns agent filesystem activity into a professional task dashboard with approvals, screenshots, metrics and history.

## Product Type
This is a local control and visibility layer.

It is explicitly not:
- an agent orchestrator
- a scheduler for internal Copilot execution
- a replacement for Copilot or other coding agents

## Architektur-Trennung (KRITISCH!)

```
App-Orchestrator/     = Source-Code der App (dieses Repo)
<Ziel-Projekt>/       = Wo der Coding-Agent arbeitet
  .orchestrator/
    jobs/             = Vom Orchestrator beobachteter Ordner
```

**Die App enthält KEINE Jobs.** Sie beobachtet Jobs in externen Projekten via konfigurierbarem Watch-Path. Ein Orchestrator kann mehrere Projekte gleichzeitig beobachten.

## Operating Model
- Worker: Copilot (or any coding agent) — arbeitet im Ziel-Projekt
- Contract: Folder and file conventions — leben im Ziel-Projekt unter `.orchestrator/jobs/`
- Control Tower: App-Orchestrator app — eigenständige App, liest/beobachtet von außen

## Filesystem Contract
Watch path (konfigurierbar):
- `<Ziel-Projekt>/.orchestrator/jobs/`

Each job folder must contain:
- job.json
- prompt.md
- status.md
- artifacts/
- screenshots/
- logs/
- repo/
- review.md
- metrics.json

## Job States
Draft -> Running -> Review Needed -> Accepted | Rejected -> Archived

## Core Capabilities
1. Folder watcher using .NET FileSystemWatcher
2. Progress estimation from activity signals
3. Human approval workflow (Accept / Reject / Needs Rework)
4. Artifact viewer (files, notes, screenshots, logs)
5. Optional screenshot verification via Playwright
6. Metrics and timeline analytics

## UI Surfaces
- Dashboard columns: Active, Awaiting Review, Completed, Failed/Idle
- Job detail tabs: Overview, Files, Diff, Screenshots, Logs, Metrics, Review Notes, Timeline

## MVP Build Order
Phase 1:
- folder scanner
- dashboard
- status states
- job detail page

Phase 2:
- screenshot verification
- diffs
- approval flow

Phase 3:
- metrics
- AI summaries
- trend analytics

## Tech Stack
- Backend: ASP.NET Core Web API
- Frontend: Angular
- Storage: SQLite (PostgreSQL optional later)
- File Watching: FileSystemWatcher
- Diff Engine: git CLI or LibGit2Sharp
- Screenshot Verification: Playwright

## Critical Rule
Filesystem first.
Convention over integration.
Visibility over control.

## Success Criteria
- A new job folder appears and is auto-discovered
- File activity transitions a job into Running
- User can review evidence and mark Accepted/Rejected
- Timeline and metrics are stored and visible

## Next Step
Use the builder prompt in /App-Orchestrator/handoff/builder-prompt.md to scaffold the app.