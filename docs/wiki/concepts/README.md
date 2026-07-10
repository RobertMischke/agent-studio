# Concept and Knowledge Pages

Hand-maintained, living explainer pages for a domain area. Unlike
[`common-problems/`](../common-problems/) (one incident pattern per folder) and
[`learnings/`](../learnings/) (auto-distilled per-task pages, do not hand-edit),
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
| [docs-structure-migration.md](docs-structure-migration.md) | Record of the real `docs/` folder migration into clearer domains, reports, architecture, frontend, CLI, and Wiki areas while keeping Markdown as default and HTML for visual maps. | [`docs/contracts/wiki-tree.md`](../../contracts/wiki-tree.md) |
| [token-aggregation.md](token-aggregation.md) | Token aggregators -> bus-backed shims (ASS-881): one canonical `ITokenAggregator` over the Agent Message Bus, the legacy shims, and the architecture guard test. | [`docs/domains/tokens.md`](../../domains/tokens.md) |

## Designated topics (AGENTS/wiki-sync)

[`designated-topics/`](designated-topics/README.md) holds the machine-maintained
"Current State / Progress" pages for a set of designated topics, kept fresh by the
opt-in `post-agents-wiki-sync` pipeline step so agents read the current state of a
topic instead of re-discovering it. The operator-owned topic list is
[`designated-topics/registry.json`](designated-topics/registry.json); each entry
pins an AGENTS-surface pointer to one of the concept pages below plus a
`<slug>.md` state page. Unlike the concept pages, the state pages and the
`designated-topics/README.md` index are generated - do not hand-edit them.
