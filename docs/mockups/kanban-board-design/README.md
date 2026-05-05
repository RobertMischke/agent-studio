# Kanban board design specification

The single source of truth for how the project Kanban board looks and behaves. Future layout tasks read this file before changing CSS, components, or grid math. Deviations require an amendment recorded in [taxonomy.md](taxonomy.md).

The interactive reference is [ui.html](ui.html) (open it in a browser; the page renders three viewport widths inline). The reconciliation table for the seven existing layout tasks is [reconciliation.md](reconciliation.md). The first-slice CSS implementation lands behind `Frontend:KanbanDesignSpecV1` (default off).

## Why this spec exists

Five layout tasks have touched the Kanban board in the last few days, plus two more sit in `2-ready`:

- `vscode-style-layout-density-and-tabs` (chrome density)
- `kanban-lane-grouping-collapse` (collapse logic)
- `expanded-lifecycle-lanes-concept` (lane count)
- `lanes-with-explicit-human-review-step` (7-lane redesign)
- `fix-kanban-lane-layout-overflow` (overflow rescue)
- `chat-layout-integration-bridge`, `improve-layout-of-the-details-page` (queued)

Each picked its own width math, padding, lane-header treatment, and density. The visible board mess is the predictable result of layout work without a shared reference. This spec locks the rules every future task must reconcile against.

## What the Kanban board is

The Kanban board is the project-level view that shows the full lifecycle of every task in the active project, side by side, as a horizontal stack of lanes. The user drags tasks between lanes; the orchestrator and the runtime move tasks between lanes too. The board is the single visible answer to the question *"where is each task right now?"*.

The board is read first, acted on second. Density wins over decoration.

## Lane vocabulary

The board renders the seven post-ADR-0025 lanes in three visual phase groups. Lane vocabulary is fixed; phase groupings are presentation only.

| Slot | Folder state         | Title          | Phase    | Owner         | Icon |
|------|----------------------|----------------|----------|---------------|------|
| 1    | `1-preparation`      | In Preparation | Backlog  | human         | 📋   |
| 2    | `2-ready`            | Ready          | Backlog  | human         | 📦   |
| 3    | `3-progress`         | In Progress    | Active   | agent         | 🔵   |
| 4    | `4-auto-review`      | Auto Review    | Active   | orchestrator  | 🤖   |
| 5    | `5-human-review`     | Human Review   | Active   | human         | 👁️   |
| 6    | `6-completed`        | Completed      | Done     | human         | 🟢   |
| 7    | `7-archive`          | Archive        | Done     | system        | 🗄️   |

An optional eighth lane, `failed-pickup`, joins the board only when its job count is non-zero. It renders to the right of `3-progress` with the warning treatment from [taxonomy.md](taxonomy.md). It never reserves space when empty.

Phase groups carry a small uppercase label above their lanes (`BACKLOG · human`, `ACTIVE · agent`, `DONE · human`). Groups are visual only: drag-and-drop and reorder ignore them.

## The locked rules

Numbers are deliberate. They are extracted from existing successful Kanban tools (Linear, Trello, Asana) and from the in-house VS Code-shaped chrome direction. A future task may not pick different values without amending [taxonomy.md](taxonomy.md).

### Grid

- The lane row uses `grid-template-columns: repeat(N, minmax(220px, 1fr))`, where `N` is the count of currently visible lanes. Collapsed-to-rail lanes count as one slot but use `48px` instead of `1fr`.
- The optional `failed-pickup` lane increases `N` by 1 only when it has at least one task.
- Horizontal scroll is the graceful fallback when the viewport cannot host the floor (`overflow-x: auto` on the dashboard wrapper).

### Lanes

- Lane header height: 36 px, single row, no wrap.
- Lane padding: 8 px horizontal, 4 px vertical.
- Lane background: `--surface-1` (#181825). No gradient. No shadow.
- Lane outline: 1 px hairline `--border-1` (rgba(255,255,255,0.06)).
- Collapsed rail width: 48 px. Click anywhere on the rail to expand. The rail keeps the count badge, the running/needs-input/error indicators, and a vertical title.
- Phase group: 4 px gap between lanes inside the group, 12 px gap between groups. The group header is 18 px, 11 px text, uppercase, letter-spacing 0.06 em. No background fill, no border.

### Cards

- Card minimum height: 56 px.
- Card maximum height: 200 px (truncate body, never the title).
- Card padding: 10 px.
- Card border-radius: 6 px.
- Card background: `--surface-2` (#1e1e2e).
- Selected card: 2 px outline in `--accent` (#a5b4fc). The brightness does not change.
- Card stack: 8 px gap between cards inside a lane.

### Density and typography

- Chrome text 13 px (lane headers, group labels, status chips on cards).
- Body text 14 px (card title, card meta).
- No font above 16 px on the board.
- The 4 / 8 / 12 / 16 px scale governs every spacing value. No 5, 7, 10, 14 px; no off-scale values whatsoever.

### Color

- Lane headers do not use background fill to indicate state. They use a 1 px outline tint per phase: backlog hairline-only, active accent-blue, done muted green. The fill stays `--surface-1`.
- Cards do not use background color to indicate status. Status badges (running, needs-input, error) sit inside the card as 13 px chips, never as full-card tints.
- The `failed-pickup` lane is the single exception: a 1 px amber outline plus a 12 px amber dot in the lane header.

### Motion

- Drag uses `transform` and `opacity` only. Never width, height, or margin.
- Drop animates the card into its final slot over 180 ms (`ease-out`).
- Lane reorder over 200 ms (`ease-in-out`).
- No background-color transitions on cards. A status change updates the badge, not the card body.

### Collapse and persistence

- Each lane has a chevron in its header. Click collapses the lane to the 48 px rail. Click the rail to expand.
- Collapse state is persisted per project in `localStorage` under `atp.kanban.collapsed.<project>`. The default for new projects is: every lane expanded except `7-archive` (collapsed by default; the archive is bulk evidence, not active work).
- The `failed-pickup` lane cannot be collapsed while non-empty. It auto-disappears when the count reaches zero.

## Defaults

| State                           | Default           | Persistence                                  |
|---------------------------------|-------------------|----------------------------------------------|
| Feature flag                    | off               | `atp.flag.kanbanDesignSpecV1`                |
| Lane collapse (per project)     | all expanded except archive | `atp.kanban.collapsed.<project>`   |
| Optional failed-pickup lane     | shown only when non-empty | derived from data, not persisted     |
| Phase group labels              | shown              | non-toggleable in v1                         |

## Files

- [taxonomy.md](taxonomy.md) - every locked decision, by element. Future amendments append here.
- [ui.html](ui.html) - interactive reference. Three viewport widths in one page (1280, 1440, 1920). Toggles for expanded/collapsed lanes and the optional `failed-pickup` lane.
- [reconciliation.md](reconciliation.md) - the seven existing layout tasks, what each delivered, where each conflicts with this spec, and the change each must absorb.

## Implementation slices

This spec is the contract; implementation is incremental.

| Slice | Scope                                                                               | State |
|-------|-------------------------------------------------------------------------------------|-------|
| 1     | Feature flag, grid template, lane header height, spacing rhythm, card sizing rules  | this task |
| 2     | Card status chips replace background tints; selection outline rule                   | next  |
| 3     | Per-project collapse persistence and default archive collapsed                       | next  |
| 4     | Optional `failed-pickup` lane appears only when non-empty                            | next  |
| 5     | Drag/drop motion rules (transform + opacity, 180 / 200 ms timings)                   | next  |

Slice 1 lands behind `Frontend:KanbanDesignSpecV1`, default off, on for the user's developer build. Both the legacy and the spec layouts coexist; the flag picks one.

## Non-goals

- The spec does not invent a new lane vocabulary. The seven lanes come from ADR-0025 (`expanded-lifecycle-lanes-concept`, `lanes-with-explicit-human-review-step`).
- The spec does not change the drag-and-drop semantics. It only locks the visual rules around them.
- The spec does not redesign the card body content. Card anatomy is referenced from `taxonomy.md` and stays compatible with `app-job-card`.
- The spec does not block the chat or task-detail mockups. Those surfaces stay under their own specs and feature flags.
