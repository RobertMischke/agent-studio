# Kanban board taxonomy

Every locked decision for the project Kanban board, by element. The implementation tracks this file. Deviations require an amendment in the **Amendments** section at the bottom.

References shown alongside each decision are not invented; they come from prior art used by the team or from the existing design-principles file. When this spec borrows a number, it cites where the number came from.

## Lane row container

| Property                | Value                                          | Why                                                          |
|-------------------------|------------------------------------------------|--------------------------------------------------------------|
| Display                 | `grid`                                         | Equal-width lanes, predictable reflow                        |
| `grid-template-columns` | `repeat(N, minmax(220px, 1fr))`                | 220 px floor keeps a usable card-width even at 1280 viewport |
| Gap                     | 12 px between phase groups, 4 px inside groups | 4 / 8 / 12 / 16 px scale (design-principles.md)              |
| Padding                 | 16 px outer (top, right, bottom, left)         | Same as the today's `.dashboard` outer padding               |
| Overflow                | `overflow-x: auto`                             | Graceful fallback when N exceeds the viewport budget         |
| Background              | `--surface-2` (#1e1e2e)                        | Matches editor area in vscode-layout                         |

`N` is the count of currently visible lanes (collapsed-to-rail still counts as 1). The optional `failed-pickup` lane increments `N` only when its task count > 0.

## Lane (expanded)

| Property        | Value                                                    | Why                                          |
|-----------------|----------------------------------------------------------|----------------------------------------------|
| Background      | `--surface-1` (#181825)                                  | Sits 1 step lighter than the board surface   |
| Border          | 1 px hairline `--border-1` (rgba(255,255,255,0.06))      | Linear / VS Code conventions                 |
| Border-radius   | 6 px                                                     | Matches card radius for visual rhythm        |
| Padding         | 8 px horizontal, 4 px vertical                           | Linear lane padding reference                |
| Min-width       | 220 px (via grid `minmax`)                               | Card title + 2 chips remain readable         |
| Display         | `flex` column                                            | Header on top, body fills                    |
| Gap             | 8 px between header and body                             | 4 / 8 / 12 / 16 px scale                     |

## Lane header

| Property         | Value                                                    | Why                                          |
|------------------|----------------------------------------------------------|----------------------------------------------|
| Height           | 36 px, fixed                                             | VS Code editor tab + 6 px breathing room     |
| Padding          | 0 (inherits lane padding)                                | Avoid double padding                         |
| Display          | `flex` row, `align-items: center`, `gap: 8 px`           | Icon, title, count, collapse button          |
| Title            | 13 px, weight 600, `--text-primary` (#e2e8f0)            | Chrome density rule                          |
| Count badge      | 12 px tabular-nums, padding 2 / 8 px                     | Same as today's `.column__count`             |
| Collapse chevron | 22 px square button, 1 px border                         | Hit target ≥ 22 px                           |
| Outline tint     | none for backlog, `--accent-blue` for active, muted green for done | Phase legibility without background fill |

The lane header **never** uses background fill to indicate state. The phase color shows as a 1 px hairline outline only.

## Lane (collapsed rail)

| Property      | Value                                                  | Why                                       |
|---------------|--------------------------------------------------------|-------------------------------------------|
| Width         | 48 px, fixed (overrides grid `1fr`)                    | VS Code activity-bar width carry-over     |
| Background    | `--surface-1` (#181825)                                | Same as expanded lane                     |
| Click target  | Whole rail toggles back to expanded                    | One affordance, not two                   |
| Indicators    | Count, running dot, needs-input dot, error dot, active CLI badge | Carry-over from kanban-lane-grouping spec |
| Title         | Vertical (writing-mode rl), 11 px, uppercase           | Carry-over from kanban-lane-grouping spec |

## Card

| Property        | Value                                                   | Why                                               |
|-----------------|---------------------------------------------------------|---------------------------------------------------|
| Background      | `--surface-2` (#1e1e2e)                                 | Lifts above lane                                  |
| Border-radius   | 6 px                                                    | Matches lane rhythm                               |
| Padding         | 10 px                                                   | 4 / 8 / 12 / 16 px scale (one-off because the original card-padding sat at this value; carry-over) |
| Min-height      | 56 px                                                   | Title + 1 meta row remain visible                 |
| Max-height      | 200 px                                                  | Truncate body, never the title                    |
| Title           | 14 px, weight 600                                       | Editor body density                               |
| Meta            | 13 px chrome density                                    | Chrome density rule                               |
| Selected        | 2 px outline `--accent` (#a5b4fc), no fill change       | Selection without status confusion                |
| Status chips    | 13 px, inline at the bottom of the card                 | Status never changes the card background          |

> Note on the 10 px padding exception: this is the only deviation from the 4 / 8 / 12 / 16 px scale, and it exists because every existing card already uses 10 px and a v1 reflow would force every consumer to retest. The amendment is recorded; future cards must use 8 px or 12 px.

## Phase group

| Property      | Value                                          | Why                                            |
|---------------|------------------------------------------------|------------------------------------------------|
| Header        | 18 px tall, 11 px text, uppercase, 0.06 em letter-spacing | Carry-over from existing `.lane-group__head` |
| Header fill   | none                                           | Visual separation from lanes via gap, not box  |
| Header border | none                                           | Same                                           |
| Inter-lane gap inside group | 4 px                              | Cohesion within a phase                        |
| Inter-group gap             | 12 px                             | Visual separation between phases               |

Phase groups are presentation only. Drag-and-drop and reorder ignore them.

## Optional `failed-pickup` lane

| Property       | Value                                                      | Why                                              |
|----------------|------------------------------------------------------------|--------------------------------------------------|
| Visibility     | renders only when at least one job has `failed-pickup` state | No empty-lane noise on the happy path          |
| Position       | between `3-progress` and `4-auto-review`                   | Surface failures next to the lane that emits them |
| Outline tint   | 1 px amber (`#f59e0b`)                                     | The single colored outline on the board         |
| Header dot     | 12 px amber dot inside the header                          | Quick legibility in a long board                |
| Collapse       | not collapsible while non-empty                            | Failures must remain visible                    |

## Density and typography

| Surface             | Size  | Weight | Color token             |
|---------------------|-------|--------|-------------------------|
| Lane header title   | 13 px | 600    | `--text-primary`        |
| Phase group label   | 11 px | 700    | `--text-muted`          |
| Card title          | 14 px | 600    | `--text-primary`        |
| Card meta           | 13 px | 400    | `--text-muted`          |
| Count badge         | 12 px | 700    | `--text-muted`          |
| Status chip         | 13 px | 500    | `--text-primary`        |

No font above 16 px on the board.

## Color tokens

The board uses the existing Catppuccin tokens already wired into the app.

| Token             | Value                          | Usage                              |
|-------------------|--------------------------------|------------------------------------|
| `--surface-0`     | #0f0f1a                        | activity bar (carried over)        |
| `--surface-1`     | #181825                        | lane background                    |
| `--surface-2`     | #1e1e2e                        | dashboard background, card background |
| `--border-1`      | rgba(255,255,255,0.06)         | lane and card hairline             |
| `--accent`        | #a5b4fc                        | selection outline                  |
| `--accent-blue`   | #38bdf8                        | active phase lane outline          |
| `--accent-amber`  | #f59e0b                        | failed-pickup outline and dot      |
| `--text-primary`  | #e2e8f0                        | titles                             |
| `--text-muted`    | #94a3b8                        | meta, count badges                 |

## Motion

| Surface       | Property            | Duration | Easing       | Why                                  |
|---------------|---------------------|----------|--------------|--------------------------------------|
| Drag          | transform, opacity  | n/a (live) | linear     | GPU-cheap, no reflow                 |
| Drop          | transform           | 180 ms   | ease-out     | Snap feel, not a slide               |
| Lane reorder  | transform           | 200 ms   | ease-in-out  | Slightly slower for spatial cues     |
| Hover         | none                | n/a      | n/a          | Hover does not redraw the board      |
| Status change | badge swap          | n/a      | n/a          | Card body never re-tints             |

No `background-color` transitions on cards. Period.

## Spacing rhythm

The 4 / 8 / 12 / 16 px scale is the only allowed scale on the board. The single exception is card padding (10 px), recorded as an amendment.

## Persistence

| Key                              | Value                                            | Notes                              |
|----------------------------------|--------------------------------------------------|------------------------------------|
| `atp.flag.kanbanDesignSpecV1`    | `'1'` enables the spec layout                    | Frontend-only feature flag         |
| `atp.kanban.collapsed.<project>` | JSON array of collapsed lane state names         | Per-project, never global          |

The default for a new project is: every lane expanded except `7-archive`.

## Amendments

Amendments are dated and signed by the task slug that introduced them. A future task that needs to deviate appends here; it does not silently break the rule.

| Date       | Task slug                                  | Rule changed                                                | Reason                                       |
|------------|--------------------------------------------|-------------------------------------------------------------|----------------------------------------------|
| 2026-05-05 | kanban-board-design-spec-mockup-first      | Card padding fixed at 10 px, off the 4 / 8 / 12 / 16 scale  | Carry-over from existing card stack; one-off |
