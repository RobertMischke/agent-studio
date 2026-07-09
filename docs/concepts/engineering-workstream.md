# Engineering Workstream — orientation in the running development stream

**Status:** concept v2, 2026-07-09 — finalized from the operator's refinement
of [`project-chartroom-concept.md`](project-chartroom-concept.md) (now
superseded; naming settled: **Engineering Workstream**, entry page
**Current Development State**). Related: AGT-1984 (wiki own checkout,
branch-bound), [`multichat-orchestrator.md`](multichat-orchestrator.md)
(collector/curator are orchestrator contexts),
[`post-processing-immediacy-and-parallelism.md`](post-processing-immediacy-and-parallelism.md)
(collector hook is a pipeline step).

## 1. Purpose — deliberately re-weighted

The core is **not** "Dokumentation des Projekts" but **Orientierung im
laufenden Entwicklungsstrom**. No second Kanban, no onboarding wiki, no
project plan, no Project Brief / System Overview / Target State / Roadmap
entry page. It answers: *"Wo steht die Entwicklung gerade, was ist relevant,
was wiederholt sich, und wo braucht es Aufmerksamkeit?"*

> Das Kanban-Board bleibt für Arbeit. Der Engineering Workstream bleibt für
> Orientierung.

## 2. The fixed frame

```
Engineering Workstream
├── Current Development State   <- zentrale Startseite
├── Development Signals
├── System Knowledge
├── Decision Log
└── Workstream Log
```

**Frame rules (hard):**

1. The outer frame is **given and immutable** — frame pages cannot be
   deleted or structurally edited (not by agents, not by accident).
2. **Sub-pages** may be created beneath frame pages; they carry **history**
   and follow the same logic (aggregators create them and link them).
3. Every frame page is **prompt-known**: the collector/aggregator knows the
   page exists and has the *duty* to write into it — filling happens
   automatically through the pipeline, never as a manual setup step.
4. **Anti-overgrowth is a first-class rule** (§5).
5. Pages are **HTML, not Markdown** — the operator wants a strong, punchy
   ("knallig") layout for fast orientation: self-contained HTML in the wiki
   checkout, shared design tokens, generous visual hierarchy, linked
   sub-pages. Markdown only as interim before the HTML renderer lands.

## 3. The five areas (operator-authored templates = the spec)

### 3.1 Current Development State — die verdichtete Lageansicht

Kein vollständiger Statusbericht, keine Projektbeschreibung: ein
**verdichteter Arbeitskontext**. Wird bei jedem Refresh **ersetzt**, nicht
erweitert.

```
# Current Development State
## Current Focus
What currently shapes the development work.
## Active Signals
Recurring problems, patterns, risks, or inconsistencies that currently matter.
## Human Attention
Topics that require review, clarification, or a decision by a developer.
## Open Questions
Questions that are currently unresolved or cannot be answered from existing knowledge.
## Recent Relevant Changes
Changes that affect how the system should currently be understood.
## References
- Related Development Signals / Decisions / System Knowledge pages / Kanban items
```

### 3.2 Development Signals — wiederkehrende Auffälligkeiten

Probleme, Muster, Risiken, Inkonsistenzen, Wissenslücken, technische
Auffälligkeiten, unklare Anforderungen, architektonische Spannungen. Der
Wert: das LLM erkennt aus vielen Aufgaben *"da tritt etwas wiederholt auf,
das ist relevant, das sollte ein Mensch sehen."*

```
# Development Signal: Name
## Type
Problem / Pattern / Risk / Knowledge Gap / Inconsistency
## Summary
Short description of the signal.
## Evidence
Related Kanban items, task summaries, commits, incidents, or notes.
## Impact
What this affects.
## Frequency
Once / Repeated / Frequent / Systemic
## Current Interpretation
What this signal currently seems to indicate.
## Human Action
What should be reviewed, clarified, decided, or monitored.
## Status
Observed / Active / Under Review / Mitigated / Resolved
```

### 3.3 System Knowledge — stabiles Wissen, aktualisiert statt erweitert

Keine Aufgaben, keine Statusmeldungen: Konzepte, Komponenten, Workflows,
Schnittstellen, Datenmodelle, technische/fachliche Regeln, Constraints.
**Kernregel: bestehende Seiten verbessern — keine Wissenssplitter pro Task.**

```
# Concept / Component: Name
## Current Understanding
Consolidated knowledge about this concept or component.
## Responsibilities
What this part is responsible for.
## Relevant Details
Important behavior, rules, constraints, or implementation notes.
## Related Signals
Signals that affect this concept or component.
## Related Decisions
Decisions that explain why it is currently designed this way.
## Last Updated From
References to Kanban items, workstream log entries, or manual updates.
```

### 3.4 Decision Log — Zustand mit Begründung

Ohne Decision Log wird LLM-generierte Doku schnell "aktueller Zustand ohne
Begründung".

```
# Decision: Title
## Status
Proposed / Accepted / Rejected / Superseded
## Context
What made this decision necessary.
## Decision
What was decided.
## Rationale
Why this decision was made.
## Consequences
Expected impact, trade-offs, or constraints.
## Related Signals
## Related Knowledge
## Date
YYYY-MM-DD
```

### 3.5 Workstream Log — verdichteter Verlauf

Kein Backlog, keine Ticketliste, kein klassisches Changelog: was aus
Aufgaben **für den Entwicklungsstrom relevant** wurde.

```
# Workstream Log Entry
## Source
Kanban item, task, pull request, incident, or manual note.
## Summary
What happened.
## Knowledge Updates
Which System Knowledge pages were created, changed, or refined.
## Signals
Which Development Signals were created, confirmed, changed, or resolved.
## Decisions
Which decisions were created, confirmed, or affected.
## Human-Relevant Impact
Why this matters for the ongoing development work.
```

## 4. Mechanics (carried from Chartroom v1, re-anchored)

1. **Collector (pipeline step, automatic):** on task onboarding/completion,
   writes a Workstream Log entry and updates Signals / System Knowledge /
   Decision Log; refreshes Current Development State when enough changed.
   The prompt carries the frame map + area rules (prompt-known pages, duty
   to fill, sub-pages allowed and linked).
2. **Curator (periodic orchestrator context):** verifies signal claims,
   merges duplicates, condenses the State page, prunes stale sub-pages —
   the anti-overgrowth enforcement pass.
3. **Pull:** read API so any orchestrator/multichat context can pull the
   Current Development State or a signal dossier as working context.

## 5. Anti-overgrowth guardrails (hard rules for collector + curator)

- Current Development State is **replaced**, never appended.
- Signals are **merged by identity**: same phenomenon = Frequency hochzählen
  plus neue Evidence — nie duplizieren.
- System Knowledge: **update in place**; neue Seite nur für genuin neues
  Konzept/Komponente; `Last Updated From` ist Pflicht.
- Sub-page depth max. 2; per-area Seitenbudgets (Start: Signals max. 30
  aktiv, Knowledge max. 50); über Budget muss der Kurator erst
  mergen/verdichten, bevor Neues entsteht.
- Frame pages immutable — technisch erzwungen, nicht Konvention.

## 6. Storage & rendering

Lives in the project wiki space — i.e. the wiki's **own checkout**
(AGT-1984), branch-bound (working branch, typically develop). HTML pages,
self-contained, consistent design tokens, both themes; sub-pages linked from
their frame page. History = git history of the checkout.

## 7. Implementation slices

| Slice | Scope | Gate |
|---|---|---|
| **EW-1 frame** | fixed 5-area frame in the wiki area: HTML shell, immutability enforcement, sub-page mechanics with history, navigation | **implemented 2026-07-09** (AGT-1986, merged) |
| **EW-2 collector** | pipeline step on onboarding/completion writing Log/Signals/Knowledge/State per §4.1 with §5 rules; prompt template with frame map | EW-1 |
| **EW-3 retro-pilot + curator** | one-shot retroactive generation for Agent Studio (validates the approach on real history — tonight alone yields signals like "post-processing robustness" and "restart resume orphans"), then the periodic curator | EW-2 |
