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
| [testing/](testing/) | Dedicated test workspace and probe contracts; Windows test baseline and platform gates. |
| [doku-inventur-2026-07/](doku-inventur-2026-07/README.md) | Per-document July 2026 inventory, sampled code checks, archive decisions, and the Phase 2 structure sketch. |
| [haertung-verteilte-ausfuehrung/](haertung-verteilte-ausfuehrung/index.html) | Distributed execution hardening, including runner incidents, invariants, and implementation history. |
| [lagebild-2026-08/](lagebild-2026-08/index.html) | Dated snapshot (03.08.2026) of where the system stands: component topology and hosts, CAR execution-layer status, card distribution across lanes, and open themes with card references. |
| [decision-surface/](decision-surface/README.md) | Ownership, artifact, action, and lifecycle contract for operator decisions on escalated tasks. |
| [board-statusmodell-ist-soll/](board-statusmodell-ist-soll/index.html) | German decision Workbench mapping every board lane transition and proposing integration, acceptance, and archive guards (AGT-2424, with AGT-2301 field evidence). |
| [research-deliverables/](research-deliverables/index.html) | Primary HTML report, companion-link, prompt-block, and lightweight-pipeline convention for Research tasks. |
| [remote-hosts.md](remote-hosts.md) | Add, connect, drain, retire, revive, and permanently remove remote runner hosts. |
| [remote-task-server-local-studio.md](remote-task-server-local-studio.md) | Phase A architecture, security, migration, and sub-15-minute rollback plan for a private Hetzner Task Server with Robert's Angular Studio kept local. |
| [releases.md](releases.md) | Tag-driven release assets, component version matrix, guided install, drain-first update, health-gated auto-rollback, and honest CI contract. |
| [url-preview-diagnostics.md](url-preview-diagnostics.md) | URL Preview diagnosis classes, bounded evidence, recovery actions, and Project Settings quick setup. |
| [stable-release-contract.md](stable-release-contract.md) | Immutable Stable tags, build manifests, preflight comparison, rollback identity, and legacy migration. |
| [develop-main-promotion.md](develop-main-promotion.md) | Operator checklist and command for exact-SHA `develop` to `main` promotion, full gate, annotated release marker, and deploy-cron handoff. |
| [installer-story/](installer-story/index.html) | German concept Workbench for the guided all-Docker install on a fresh Linux or Windows machine: component matrix, per-step flow with failure modes, Compose and secrets state, test protocol template, and the cut into a Linux run, a Windows run, and the installer implementation (AGT-2503). |
| [orchestrator-waechter/](orchestrator-waechter/index.html) | Decision dossier for the global Orchestrator Watcher: trigger catalogue, inspect-rescue-analyze-report ladder, Activity visibility, model economy, authority boundaries, operating model, and recommended slices (AGT-2557). |
| [kontext-orchestrator-chats/](kontext-orchestrator-chats/index.html) | Decision dossier for context-aware project and task chats, including current evidence, context ownership, token budgets, UX sketches, and five delivery slices (AGT-2514). |
| [living-document-naming/](living-document-naming/index.html) | Decision dossier evaluating the public name for the artifact that spans exploration, decisions, task-driven delivery tracking, and durable documentation; recommends keeping Dossier, with Living Spec as runner-up (AGT-2584). |

Konvergenz-Probe 2026-07-30: develop ist der Arbeitsbranch.
| [deck-icon-exploration/](deck-icon-exploration/index.html) | Round 2 Deck icon alternatives for the multi-faceted project console, with light and dark proofs, recommendation, rejected Round 1 direction, and implementation seam (AGT-2355). |
