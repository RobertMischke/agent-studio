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
  job.json          # Metadaten (ID, Titel, State, Priorität, Agent)
  prompt.md         # Aufgabenbeschreibung für den Agenten
  status.md         # Fortschrittsliste, vom Agenten aktualisiert
  review.md         # Review-Entscheidung (Accept/Reject/Rework)
  metrics.json      # Mess-Kennzahlen (Dauer, Änderungen, Qualität)
  artifacts/        # Generierte Dateien, Outputs
  screenshots/      # Screenshot-Evidenz (optional, Playwright)
  logs/             # Agent-Logs, Build-Outputs
  repo/             # Optionaler Git-Klon oder Worktree
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
  "priority": "high",
  "agent": "copilot"
}
```

**States:** `draft` → `running` → `review-needed` → `accepted` | `rejected` → `archived`

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

- State: Draft
- Last update: <ISO-8601>
- Current step: Initialized
- Notes:
  - Job folder created from template.
```

### review.md

```markdown
# Review

## Decision
- Pending

## Notes
- Add acceptance or rework notes here.

## Approver
- Name:
- Date:
```

### metrics.json

```json
{
  "durationMinutes": 0,
  "filesChanged": 0,
  "linesAdded": 0,
  "linesRemoved": 0,
  "screenshotsProduced": 0,
  "acceptedFirstTry": false,
  "reworkCount": 0,
  "buildSuccess": null,
  "testSuccess": null
}
```

## Schnellstart: Neuen Job anlegen

```bash
# Im Ziel-Projekt:
mkdir -p .orchestrator/jobs/mein-neuer-job/{artifacts,screenshots,logs,repo}
# Dann job.json, prompt.md, status.md, review.md, metrics.json anlegen (siehe Templates oben)
```
