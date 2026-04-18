# Builder Prompt

Create a local .NET + Angular application that watches workspace job folders and turns agent filesystem activity into a professional task dashboard with approvals, screenshots, metrics and history.

## Architektur-Trennung (KRITISCH!)

Die App (Backend + Frontend) lebt in `/App-Orchestrator/backend/` und `/App-Orchestrator/frontend/`.

Die Jobs, die sie beobachtet, liegen NICHT in der App, sondern im **Ziel-Projekt** unter:
```
<Ziel-Projekt>/.orchestrator/jobs/
```

Der Watch-Path wird per `appsettings.json` konfiguriert:
```json
{
  "WatchPaths": ["C:\\Projects\\MeinProjekt\\.orchestrator\\jobs"]
}
```

## Non-Goals
- Do not control Copilot internals.
- Do not orchestrate agent execution logic.
- Do not depend on private APIs.
- Do NOT store job data inside the App-Orchestrator source tree.

## Required Contract
Watch folder (external, configured):
- `<watch-path>/` contains job folders

Per job folder schema:
- job.json
- prompt.md
- status.md
- artifacts/
- screenshots/
- logs/
- repo/
- review.md
- metrics.json

## Required Features
1. Folder watcher (create/update/delete, timestamps, size growth)
2. Progress estimator (active/idle/likely-finished/blocked)
3. Approval workflow (Draft, Running, Review Needed, Accepted, Rejected, Archived)
4. Artifact viewer (files, logs, markdown, screenshots)
5. Optional verification runner (Playwright if verify script/config exists)
6. Metrics (duration, changed files/lines, rework count, acceptance quality)

## UI
Dashboard columns:
- Active Jobs
- Awaiting Review
- Completed
- Failed/Idle

Job detail tabs:
- Overview
- Files
- Diff
- Screenshots
- Logs
- Metrics
- Review Notes
- Timeline

## Build Sequence
Phase 1: scanner, dashboard, states, job detail
Phase 2: screenshots, diffs, approvals
Phase 3: metrics, summaries, trend analytics

## Principle
Filesystem first. Convention over integration. Visibility over control.