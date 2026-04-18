# Agent-Taskboard Paths

## Trennung: App-Code vs. Beobachtete Daten

Die Pfade teilen sich in zwei Bereiche:
1. **App-Source** — der Code der Taskboard-App selbst (dieses Repo)
2. **Watch-Target** — die Job-Ordner im Ziel-Projekt, die der Agent bearbeitet

---

## 1. App-Source (dieses Repo)

### Code
- `backend/` — ASP.NET Core 10 API
- `frontend/` — Angular 21 Dashboard (PWA)

### Config & Docs
- `.github/copilot-instructions.md` — Copilot-Anweisungen für dieses Repo
- `.github/prompts/` — Reusable Prompts (z.B. Sync abhängiger Projekte)
- `docs/filesystem-contract.md` — Job-Ordner-Kontrakt + Template
- `NEW-I.md` — Initiative & Mission
- `api.ps1` — Backend start/stop/restart/status Script

---

## 2. Watch-Target (im Ziel-Projekt, NICHT hier!)

Pfad wird per Config angegeben (`WatchPaths` in `appsettings.json`). Aktuell:
```
C:\Projects\Runbook\App\.orchestrator\jobs\
```

### Ordnerstruktur (nummerierte State-Ordner)
```
.orchestrator/jobs/
  1-preparation/        ← Jobs in Vorbereitung
    <job-name>/
      job.json
      prompt.md
      status.md
      logs/             ← optional
  2-ready/              ← Bereit zur Bearbeitung
  3-progress/           ← In Bearbeitung
  4-review/             ← Zur Prüfung
  5-completed/          ← Abgeschlossen
```

### Copilot-Instruktionen im Ziel-Projekt
- `<Ziel-Projekt>/.github/copilot-instructions.md` — enthält den Autopilot-Workflow
- Wird vom Taskboard aus per Prompt synchronisiert (`/sync-target-instructions`)

### Beispiel-Jobs (Runtime, im Ziel-Projekt)
- `C:\Projects\MeinProjekt\.orchestrator\jobs\feature-login\`
- `C:\Projects\MeinProjekt\.orchestrator\jobs\bugfix-navbar\`
- `C:\Projects\MeinProjekt\.orchestrator\jobs\landingpage-v2\`