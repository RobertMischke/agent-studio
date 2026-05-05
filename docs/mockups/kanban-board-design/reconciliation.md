# Reconciliation list: existing layout tasks vs the Kanban board spec

Each row reads: what the task delivered, where it conflicts with the spec in [README.md](README.md) and [taxonomy.md](taxonomy.md), and what the task must absorb to align.

This list is **not** an instruction to auto-modify those tasks. It is a surface for the user to decide which tasks need a follow-up, which can be aligned in their next continuation, and which already reconcile.

## 1. `vscode-style-layout-density-and-tabs` (4-review)

**What it delivered.** The `Frontend:VsCodeLayout` flag, a status bar, a denser detail header, and the chrome-density tables that this Kanban spec borrows from. Lives at `docs/mockups/vscode-layout/`.

**Conflicts with this spec.**
- Owns the chrome density rules but never declared which numbers apply to the board itself; the Kanban inherited "VS Code-ish" by accident, not by contract.
- Did not lock a grid-template-columns recipe; the board's flex-based layout drifted from the editor area's tab math.

**Required change to align.**
- Update `docs/mockups/vscode-layout/taxonomy.md` to delegate Kanban-specific decisions to `kanban-board-design/taxonomy.md`. One sentence: "The Kanban board follows `kanban-board-design/`; this file does not duplicate those rules."
- No CSS change. The flag remains independent.

## 2. `kanban-lane-grouping-collapse` (4-review)

**What it delivered.** The collapsed-rail rendering at 36 px wide, indicators (running, needs-input, error), per-lane localStorage persistence, drag-and-drop into a collapsed rail.

**Conflicts with this spec.**
- Rail width is 36 px; the spec locks 48 px (matches the activity-bar rail width carried from VS Code conventions).
- Collapse persistence is a global key, not per-project (`atp.kanban.collapsed.<project>`).
- Default collapse state is "all expanded"; the spec defaults `7-archive` collapsed.

**Required change to align.**
- Bump rail width from 36 px to 48 px in `job-column.ts` (`.column-rail` width and flex basis).
- Switch collapse persistence to a per-project key. Document the migration: read the legacy global key into the active project's key on first load, then drop the legacy key.
- Default `7-archive` collapsed for new projects.

## 3. `expanded-lifecycle-lanes-concept` (4-review)

**What it delivered.** The lane-vocabulary research document and the choice between filesystem states vs sidecar substates. Source of the seven-lane vocabulary the spec adopts.

**Conflicts with this spec.**
- None on the visual layer. The vocabulary the spec uses is the one this task chose.

**Required change to align.**
- None. The Kanban spec cites this task as the authority for lane names.

## 4. `lanes-with-explicit-human-review-step` (4-review)

**What it delivered.** The seven-lane state machine (`4-auto-review` and `5-human-review` as separate folders), migration from `4-review`, distinct visual treatment per lane.

**Conflicts with this spec.**
- The proposed "machine icon vs eye icon" chrome stays compatible.
- The proposed "distinct visual treatment for `4-auto-review` vs `5-human-review`" used inline background tints in early CSS sketches; the spec forbids background fills on lane headers and reserves color for outline tints only.

**Required change to align.**
- Replace any background-fill lane-header treatment with the outline-tint rule (1 px `--accent-blue` for active phase, no fill).
- Keep the icons (🤖, 👁️) as the lane-header icon; the spec adopts them.

## 5. `fix-kanban-lane-layout-overflow` (2-ready)

**What it delivered.** Identified the overflow regression, planned a Playwright spec at 1440x900, and proposed `flex: 1 1 0` with a 200 px min-width.

**Conflicts with this spec.**
- The spec uses `grid-template-columns: repeat(N, minmax(220px, 1fr))` instead of flex. The 220 px floor is 20 px more than the proposed 200 px.
- The Playwright assertion target ("six lanes side by side") is now seven lanes (or eight when failed-pickup is on).

**Required change to align.**
- Reframe the fix as "implement the locked grid template," not "tune the existing flex math."
- Update the Playwright assertion: at 1440 px, the seven default lanes render in a single row with `archive` collapsed by default. Use `expect(dashboard).toHaveCSS('grid-template-columns', /repeat\(7,/)`.
- Bump the floor from 200 to 220 px.

## 6. `chat-layout-integration-bridge` (2-ready)

**What it delivered.** A planned `Frontend:NextGenChat` feature flag, conversation-event projection, fixture coverage. Touches the chat surface, not the board directly.

**Conflicts with this spec.**
- None on the Kanban board layer.
- One indirect interaction: the chat side sheet shares the dashboard viewport. The spec's grid-template assumes a fixed 16 px outer padding on the board container, irrespective of the side-sheet width.

**Required change to align.**
- No board-level change. Add a one-line note in the chat task's prompt that the side sheet does not change board padding.

## 7. `improve-layout-of-the-details-page` (2-ready)

**What it delivered.** Detail-page header refinements; references `vscode-layout/taxonomy.md`. Touches the task-detail surface, not the board.

**Conflicts with this spec.**
- None on the board layer.

**Required change to align.**
- No board-level change. The detail page inherits density rules from `vscode-layout`, not from this Kanban spec.

## Summary

| Task                                                | Conflict?      | Required change                                                                |
|-----------------------------------------------------|----------------|--------------------------------------------------------------------------------|
| vscode-style-layout-density-and-tabs                | none (defer)   | one-line delegation note in `vscode-layout/taxonomy.md`                        |
| kanban-lane-grouping-collapse                       | width, default | rail 36 → 48 px; per-project collapse key; archive default-collapsed           |
| expanded-lifecycle-lanes-concept                    | none           | none                                                                           |
| lanes-with-explicit-human-review-step               | color rule     | drop background fill on lane headers; keep the icons                           |
| fix-kanban-lane-layout-overflow                     | grid recipe    | use `repeat(N, minmax(220px, 1fr))`; assert seven lanes, not six               |
| chat-layout-integration-bridge                      | none           | side sheet does not change board padding (note in prompt)                      |
| improve-layout-of-the-details-page                  | none           | none                                                                           |
