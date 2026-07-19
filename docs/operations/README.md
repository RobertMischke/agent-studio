# Operations

Operator-facing setup, runtime, security, and git workflow docs.

## Zweck & Abgrenzung

Betriebswissen für Operatoren: wie das System aufgesetzt, betrieben, abgesichert
und per Git bewegt wird.

**Gehört hierher:** Setup/Onboarding, Runtime- und Observability-Verträge,
Sicherheitsarchiv, Git-Doktrin, Test-Workspaces, Remote-Host-Lebenszyklus sowie
die generierten Betriebs-Seiten (`common-problems/`, `learnings/`).

**Gehört nicht hierher:** erklärende Konzepte und Designentscheidungen (→
`concepts/`), Systemverträge und Domänenkarten (→ `system/`), Qualitäts- und
Style-Guides (→ `quality/`). Code-Verträge (Schemas, Config, In-App-Hilfe) liegen
unter `app/` und werden nur zusammen mit Code geändert.

| Area | Contents |
|---|---|
| [setup/](setup/README.md) | Project onboarding, CLI onboarding, first task, troubleshooting, and worktree stack. |
| [security/](security/overview.md) | Security overview, requirements, state, and reviews. |
| [runtime/](runtime/) | Product runtime observability and log-capture contracts. |
| [git/](git/) | Commit, push, and attribution doctrine. |
| [testing/](testing/) | Dedicated test workspace and probe contracts. |
| [remote-hosts.md](remote-hosts.md) | Add, connect, drain, retire, revive, and permanently remove remote runner hosts. |
