# Wiki Pulse — the dashboard above the Workstream

**Status:** concept v1, 2026-07-09 — operator-requested. Naming settled:
the Engineering Workstream is renamed **Workstream** (product name; see
[`engineering-workstream.md`](engineering-workstream.md), retitled), and the
wiki no longer opens on a tree or a page but on a generated dashboard:
**Pulse**. Mockup: [`mockups/wiki-pulse-dashboard.html`](mockups/wiki-pulse-dashboard.html).

Supersedes the archived card ASS-1768 (wiki dashboard: recent edits +
quick nav) — its "recent edits" half already exists in the backend
(`GetWikiRecentEdits`, `/api/projects/{p}/wiki/recent`) and becomes one tile
of Pulse.

## 1. Operator intent (anchors, 2026-07-09)

- "Umbenennen in nur **Workstream**. Es sollte das **oberste Element** sein."
- "Darüber wollen wir noch ein **Dashboard** haben … wenn man auf das Wiki
  einsteigt, soll man eine **Historie der letzten Änderungen** sehen:
  **Warnings**, welche Sachen **müssen sortiert werden**, was **gemacht
  wird** — quasi **Drift-Grading**-Zeug."

## 2. Position — a view above the tree, not a page in it

```
Wiki entry ──►  PULSE            <- generated dashboard, the wiki landing
                Workstream       <- top element of the content tree
                ├── Current Development State
                ├── Development Signals
                ├── System Knowledge
                ├── Decision Log
                └── Workstream Log
                (remaining docs/ areas below)
```

**Hard property:** Pulse is a *view*, not a wiki page. It is computed from
git history + frame metadata on request, is never stored in the checkout,
is not editable, and is **not prompt-known** — collector/curator have no
duty toward it and cannot write into it. That keeps the anti-overgrowth
story clean: Pulse renders state, it never adds state.

## 3. The five tiles

### 3.1 Änderungs-Feed (history of last changes)
Recent wiki edits, newest first, grouped by day: page title, frame-area
badge, author, source task key (parsed from the commit subject/trailer),
commit subject. One click → the page, with the revision preselected.
*Source:* `/wiki/recent` exists; extend entries with area + task-key.

### 3.2 Warnings (needs a human now)
- Development Signals with `Human Action` set and status Observed/Active.
- Frame violations: structural pages missing or edited outside the rules.
- Broken internal links between wiki pages.
- Areas over their page budget (Workstream §5 anti-overgrowth) — the
  curator is behind.

### 3.3 Sortierbedarf (inbox)
Pages that landed **outside the frame** (unfiled fragments, orphaned
sub-pages, `docs/` strays) that the curator must merge or file. Count +
direct links. Empty inbox is the healthy state and is shown as such.

### 3.4 In Arbeit (what is being worked on)
Live runs whose working diff touches `docs/**` (task key, lane, runtime),
plus outcome + timestamp of the last collector/curator pass. Answers "wird
gerade am Wiki gearbeitet, und lief die Pflege zuletzt durch?"

### 3.5 Drift-Grading (where does the wiki lie?)
Per frame area (and per System Knowledge page) a freshness grade comparing
**page age against development activity since**: commits under the
project's code roots since the page's last update.

- v1 heuristic, fully deterministic: `0–9` commits since update → **Fresh**,
  `10–49` → **Aging**, `50+` → **Stale**. Area grade = worst page grade +
  counts.
- The point is honest orientation: a Stale badge on System Knowledge says
  "read with care, the code has moved on" — and is the curator's work queue.

**v1.1 (operator, 2026-07-10): LLM-graded page reports on top.** Every page
gets a machine-written assessment report ("Meter-Dokument": grade + short
feedback) stored in its `.meta.json` sidecar. A **global trigger on this
dashboard** runs the grading over all pages with an operator-chosen,
relatively strong model (model picked at trigger time; default from a new
workspace "maintenance model" setting — deliberately NOT the project
pipeline models). Critically graded pages surface in the Warnings/Drift
tiles next to the deterministic heuristic. Implementation: AGT-2051 (run +
reports + tile), AGT-2052 (meta panel expand/collapse UX), AGT-2053
(bidirectional wiki↔task cross-references in JSON metadata, tolerant of
deletions — dangling refs render as "existed once", no integrity jobs).

## 4. Mechanics

1. **Pure deterministic aggregation — no LLM in v1.** Data sources: git log
   (cached), `EngineeringWorkstreamFrame` metadata, signal page frontmatter,
   last curator report. Everything read-only.
2. **Performance is a prerequisite:** Pulse must load **< 1s warm**. It sits
   on the same measurement/caching layer as the wiki-performance work (tree
   title cache, git-log memoization keyed on HEAD) — see the wiki
   performance card; without it, Pulse would multiply today's slow calls.
3. **Degrades gracefully:** any tile whose source is unavailable renders an
   empty state with a reason, never an error page.

## 5. Slices

| Slice | Scope | Gate |
|---|---|---|
| **WS-R rename** | Engineering Workstream → **Workstream** everywhere it is user-visible (nav label, frame root, docs); Workstream pinned as top element of the wiki tree | none (small) |
| **PULSE-1** | dashboard as wiki landing: Feed + Inbox + Drift v1 (deterministic), empty states | WS-R; wiki-perf caching |
| **PULSE-2** | Warnings from signals, "In Arbeit" live view, curator integration | PULSE-1; EW-2 collector |
