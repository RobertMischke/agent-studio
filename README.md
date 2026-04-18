# App-Orchestrator

**Local AI Work Monitor** — eine eigenständige App (.NET + Angular), die Coding-Agenten bei der Arbeit beobachtet und deren Fortschritt als professionelles Dashboard darstellt.

## Konzept-Trennung (WICHTIG!)

```
┌─────────────────────────────┐     ┌──────────────────────────────────┐
│  App-Orchestrator/          │     │  Ziel-Projekt (z.B. C:\Proj\X)  │
│  ════════════════           │     │  ═══════════════════════════════  │
│  Source-Code der App:       │     │  Hier arbeitet der Agent:        │
│  - backend/ (.NET API)      │     │  - src/, lib/, ...               │
│  - frontend/ (Angular)      │     │  - .orchestrator/                │
│  - docs/ (Konzept, Handoff) │     │    └── jobs/                     │
│  - NEW-I.md, PATHS.md       │     │        ├── feature-login/        │
│                             │────>│        ├── bugfix-navbar/         │
│  Die App LIEST/BEOBACHTET   │     │        └── _template/            │
│  den jobs/-Ordner im Ziel.  │     │                                  │
│  Sie enthält KEINE jobs/.   │     │  Das ist der "Watch Path".       │
└─────────────────────────────┘     └──────────────────────────────────┘
```

### Was lebt WO?

| Ort | Inhalt | Beispiel |
|-----|--------|---------|
| `App-Orchestrator/` | App-Source-Code + Konzeptdocs | Backend, Frontend, NEW-I.md, Handoff |
| `<Ziel-Projekt>/.orchestrator/jobs/` | Agent-Arbeitsartefakte | job.json, prompt.md, status.md, artifacts/, logs/ |

### Warum diese Trennung?

1. **Der Orchestrator ist ein eigenständiges Produkt** — sein Code gehört nicht in die Projekte, die er beobachtet.
2. **Jobs gehören zum Ziel-Projekt** — der Agent arbeitet dort, also liegen seine Artefakte auch dort.
3. **Mehrere Projekte gleichzeitig** — ein Orchestrator kann mehrere Watch-Paths beobachten.
4. **Sauberes Git** — Job-Artefakte verschmutzen nicht den Orchestrator-Source, und umgekehrt.

## Einstieg

1. [NEW-I.md](NEW-I.md) — Initiative & Mission lesen
2. [PATHS.md](PATHS.md) — Pfad-Konventionen verstehen
3. [handoff/builder-prompt.md](handoff/builder-prompt.md) — Scaffold-Prompt für Phase 1
4. [docs/filesystem-contract.md](docs/filesystem-contract.md) — Job-Ordner-Kontrakt (Template)

## Konfiguration

Die App bekommt den Watch-Path als Konfiguration:

```json
// appsettings.json (Backend)
{
  "WatchPaths": [
    "C:\\Projects\\Runbook\\App\\.orchestrator\\jobs"
  ]
}
```

**Wichtig:** Der Orchestrator hat keinen eigenen Jobort. Er zeigt auf Ziel-Apps und beobachtet deren `.orchestrator/jobs/`-Ordner. Ein Orchestrator kann mehrere Apps gleichzeitig beobachten — einfach weitere Pfade in `WatchPaths` hinzufügen.

Perspektivisch: Man startet den Orchestrator und sagt "das ist die App, mit der du arbeitest" — er zeigt dann an, was zu tun ist.
