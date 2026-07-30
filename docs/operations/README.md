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
| [doku-inventur-2026-07/](doku-inventur-2026-07/README.md) | Per-document July 2026 inventory, sampled code checks, archive decisions, and the Phase 2 structure sketch. |
| [decision-surface/](decision-surface/README.md) | Ownership, artifact, action, and lifecycle contract for operator decisions on escalated tasks. |
| [research-deliverables/](research-deliverables/index.html) | Primary HTML report, companion-link, prompt-block, and lightweight-pipeline convention for Research tasks. |
| [remote-hosts.md](remote-hosts.md) | Add, connect, drain, retire, revive, and permanently remove remote runner hosts. |
| [remote-task-server-local-studio.md](remote-task-server-local-studio.md) | Phase A architecture, security, migration, and sub-15-minute rollback plan for a private Hetzner Task Server with Robert's Angular Studio kept local. |
| [releases.md](releases.md) | Tag-driven release assets, component version matrix, guided install, drain-first update, health-gated auto-rollback, and honest CI contract. |
| [url-preview-diagnostics.md](url-preview-diagnostics.md) | URL Preview diagnosis classes, bounded evidence, recovery actions, and Project Settings quick setup. |
| [stable-release-contract.md](stable-release-contract.md) | Immutable Stable tags, build manifests, preflight comparison, rollback identity, and legacy migration. |
