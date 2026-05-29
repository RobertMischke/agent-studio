# Audit — Tab Bars

Tab-strip surfaces in `frontend/src/app/`. Canonical: [`<app-pane-tabs>`](../../frontend/src/app/components/pane-tabs/pane-tabs.component.ts) (introduced in F38), with two variants:

- `pane-tabs--header` — full-height tab strip projected into `<app-pane-header>`'s `tabs` slot. Active tab lifts into the body via a 2px accent bottom-border.
- `pane-tabs--pill` — compact pill toggle group inside a pane body. Active tab "raises" against the surrounding elevated surface.

Both variants share `.pane-tab`, `.pane-tab__badge`, `.pane-tab__icon`, `.pane-tab__spinner`, `.pane-tab__livedot` so consumers get identical typography, icon sizing, and spacing.

## Inventory

| Site                                              | Reads canonical? | Notes                                       |
| ------------------------------------------------- | ---------------- | ------------------------------------------- |
| Prompt-pane tabs (Description / Files / Overview) | ✅ via `<app-pane-tabs variant="header">` | Header variant |
| Protocol-pane tabs (Protocol / Activity)          | ✅ via `<app-pane-tabs variant="header">` | Header variant; activity-first reorder via data-attribute |
| Studio-shell main tab strip                       | ❌ `.studio-tab` (per-feature) | One-off tab strip for the shell editor tab host |
| Project-tabs strip (header chrome)                | ❌ `.project-tab` (per-feature) | Top-header project switcher |
| Pane-toggle-bar                                   | ❌ `.pane-toggle-bar` (per-feature) | Old pane toggle; could migrate to `pill` variant |

## Findings

**`<app-pane-tabs>` is the only canonical tab component and is correctly used by the two highest-traffic surfaces** (prompt-pane, protocol-pane). The studio-shell editor tab strip and the top-header project switcher each have their own implementation; both predate F38 and the per-feature requirements (drag-reorder for the editor strip, brand-coloured chips for the project switcher) made converging on `<app-pane-tabs>` not worthwhile at the time.

Two open questions:

1. Should `<app-pane-tabs>` grow a third variant (`variant="strip"`) that captures the editor-strip shape (longer tabs, close button per tab, drag-reorder)? Probably yes once a second consumer needs it; today it is one site.
2. Should the project-tabs strip read the canonical tab tokens (border-bottom accent, hover wash) even when it stays a separate component? Yes — that is a small slice and gets it consistent without merging the components.

## Migration consideration

Tabs are the **closest family in the codebase to "already converged"**. The follow-up work is small. See [tabs.md](tabs.md) for the proposed two slices.
