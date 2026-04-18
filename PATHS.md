# App-Orchestrator Paths

## Trennung: App-Code vs. Beobachtete Daten

Die App-Orchestrator-Pfade teilen sich in zwei Bereiche:
1. **App-Source** — der Code der Orchestrator-App selbst (dieses Repo)
2. **Watch-Target** — die Job-Ordner im Ziel-Projekt, die der Agent bearbeitet

---

## 1. App-Source (dieses Repo)
- `/App-Orchestrator/`

### Konzept & Handoff
- `/App-Orchestrator/NEW-I.md`
- `/App-Orchestrator/PATHS.md`
- `/App-Orchestrator/README.md`
- `/App-Orchestrator/handoff/builder-prompt.md`

### App-Code (wird in Phase 1 scaffolded)
- `/App-Orchestrator/backend/` — .NET API
- `/App-Orchestrator/frontend/` — Angular Dashboard

### Docs
- `/App-Orchestrator/docs/filesystem-contract.md` — Job-Ordner-Kontrakt + Template

---

## 2. Watch-Target (im Ziel-Projekt, NICHT hier!)

Pfad wird per Config angegeben. Aktuell:
```
C:\Projects\Runbook\App\.orchestrator\jobs\
```

Der Orchestrator hat keinen eigenen Jobort — er zeigt auf Ziel-Apps.

### Job-Ordner-Struktur (pro Job)
- `<watch-path>/<job-name>/job.json`
- `<watch-path>/<job-name>/prompt.md`
- `<watch-path>/<job-name>/status.md`
- `<watch-path>/<job-name>/review.md`
- `<watch-path>/<job-name>/metrics.json`
- `<watch-path>/<job-name>/artifacts/`
- `<watch-path>/<job-name>/screenshots/`
- `<watch-path>/<job-name>/logs/`
- `<watch-path>/<job-name>/repo/`

### Template (lebt in den Docs der App, wird ins Ziel kopiert)
- `/App-Orchestrator/docs/filesystem-contract.md` — enthält das Template + Anleitung

### Beispiel-Jobs (Runtime, im Ziel-Projekt)
- `C:\Projects\MeinProjekt\.orchestrator\jobs\feature-login\`
- `C:\Projects\MeinProjekt\.orchestrator\jobs\bugfix-navbar\`
- `C:\Projects\MeinProjekt\.orchestrator\jobs\landingpage-v2\`