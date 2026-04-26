# Agent-Taskboard

**Local AI Work Monitor** — eine eigenständige App (.NET + Angular), die Coding-Agenten bei der Arbeit beobachtet und deren Fortschritt als Kanban-Board darstellt.

## Konzept-Trennung (WICHTIG!)

```
┌─────────────────────────────┐     ┌──────────────────────────────────┐
│  agent-taskboard/           │     │  Ziel-Projekt (z.B. C:\Proj\X)  │
│  ════════════════           │     │  ═══════════════════════════════  │
│  Source-Code der App:       │     │  Hier arbeitet der Agent:        │
│  - backend/ (.NET API)      │     │  - src/, lib/, ...               │
│  - frontend/ (Angular PWA)  │     │  - .orchestrator/                │
│  - docs/                    │     │    └── jobs/                     │
│  - .github/prompts/         │     │        ├── 1-preparation/        │
│                             │────>│        ├── 2-ready/              │
│  Die App LIEST/BEOBACHTET   │     │        ├── 3-progress/           │
│  den jobs/-Ordner im Ziel.  │     │        ├── 4-review/             │
│  Sie enthält KEINE jobs/.   │     │        └── 5-completed/          │
└─────────────────────────────┘     └──────────────────────────────────┘
```

### Was lebt WO?

| Ort | Inhalt |
|-----|--------|
| `agent-taskboard/` | App-Source-Code, Prompts, Docs |
| `<Ziel-Projekt>/.orchestrator/jobs/` | Job-Ordner mit `job.json`, `prompt.md`, `status.md` |

### Warum diese Trennung?

1. **Der Taskboard ist ein eigenständiges Produkt** — sein Code gehört nicht in die Projekte, die er beobachtet.
2. **Jobs gehören zum Ziel-Projekt** — der Agent arbeitet dort, also liegen seine Artefakte auch dort.
3. **Mehrere Projekte gleichzeitig** — ein Taskboard kann mehrere Watch-Paths beobachten.
4. **Sauberes Git** — Job-Artefakte verschmutzen nicht den Taskboard-Source, und umgekehrt.

## Starten

```powershell
# Backend
.\api.ps1 start

# Frontend (VS Code Task)
# Oder: npm start --prefix frontend
```

## Konfiguration

```json
// backend/appsettings.json
{
  "WatchPaths": [
    "C:\\Projects\\Runbook\\App\\.orchestrator\\jobs"
  ]
}
```

## Abhängige Systeme synchronisieren

Wenn sich der Workflow oder das Ordner-Schema ändert, müssen die Agent-Instruktionen in den Ziel-Projekten aktualisiert werden. Dafür gibt es den Prompt `/sync-target-instructions` (in `.github/prompts/`).

## Docs

- [NEW-I.md](NEW-I.md) — Initiative & Mission
- [PATHS.md](PATHS.md) — Pfad-Konventionen
- [docs/filesystem-contract.md](docs/filesystem-contract.md) — Job-Ordner-Kontrakt
