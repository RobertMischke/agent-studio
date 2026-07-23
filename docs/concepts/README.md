# Concept and Knowledge Pages

## Zweck & Abgrenzung

Hand-gepflegte, lebende Erklär- und Wissens-Seiten: das *Warum*, *Was* und *Wie*
eines Bereichs, plus laufendes Wissenslog, dazu datierte Deep-Dives, Mockups und
Proposals.

**Gehört hierher:** Architektur-Konzepte vor der ADR-/Domänen-Reife, lebende
Wissens-Seiten, Mockups (`mockups/`), Proposals (`proposals/`) und die
generierten Designated-Topic-Seiten (`designated-topics/`).

**Gehört nicht hierher:** verbindliche Systemverträge und Domänenkarten (→
`system/`), Betriebs-/Setup-Wissen (→ `operations/`), Qualitäts- und Style-Guides
(→ `quality/`). Code-Verträge (Schemas, Config, In-App-Hilfe) liegen unter `app/`.

> Since 2026-07-18 this folder also holds the architecture concept pages that
> previously sat directly in `docs/concepts/` while the living knowledge pages
> lived in `docs/wiki/concepts/`; the two sets were merged when the `docs/wiki/`
> subfolder was dissolved.

Hand-maintained, living explainer pages for a domain area. Unlike
[`common-problems/`](../operations/common-problems/) (one incident pattern per folder) and
[`learnings/`](../operations/learnings/) (auto-distilled per-task pages, do not hand-edit),
pages here are durable concept write-ups that accumulate knowledge over time.

Each page explains *why* a design exists, *what* the moving parts are, and *how*
to work with the area, in language aimed at both operators and any LLM instance
picking up a task in that domain. Every page ends with a **Living knowledge
log** section: append new findings there (newest on top) rather than letting
hard-won context evaporate into commit messages.

These pages are the conceptual companion to the system-of-record domain docs in
`docs/` (linked from each page). The domain doc owns the plan and the current
contract; the concept page owns the explanation and the running knowledge log.

## Pages

| Page | Area | System-of-record doc |
|---|---|---|
| [completion-review-and-remote-runner-stability.html](completion-review-and-remote-runner-stability.html) | Living umbrella analysis for semantic completion, exact-revision Auto Review, runner/host provenance, controlled cross-host continuation, build/test and visual-evidence gates, retry identity, CLI aborts, and parallel Remote Host stability. | [`docs/system/domains/runner.md`](../system/domains/runner.md), [`docs/system/domains/pipeline.md`](../system/domains/pipeline.md), [`docs/system/contracts/run-outcome.md`](../system/contracts/run-outcome.md) |
| [docs-structure-migration.md](docs-structure-migration.md) | Record of the real `docs/` folder migration into clearer domains, reports, architecture, frontend, CLI, and Wiki areas while keeping Markdown as default and HTML for visual maps. | [`docs/system/contracts/wiki-tree.md`](../system/contracts/wiki-tree.md) |
| [model-escalation-and-companion-routing.md](model-escalation-and-companion-routing.md) | Class-scoped recommendation, economics, pipeline contract, trigger policy, rollout gates, and follow-up slices for stronger-model reissue and companion roles. | [`docs/system/domains/pipeline.md`](../system/domains/pipeline.md), [`docs/system/domains/cli.md`](../system/domains/cli.md), [`docs/system/contracts/run-outcome.md`](../system/contracts/run-outcome.md) |
| [token-aggregation.md](token-aggregation.md) | Token aggregators -> bus-backed shims (ASS-881): one canonical `ITokenAggregator` over the Agent Message Bus, the legacy shims, and the architecture guard test. | [`docs/system/domains/tokens.md`](../system/domains/tokens.md) |
| [tree-project-indicator-alternatives.md](tree-project-indicator-alternatives.md) | Eight alternatives and recommendation for a project-level Explorer state indicator that shows situation instead of a total. | [`docs/quality/design/tree-indicator-exploration-2026-07.html`](../quality/design/tree-indicator-exploration-2026-07.html) |

## Designated topics (AGENTS/wiki-sync)

[`designated-topics/`](designated-topics/README.md) holds the machine-maintained
"Current State / Progress" pages for a set of designated topics, kept fresh by the
opt-in `post-agents-wiki-sync` pipeline step so agents read the current state of a
topic instead of re-discovering it. The operator-owned topic list is
[`designated-topics/registry.json`](designated-topics/registry.json); each entry
pins an AGENTS-surface pointer to one of the concept pages below plus a
`<slug>.md` state page. Unlike the concept pages, the state pages and the
`designated-topics/README.md` index are generated - do not hand-edit them.
