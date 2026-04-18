# Filesystem Contract — Job-Ordner-Konvention

## Wo leben Jobs?

Jobs leben **im Ziel-Projekt**, nicht im App-Orchestrator-Repo.

```
<Ziel-Projekt>/
  .orchestrator/
    jobs/
      feature-login/
      bugfix-navbar/
      ...
```

Der Orchestrator beobachtet diesen Pfad per Konfiguration (`WatchPaths` in `appsettings.json`).

## Job-Ordner anlegen

Jeder Job ist ein Ordner unter `.orchestrator/jobs/<job-name>/` mit folgender Struktur:

```
<job-name>/
  job.json          # Metadaten (ID, Titel, State, Order, Agent)
  prompt.md         # Aufgabenbeschreibung für den Agenten
  status.md         # Fortschrittsliste / Verarbeitungsprotokoll
  logs/             # Optionale Log-Dateien (Build-Outputs etc.)
```

---

## Template-Dateien

### job.json

```json
{
  "id": "<job-name>",
  "title": "<Beschreibung>",
  "createdAt": "<ISO-8601>",
  "state": "draft",
  "order": 1,
  "agent": "copilot"
}
```

**States:** `1-preparation` → `2-ready` → `3-progress` → `4-review` → `5-completed`

### prompt.md

```markdown
# Job Prompt

Describe what the coding agent should build inside this job folder.

## Goal
Build feature X.

## Acceptance Criteria
- Criterion 1
- Criterion 2

## Constraints
- Work inside this job folder.
- Keep a concise status log in status.md.
```

### status.md

```markdown
# Status

- State: Preparation
- Last update: <ISO-8601>

## Protocol
- Job folder created.
```

## Schnellstart: Neuen Job anlegen

```bash
# Im Ziel-Projekt:
mkdir -p .orchestrator/jobs/mein-neuer-job/logs
# Dann job.json, prompt.md, status.md anlegen (siehe Templates oben)
```
