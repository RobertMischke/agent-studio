# Audit — Pills, Chips, Badges, Tags

Pill-shaped semantic surfaces in `frontend/src/app/`. The canonical recipe is the SCSS mixin `m.chip($accent, $alpha-bg, $alpha-border)` in [`frontend/src/styles/_mixins.scss`](../../../../frontend/src/styles/_mixins.scss):

```scss
@include m.chip(var(--studio-accent-warn));        // small warn-tinted pill
@include m.chip(var(--studio-accent), 0.18, 0.5);  // brand pill, stronger
```

The mixin ships height 18px, padding 0 8px, border-radius 9px (pill shape), 10.5px uppercase text, accent-tinted background + border. Components that want a different shape (count-badge, lane-pill, kbd) build their own; the mixin is for the "small accent-tinted status pill" pattern specifically.

## Family A — Status / mode pill (mixin-eligible)

Inventory: `.column__status-pill`, `.evidence-row__ack-pill`, `.job-card__execution-pill`, `.job-card__issue-pill`, `.job-card__loop-pill`, `.job-card__pending-pill`, `.job-card__phase-pill`, `.job-card__review-pill`, `.job-card__state-pill`, `.job-card__type-pill`, `.job-card__auto-review-pill`, `.pdov__score-pill`.

| Site                     | Reads canonical? | Notes                                                   |
| ------------------------ | ---------------- | ------------------------------------------------------- |
| `.column__status-pill`   | ✅ via mixin     | Reference implementation, uses `@include m.chip(...)`   |
| `.job-card__*-pill`      | ❌ inlined       | Each rebuilds the chip recipe in BEM; should switch     |
| `.evidence-row__ack-pill`| ❌ inlined       | Same                                                    |
| `.pdov__score-pill`      | ❌ inlined       | Same                                                    |

**Findings.** The mixin is widely under-used. Twelve+ sites rebuild the recipe. **Migration target**: replace inlined `display: inline-flex; ...; height: 18px; padding: 0 8px; border-radius: 9px; ...;` blocks with `@include m.chip(...)`. Per-file slices, not big-bang.

## Family B — Chip (subtler than pill)

Inventory: `.commandbar__field--chip`, `.commandbar__select--chip`, `.convo-tools__chip-count`, `.detail__key-chip`, `.job-card__key-chip`, `.job-card__owner-chip`, `.job-card__tag-chip`, `.obs__issue-chip`, `.pane__session-chip`, `.pane__telemetry-chip`, `.sheet__context-chip`.

Chips here mean "borderless or subtly-bordered inline label" — slightly different from the pill recipe (no accent tint, just a neutral elevated background). The canonical recipe should be: `--studio-bg-elevated` background, `--studio-fg-dim` text, `border-radius: 4px (sm)`, `padding: 0 6px`, `height: 18px`. Today every consumer rebuilds it. **Migration target**: add `@mixin chip-neutral` to `_mixins.scss` in a follow-up and migrate the eleven sites.

## Family C — Badge (count badge, small numeric)

Inventory: `.column__subsection-count`, `.convo-tools__bin-count`, `.detail__pager-count`, `.job-card__git-count`, `.obs__kind-badge`, `.pane__telemetry--badge`, `.pane-tab__badge` (in `pane-tabs.component.scss`), `.proj-detail__banner-count`, `.sec-panel__sev-count`, `.vdbg__phase-count`, `.vdbg__tab-badge`, `.wss__bucket-count`.

Already-canonical: `.pane-tab__badge` in `pane-tabs.component.scss` — `min-width: 16px`, `height: 16px`, `padding: 0 5px`, `background: --studio-accent`, `border-radius: 8px`. Twelve others rebuild similar geometry. **Migration target**: `@mixin badge-count` for the recurring count-badge pattern.

## Family D — Tag (free-form label, no count, no accent)

Inventory: `.create-dialog__enhance-tag`, `.psr-modal__file-tag`, `.psr-modal__skill-tag`, `.steer-card__upload-btn` (mislabeled as button, behaves like a tag).

Four sites. Border + plain text + small padding. **Migration target**: TBD — the four sites are similar enough that a `@mixin tag` would work, but the use-case is narrow.

## Family E — Lane pill (already canonical-ish via `--lane-*` tokens)

Lane pills (Backlog / Ready / Progress / ... ) all read `--lane-*` tokens directly; the geometry is duplicated per-board-surface (column header, job card overlay, filter sidesheet pill list). The geometry is consistent (height 22px, radius 11px, padding 2px 8px, font 11px) but defined per-component.

**Migration target**: a single `@mixin lane-pill($lane)` in `_mixins.scss` that consumes a `--lane-*` token. Lower priority than Families A-C.

## Summary

The codebase has **one canonical chip mixin** (`m.chip`) and ~40 inlined re-implementations across four families. The cleanup is the same shape as buttons: per-file slices, not a single PR.

**Concrete proposals** for the next variant in [pills.md](./pills.md):

1. `@mixin chip-neutral` for Family B.
2. `@mixin badge-count` for Family C.
3. `@mixin lane-pill($lane)` for Family E.
4. Decide whether to ship `<app-pill tone="...">` or stay on mixins. Open question in [pills.md](./pills.md).
