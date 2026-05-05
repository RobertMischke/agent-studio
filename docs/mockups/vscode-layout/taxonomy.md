# Chrome Element Taxonomy

Every visible element of today's task-detail page, mapped to its destination in the VS Code-style layout. Implementation slice 1 (this task) ships the items in the **slice 1** column; later slices ship the rest.

## Top-level shell (today's `.header`)

| Element today                       | Source                                       | Destination (slice 1)                                       | Destination (final) |
|-------------------------------------|----------------------------------------------|-------------------------------------------------------------|--------------------|
| Brand icon + "Agent · Task Processor" | `app.ts` `.header__brand`                   | Title bar (30 px), unchanged                                | Title bar          |
| Project tabs strip                  | `project-tabs.component`                     | Hidden inside the detail view; visible on board             | Activity bar       |
| Owner select ("All / Robert / …")   | `app.ts` `.client-filter`                    | Status bar (right-side cluster)                             | Status bar         |
| `+ Add Task` button                 | `app.ts` `.btn--create`                      | Activity bar bottom (or status bar)                         | Activity bar       |
| Devtools menu (⋮)                   | `app.ts` `.devtools-menu`                    | Status bar (right-side cluster)                             | Status bar         |
| Workspace banner                    | `workspace-banner.component`                 | Unchanged                                                   | Unchanged          |

## Detail view header (today's `.detail__header`)

| Element today                       | Source                                       | Destination (slice 1)                                       | Destination (final) |
|-------------------------------------|----------------------------------------------|-------------------------------------------------------------|--------------------|
| Back-to-board button (`←`)          | `detail-header.component`                    | Removed from detail view; activity-bar icon owns it         | Activity bar       |
| Project pill (initial + name)       | `detail-header.component`                    | Status bar (left cluster)                                   | Status bar         |
| Title (h2, editable)                | `detail-header.component`                    | Tab bar tab label (slice 2); single-row strip in slice 1    | Tab bar            |
| Created-at meta                     | `detail-header.component`                    | Meta panel                                                  | Meta panel         |
| State pill (review / progress / …)  | `detail-header.component`                    | Status bar (left cluster)                                   | Status bar         |
| `Complete & Next` button            | `detail-header.component`                    | Stays inline (it is an action, not chrome)                  | Stays inline       |

## Command deck (today's `.command-deck`)

| Element today                       | Source                                       | Destination (slice 1)                                       | Destination (final) |
|-------------------------------------|----------------------------------------------|-------------------------------------------------------------|--------------------|
| Project switcher (move task)        | `command-deck.component`                     | Meta panel (when open)                                      | Meta panel         |
| CLI type select                     | `command-deck.component`                     | Meta panel                                                  | Meta panel         |
| Model select                        | `command-deck.component`                     | Meta panel                                                  | Meta panel         |
| Start / Stop button                 | `command-deck.component`                     | Composer toolbar (action, stays accessible)                 | Composer           |
| Elapsed time                        | `command-deck.component`                     | Status bar (right cluster)                                  | Status bar         |
| `▾` collapse toggle                 | `command-deck.component`                     | Replaced by the Meta panel "i" toggle on the chat header    | Removed            |

## Pane toggle bar (today's `.pane-toggle-bar`)

| Element today                       | Source                                       | Destination (slice 1)                                       | Destination (final) |
|-------------------------------------|----------------------------------------------|-------------------------------------------------------------|--------------------|
| Prompt / Protocol / Git toggles     | `pane-toggle-bar.component`                  | Status bar (right cluster), one chip per pane               | Side-bar chevron sections |
| `Open in VS Code` action            | `pane-toggle-bar.component`                  | Status bar (right cluster)                                  | Status bar         |

## Pane chrome (today's `.pane__header`)

| Element today                       | Source                                       | Destination (slice 1)                                       | Destination (final) |
|-------------------------------------|----------------------------------------------|-------------------------------------------------------------|--------------------|
| Pane icon + title                   | `protocol-pane`, `prompt-pane`, `git-pane`   | Reduced padding (10 px → 6 px)                              | Tab title          |
| Live dot, summary spinner           | `protocol-pane.component`                    | Inline with title                                           | Inline             |
| Session chip, model chip, tokens    | `protocol-pane.component`                    | Meta panel; status bar shows model only                     | Meta panel         |
| Watchdog pill                       | `protocol-pane.component`                    | Status bar (right cluster)                                  | Status bar         |
| Rate-limit chip                     | `protocol-pane.component`                    | Meta panel; status bar shows tight summary                  | Meta panel         |
| Maximize / hide buttons             | each `pane__header`                          | Unchanged                                                   | Unchanged          |
| **NEW** "i" Meta toggle             | `protocol-pane.component`                    | Added in slice 1                                            | Persists           |

## Inspector tabs (today's `.inspector__tabs`)

| Element today                       | Source                                       | Destination (slice 1)                                       | Destination (final) |
|-------------------------------------|----------------------------------------------|-------------------------------------------------------------|--------------------|
| Protocol / Activity tabs            | `protocol-pane.component`                    | Unchanged (kept inside the editor area)                     | Editor tab strip   |
| Regenerate / Copy markdown buttons  | `protocol-pane.component`                    | Composer toolbar (next to Send)                             | Composer           |

## Status bar (NEW)

The status bar is added in slice 1. It is a single-line, never-wrapping, 22 px row anchored at the bottom of the viewport. Layout:

```
[ project · state · model ]                                  [ runs · auto · owner · ⋮ ]
```

Items are clickable. Click semantics:

- **project** opens the project filter
- **model** opens the model select in the Meta panel
- **runs** opens the run timeline
- **auto** toggles auto-pickup for the active project
- **owner** opens the owner filter

## Activity bar (NEW, slice 3)

48 px wide rail on the left, shown only when the flag is on. Top cluster:

- 📋 Board (`/`)
- 📁 Project switcher (one icon per watched project; filled when active)
- ➕ Add task

Bottom cluster:

- ⚙ Settings
- ⋮ Dev tools (when enabled)

In slice 1 the activity bar is omitted; project switching falls back to the status bar pill.

## Meta panel (NEW, slice 1)

A right-docked, collapsible panel inside the detail view. Default closed. Toggled by the "i" button on the protocol pane header. Width 280 px when open, persisted in `localStorage`.

Sections (chevron-collapsible):

1. **Identity** — owner, created-at, project move
2. **Runtime** — CLI type, model, session chip, tokens, rate limit
3. **Runs** — run count, last activity, run timeline opener
4. **Software** — branch, last commit, "Open in VS Code"

The Meta panel scrolls independently of the chat.

## Chat integration boundary

The VS Code layout flag owns app chrome and density. The next-generation chat flag owns conversation grammar and rendering. They are related but separate:

- `Frontend:VsCodeLayout` moves chrome to edges, tightens padding, and exposes the meta panel.
- `Frontend:NextGenChat` changes the Activity tab and side sheet transcript into the v5 chat grammar.

Do not use the layout work as permission to remove current chat functions. The task composer modes, run timeline, Trace mode, token and quota links, screenshots, commits, auto-eval banner, and raw technical output must stay reachable while the renderer changes.
