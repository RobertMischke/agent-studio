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
| [token-aggregation.md](token-aggregation.md) | Token aggregators -> bus-backed shims (ASS-881): one canonical `ITokenAggregator` over the Agent Message Bus, the legacy shims, and the architecture guard test. | [`docs/token-aggregation.md`](../../token-aggregation.md) |
