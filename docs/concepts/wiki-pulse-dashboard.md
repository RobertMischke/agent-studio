# Wiki Pulse dashboard

Status: Concept (living). Slice PULSE-1 implemented (2026-07-10).

> Operator intent (2026-07-09): "When you open the wiki, you should see a history
> of the last changes: warnings, which things need to be sorted, what is being
> worked on - drift-grading stuff." PULSE is the generated entry view that answers
> that: it opens first, is not a wiki page, and is never editable.

Mockup: [mockups/wiki-pulse-dashboard.html](mockups/wiki-pulse-dashboard.html).

## 1. What Pulse is

Pulse is the landing surface the wiki opens on. It is a **generated view**, not a
stored page: it does not live in the [Workstream frame](engineering-workstream.md),
it is not part of the docs tree, it cannot be edited, and it is not prompt-known.
It is composed on demand from git history and the docs tree and degrades to an
empty state per section when a source is missing - never an error page.

It is served by a single endpoint,
`GET /api/projects/{project}/wiki/pulse`, so the landing surface costs **two git
walks** rather than the tree + recent + per-doc-history fan-out. That keeps Pulse
inside the "< 1s warm, do not multiply the slow calls" budget from the
wiki-performance work.

## 2. The three sections (PULSE-1)

### 2.1 Change feed

The recently-edited wiki pages (git author + timestamp, newest first), grouped by
day in the UI. Each row is enriched with:

- a **frame-area badge** - which Workstream area the page lives under
  (`EngineeringWorkstreamFrame.AreaForPath`), or none for a page outside the frame;
- a **task key** (e.g. `AGT-2014`) parsed from the page's frontmatter `task-key`
  first, then from the commit subject.

Clicking a row opens the page in the reader.

### 2.2 Inbox (needs sorting)

Loose / unfiled knowledge pages that need a home. Deterministic detection:

- a knowledge doc that sits **directly at the wiki root** and is not a
  conventional landing file (`README.md`, `index.*`, `home.md`);
- a knowledge doc dropped **inside the Workstream frame root but under no area**
  (the frame knows its own structure, so anything filed there but not in one of
  the five areas is stray).

An **empty inbox is the healthy state** and is shown as such.

### 2.3 Drift grading v1 (deterministic, no LLM)

Per Workstream frame area, how much has the code moved since each knowledge page
was last refreshed. For every page under an area (its immutable landing shell
excluded):

1. take the page's **last update** from git history;
2. count how many commits under the **code roots** landed after that timestamp;
3. band the count: **Fresh** (0-9), **Aging** (10-49), **Stale** (50+).

The **area grade is its worst page** and the bar reports the worst page's commit
count. An area with no filed pages reads **Empty**. The overall grade is the worst
area. The grade bar sits at the top of Pulse.

**Code roots** are every top-level repository directory except the wiki root
(`docs/`) and build-output / tooling folders (`node_modules`, `dist`, `bin`,
`obj`, `.git`, ...). This is project-agnostic: it discovers whatever source
folders a repo actually has (`backend/`, `frontend/`, `runner/`, ...) rather than
hard-coding a list.

The whole heuristic is one `git log` over the code roots for author dates plus one
docs walk for per-page last-update, so it is cheap and fully reproducible.

## 3. Empty states

Every section carries its own `available` + `reason`:

| Missing source | Feed | Inbox | Drift |
|---|---|---|---|
| No `docs/` folder | unavailable (reason) | unavailable (reason) | unavailable (reason) |
| No git repository | unavailable (reason) | available (filesystem) | unavailable (reason) |
| No pages under the frame | "no recent edits" if empty | healthy / listed | `Empty` grades + reason |

The UI renders the reason, never a stack trace or blank screen.

## 4. Scope boundary

In PULSE-1: the change feed, the inbox, and the drift grade bar.

**Not** in PULSE-1 (these are PULSE-2): a Warnings tile and an "In progress"
(live-runs) tile. Pulse is deliberately deterministic in v1 - no LLM, no live
run state.

## 5. Where it lives

- Backend: `ProjectDocsService.GetWikiPulse` composes the payload;
  `EngineeringWorkstreamFrame.AreaForPath` maps a page to its area;
  `GitService.GetCommitAuthorDatesUnderPaths` backs the drift count. Endpoint in
  `ProjectDocsEndpoints` (before the `/wiki/files` catch-all).
- Frontend: `app-wiki-pulse` (`features/project-detail/.../wiki-pulse/`) renders
  the view; the wiki section opens on it when no page is selected.
- Tests: `backend.Tests/WikiPulseTests.cs` (real temp git repo),
  `wiki-pulse.component.spec.ts`, and the wiki-section spec.
