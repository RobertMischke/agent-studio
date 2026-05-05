# VS Code-style Layout: maximise content area, collapsible meta panels, tabs

Design exploration. Target surface for a denser, VS Code-shaped chrome around the existing job detail view. Flag-gated implementation lives behind `Frontend:VsCodeLayout` (default off); see [taxonomy.md](taxonomy.md) for the per-element migration map and [ui.html](ui.html) for the interactive reference.

This folder is the spec. The implementation tracks it. Deviations require a note in `taxonomy.md`.

## Why

Today's task detail spends roughly a third of the vertical space on header chrome before the chat starts: a brand row, the project tabs strip, the breadcrumb + owner pill + model selector, the panel toggle bar, copy / regenerate buttons, the inspector tabs, and only then the chat body. On a 900-px viewport the user reads ~600 px of conversation; on a long chat that is the wrong tradeoff.

VS Code solves the same problem on the editor surface. Conversation tools (Copilot Chat, Claude Code, Codex, Cursor) are converging on the same shape. We adopt the conventions; we do not adopt VS Code's brand.

## Conventions carried over from VS Code

The numbers are deliberate. They are what VS Code uses, and they are calibrated against real-world usage, not theory.

| Surface              | Height / padding         | Notes                                                                                          |
|----------------------|--------------------------|------------------------------------------------------------------------------------------------|
| Title bar            | 30 px                    | Single horizontal row. Brand on the left, command-palette-shaped center, window controls right. |
| Activity bar         | 48 px wide               | Icon-only, persistent, left rail. One icon per major top-level destination.                    |
| Side bar             | resizable                | Hosts collapsible sections (chevron headers). Multiple sections share one panel.               |
| Editor tabs          | 30 px high               | Kind icon + label + close button. Unsaved-dot replaces the close button when dirty.            |
| Editor               | 14 px text               | The reading area. Padding 8 px / 12 px. Hairline borders only.                                 |
| Status bar           | 22 px                    | Single-line, never wraps. Icon + short label per item. Click opens the detail.                 |
| Chrome text          | 13 px                    | Side bar headers, tab labels, status items.                                                    |
| Editor text          | 14 px                    | Body content (chat, markdown, log).                                                            |
| Border weight        | 1 px hairline            | No 2 px frames, no rounded boxes. Borders separate, they do not enclose.                       |
| Panel resize handles | 4 px draggable, persisted | Between activity bar / side bar / editor / panel; widths land in `localStorage`.              |
| Density rule         | 6 px padding default     | Anything above 8 px in chrome is suspect.                                                      |

Catppuccin tokens map onto the dark VS Code palette as follows:

- `--surface-0` (#0f0f1a) -> activity bar background
- `--surface-1` (#181825) -> title bar, status bar, side bar
- `--surface-2` (#1e1e2e) -> editor / chat body
- `--border-1` (rgba(255,255,255,0.06)) -> hairline separators
- `--accent` (#a5b4fc) -> active tab underline, active activity-bar icon

## Layout regions

```
+----------------------------------------------------------+
| Title bar (30 px) | brand | quick-find center | controls |
+--+-------------------------------------------------------+
|A | Tab bar (30 px)  task-1.md  task-2.md  +              |
|c +-------------------------------------------------------+
|t |                                                       |
|i |                                                       |
|v |  Editor area (chat reads to within 24 px of viewport  |
|i |  top — meta info stays out of the way by default)     |
|t |                                                       |
|y |                                                       |
|  +-------------------------------------------------------+
|  | Composer (sticky, 1-3 rows)                           |
|  +-------------------------------------------------------+
|  | Status bar (22 px)  project · mode · model · runs · 🔵 |
+--+-------------------------------------------------------+
```

## Information architecture

Three layers, mapped 1:1 onto VS Code surfaces:

1. **Persistent navigation (activity bar + status bar).** The project switcher (Runbook / Agent Task Processor / + add) becomes activity-bar icons. Owner pill, current model, run counts, "auto pickup" badge, and the panel toggle move to the status bar. Both are always visible but never steal vertical content space.

2. **Per-task chrome (tab bar + meta side panel).** Multiple open tasks become editor tabs (30 px). The detail header collapses to a thin breadcrumb-less title strip with one "i" affordance that opens a collapsible Meta side panel. Meta is closed by default — the user opted into a chat-first reading mode.

3. **Content (editor area + composer).** The chat / Markdown body fills the remaining space. The composer is sticky at the bottom of the editor area and inherits panel padding (8 px / 12 px), not the more decorative 14 px the existing pane uses.

## Default state and persistence

| State                         | Default     | localStorage key                  |
|-------------------------------|-------------|-----------------------------------|
| Feature flag                  | off         | `agent-taskboard:vscode-layout`   |
| Meta panel open               | closed      | `agent-taskboard:vscode-meta-open` |
| Activity bar visible          | yes         | (always on when flag is on)       |
| Status bar visible            | yes         | (always on when flag is on)       |
| Side bar (task nav) width     | 240 px      | `agent-taskboard:side-width`      |
| Meta panel width              | 280 px      | `agent-taskboard:meta-width`      |

Flipping the feature flag does not destroy the existing layout. Both code paths coexist; the flag picks one.

## Non-goals

- We do not adopt the VS Code brand or icon set. Our brand stays.
- We do not introduce a command palette in this slice. The activity bar is enough.
- We do not split the markdown editor and protocol view into separate VS Code "files." A task is still one document; tabs are one tab per task.
- We do not move backend state. The flag and the persisted widths are frontend-only.

## Files

- [taxonomy.md](taxonomy.md) — every chrome element on the task-detail page today, mapped to its destination in the new layout.
- [ui.html](ui.html) — interactive reference. Two states reachable in-page: kanban board and a single open task with a long chat. The chat reads to within 24 px of the viewport top.
- [evidence/](evidence/) — Playwright screenshots from the implementation slice.

## Implementation slices

This mockup is the spec for an incremental rollout. The first slice (this task) ships the chrome reduction; later slices replace the structural pieces.

| Slice | Scope                                                                                  | State |
|-------|----------------------------------------------------------------------------------------|-------|
| 1     | Feature flag, density CSS, status bar, hidden header tabs in detail view, meta toggle  | this task |
| 2     | Replace breadcrumb with editor-style tab bar, persist tab order                        | next  |
| 3     | Activity bar with full project switcher, owner, "+ add"                                | next  |
| 4     | Side-bar chevron sections (Source Control, Runs, Screenshots) replacing pane toggle    | next  |
| 5     | Resizable splits with persisted widths in localStorage                                 | next  |
