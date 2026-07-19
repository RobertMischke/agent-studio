# Wiki Pulse dashboard

Status: Concept (living). Slices PULSE-1 and PULSE-2 implemented (2026-07-11).
Updated 2026-07-19: the Workstream frame was retired ("nicht bewährt, umbauen");
drift groups and area badges are now the **real top-level docs folders**, and
the `human-action` warning became a folder-independent frontmatter convention
(see [../contracts/wiki-tree.md](../system/contracts/wiki-tree.md)). Frame wording
below is updated where behavior changed.

> Operator intent (2026-07-09): "When you open the wiki, you should see a history
> of the last changes: warnings, which things need to be sorted, what is being
> worked on - drift-grading stuff." PULSE is the generated entry view that answers
> that: it opens first, is not a wiki page, and is never editable.

Mockup: [mockups/wiki-pulse-dashboard.html](mockups/wiki-pulse-dashboard.html).

## 1. What Pulse is

Pulse is the landing surface the wiki opens on. It is a **generated view**, not a
stored page: it is not part of the docs tree, it cannot be edited, and it is not
prompt-known.
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

- an **area badge** - the top-level docs folder the page lives under (first path
  segment, order prefix stripped), or none for a page at the docs root;
- a **task key** (e.g. `AGT-2014`) parsed from the page's frontmatter `task-key`
  first, then from the commit subject.

Clicking a row opens the page in the reader.

### 2.2 Inbox (needs sorting)

Loose / unfiled knowledge pages that need a home. Deterministic detection: a
knowledge doc that sits **directly at the wiki root** and is not a conventional
landing file (`README.md`, `index.*`, `home.md`).

An **empty inbox is the healthy state** and is shown as such.

### 2.3 Drift grading v1 (deterministic, no LLM)

Per **top-level docs folder that holds pages**, how much has the code moved
since each knowledge page was last refreshed. For every page under a folder:

1. take the page's **last update** from git history;
2. count how many commits under the **code roots** landed after that timestamp;
3. band the count: **Fresh** (0-9), **Aging** (10-49), **Stale** (50+).

The **folder grade is its worst page** and the bar reports the worst page's
commit count. Folders without pages do not appear. Group order follows the saved
`docs/app/config/wiki-order.json` root order, unlisted folders behind in the tree's
default order (numeric `NN-` prefix, then name). The
overall grade is the worst folder. The grade bar sits at the top of Pulse.

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
| No pages in any top folder | "no recent edits" if empty | healthy / listed | no groups + reason |

The UI renders the reason, never a stack trace or blank screen.

## 4. Scope boundary

In PULSE-1: the change feed, the inbox, and the drift grade bar.

**Not** in PULSE-1 (these are PULSE-2): a Warnings tile and an "In progress"
(live-runs) tile. Pulse is deliberately deterministic in v1 - no LLM, no live
run state.

### 4.1 PULSE-2 warnings and live work

PULSE-2 adds two generated tiles. Warnings combines pages that carry live
`human-action` frontmatter (a folder-independent convention:
`human-action: <text>` plus `status: observed|active`, wherever the page lives)
with deterministic dead-internal-link detection. In progress lists only live
tasks whose task-aware working tree contains changes below `docs/`, with task
key, lane, runtime, and changed-doc count. (The former frame-violation,
page-budget, and collector/curator maintenance summaries were retired with the
Workstream frame, 2026-07-19.)

Update (GRADE-1, 2026-07-10, AGT-2051): the Warnings surface arrived as the
**Critical pages** section plus a **Grade all pages** trigger, driven by an
LLM grade per page written into the companion sidecars. The deterministic drift
bar above stays unchanged; the LLM grade *supplements* it. See
[wiki-grading-run.md](wiki-grading-run.md).

## 5. Where it lives

- Backend: `ProjectDocsService.GetWikiPulse` composes the payload;
  `ProjectDocsService.TopFolderForPath` maps a page to its top-level folder;
  `GitService.GetCommitAuthorDatesUnderPaths` backs the drift count. Endpoint in
  `ProjectDocsEndpoints` (before the `/wiki/files` catch-all).
- Frontend: `app-wiki-pulse` (`features/project-detail/.../wiki-pulse/`) renders
  the view; the wiki section opens on it when no page is selected.
- Tests: `backend.Tests/WikiPulseTests.cs` (real temp git repo),
  `wiki-pulse.component.spec.ts`, and the wiki-section spec.

## v1.1 (operator, 2026-07-10): LLM-graded page reports on top

Every page gets a machine-written assessment report ("Meter-Dokument":
grade + short feedback) stored in its `.meta.json` sidecar. A **global
trigger on this dashboard** runs the grading over all pages with an
operator-chosen, relatively strong model (picked at trigger time; default
from a new workspace "maintenance model" setting - deliberately NOT the
project pipeline models). Critically graded pages surface in the
Warnings/Drift tiles next to the deterministic heuristic. Implementation:
AGT-2051 (run + reports + tile), AGT-2052 (meta panel expand/collapse UX,
merged), AGT-2053 (bidirectional wiki-task cross-references in JSON
metadata, tolerant of deletions - dangling refs render as "existed once").
