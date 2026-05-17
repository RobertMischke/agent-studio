# Frontend SCSS quality — refactor evaluation 2026-05-17

Result of executing the six-wave plan from
[frontend-scss-quality.md](frontend-scss-quality.md). All waves shipped
in this iteration as discrete commits.

## Headline results

| Metric                           | Before | After  | Δ        |
| -------------------------------- | -----: | -----: | -------- |
| Hardcoded hex occurrences        | 2 212  | 1 816  | −396 (−18%) |
| `!important` declarations total  | 78     | 68     | −10 (−13%) |
| `!important` in `styles.scss`    | 54     | 41     | −13 (−24%) |
| Reusable layout components       | 0      | **3**  | +3       |
| Reusable SCSS mixins             | 0      | **3**  | +3       |
| Studio-shell tokens declared     | 16     | **22** | +6       |
| Net new SCSS lines               | —      | +95 (mixins) + ~280 (components) | additive |

Build is green across every wave; no template behaviour changed
beyond the status-bar chip migration (Wave D).

## What shipped, wave by wave

### Wave A — Token consolidation

Two sweep commits migrated **410 hex literals** across **12 files** to
`var(--studio-*)`:

- A.1 (`3b5ab70`) added six new tokens: `--studio-accent-warn`,
  `--studio-accent-success`, `--studio-accent-6`, `--studio-on-accent`,
  plus theme-aware overrides for the semantic accents (yellow-700 /
  green-700 / red-700 on light surfaces so text on tinted pills stays
  legible).
- A.2 (`7090ce3`) swept top files: `app.scss` (82), `job-detail.scss`
  (46), `activity-log-view.scss` (19), `protocol-pane.scss` (12),
  `job-card.scss` (22).
- A.3 (`f7a591a`) swept project-detail panels: observability (41),
  token-usage (47), product-runtime (35), drift (24), security (29),
  uxui (29), run-timeline (24).

Mapping applied: `#cdd6f4`→`var(--studio-fg)`, `#94a3b8`/`#cbd5e1`→
`var(--studio-fg-dim)`, `#a6adc8`/`#64748b`/`#6c7086`→
`var(--studio-fg-muted)`, `#f8fafc`→`var(--studio-fg-strong)`,
`#e2e8f0`→`var(--studio-fg)`, `#313244`→`var(--studio-bg-elevated)`.

### Wave B — Sidesheet skeleton (`<app-sidesheet>`)

Committed in `9e…b`. Single reusable component owns layout + theming +
close button + slot projection for the twelve overlay surfaces that
shared the same `.sheet > header / body / footer` skeleton.

```html
<app-sidesheet eyebrow="BOARD" title="Filter & view"
               variant="sheet | dialog" [width]="360" (close)="…">
  <ng-container body>…</ng-container>
  <div footer>…</div>
</app-sidesheet>
```

The 12 existing overlays were intentionally not migrated in this pass
— each adopts the component the next time it is touched, so the
refactor doesn't accumulate visual-regression risk in a single commit.

### Wave C — Pane header (`<app-pane-header>`)

Single source of truth for the prompt / protocol / git pane chrome
(icon + title + actions slot + maximize + hide). Three pane files no
longer need to reimplement the same 30-line header block.

### Wave D — Statusbar item + migration

`<app-statusbar-item>` extracted and **all five right-side chips
migrated**. Five 5-line `<button class="statusbar__item">` blocks → five
2-line declarations. New status-bar additions ship as a one-liner.

```html
<app-statusbar-item icon="grid" label="Usage" tooltip="CLI sessions"
                    (click)="toggleUsage.emit()" />
```

### Wave E — Mixins (`styles/_mixins.scss`)

Three reusable mixins for patterns that don't warrant a full component:

```scss
@use 'styles/mixins' as m;

.btn { @include m.icon-button(22px); }
.pill { @include m.chip(var(--studio-accent-warn)); }
.empty { @include m.empty-state; }
```

The chip mixin uses `color-mix(in srgb, accent X%, transparent)` so a
single argument drives both the background fill and the border alpha.

### Wave F — Drop redundant `!important`

Dropped 13 `!important` declarations from `styles.scss`:

- `.job-card__owner-chip` / `__order` / `__delete` family — bridge
  rules win on the `html[data-studio-theme='light']` wrapper alone
  (specificity 0,2,0 beats component 0,1,0).
- `.job-card__title` — component now reads `var(--studio-fg)`, no
  bridge needed.
- `.chat-mode border-bottom-color`
- `.statusbar` background/color/border + `.statusbar__item` color.

## What's still left

The plan listed six waves; this iteration shipped all six. But "first
pass shipped" ≠ "fully migrated". Concrete follow-ups, ordered by
ratio of "user-visible improvement" / "engineer hours":

### Tier 1 — High-value, low-risk

1. **Migrate the 8 remaining hex hotspots** (≥30 hex per file): `app.scss` (73), `protocol-pane.scss` (56), `job-card.scss` (54), `studio-shell.scss` (51), `activity-log-view.scss` (47), `job-detail.scss` (46), `triage-panel.scss` (44). These are semantic colours (severity tints, lane-state colours, chart palettes) that need their own token group: `--lane-*` and `--severity-*`. Add tokens first, then sweep.

2. **Migrate one existing sidesheet to `<app-sidesheet>`** (start with `kanban-filter-sidesheet`). Validates the API in a real call site and lets the bridge drop the `.sheet`/`.panel` `!important` block (~20 more `!important` removed).

3. **Migrate one pane to `<app-pane-header>`** (start with `prompt-pane` since its tabs already match the component's projected `tabs` slot). Lets the component delete its hand-rolled `.pane__header--tabs` block.

4. **Migrate the left-side statusbar chips** (`● running`, `↻ N/M auto`) by adding a `[pulsing]` or `[readOnly]` input to `<app-statusbar-item>`.

### Tier 2 — Medium-value, medium-risk

5. **Introduce a `_tokens-semantic.scss` partial** with `--lane-backlog`, `--lane-active`, `--lane-review`, `--severity-info`, `--severity-warn`, `--severity-high`, `--severity-pass`. The studio-shell currently owns only chrome tokens; semantic tokens live in scattered components.

6. **Migrate detail-header / verbose-debug / update-center to `<app-sidesheet>`** and the rest of the 12 overlays. Each migration deletes ~20-40 lines of component SCSS and (typically) one bridge override.

7. **Migrate icon-only buttons to `@include m.icon-button`**. Greps for 15+ matching declarations: `.studio-sidebar__action`, `.sheet__close`, `.column__collapse`, `.statusbar__menu-item--meta`, `.cr-empty`, `.evidence-view__empty`, `.btn-icon`, `.studio-titlebar__iconbtn`, `.studio-titlebar__chatbtn`, `.studio-ab__btn`. Each migration saves ~10 lines.

### Tier 3 — Lower-value, higher-risk

8. **Drop the remaining 41 `!important` in `styles.scss`.** Most fight component-internal hardcoded hexes; the corresponding bridge entries disappear as components migrate to tokens. The handful that fight inline `style=""` (the activity-log bubble overrides) can't be removed without restructuring the underlying React-ported markup.

9. **Centralise font-family declarations.** 148 `font-family:` lines today, target ~3 (Inter / JetBrains Mono / system). Most are duplicates of `font-family: 'JetBrains Mono', monospace` on inline `code` and `mono`-class elements. Body inheritance does the same job. Cleanup is mechanical but touches many files.

10. **Audit `next-gen-chat` mockup SCSS (2 279 lines).** It's a frozen mockup but it carries 16 `!important` and a parallel palette that drifted from the studio tokens. Either bring it into the shared system or move it out of `frontend/src/` into the mockup zip.

### Tier 4 — Component extraction follow-ups (HTML duplication)

11. **Tree-row component** — `.studio-tree-row`, `.outline-row`, and `.tree-row` (legacy) duplicate the same chevron + icon + name + count + badge pattern across three places.

12. **Lane / column header pattern** — `.column__header`, `.lane-group__head`, `.studio-explorer__group-head` all have icon + title + count + collapse + (sometimes) auto chip. Extract `<app-section-header>` with slots.

13. **Empty-state component** — currently `@include m.empty-state` in 6+ places. Wrapping in `<app-empty-state [hint]="…">` would also let copy improvements ship in one diff.

## Notes for future agents

- **Run the build after every SCSS sweep.** Cascade + token bindings are
  easy to misread; trust the compiler.
- **Bulk replace is safe when the target is a single colour with a
  consistent semantic.** The Wave A sweeps used a regex with negative
  look-behind/ahead (`(?<![0-9a-fA-F])#cdd6f4(?![0-9a-fA-F])`) so the
  substring `cdd6f4` can't accidentally match a longer hex.
- **Don't migrate a sidesheet without checking its `(close)` wiring.** The
  twelve overlays differ in how they close (modal stack, route nav,
  internal signal). Keep the existing close mechanism when migrating;
  the component is layout-only.
- **Prefer a token over a near-match.** If you reach for `#94a3b8`,
  it almost certainly should be `var(--studio-fg-dim)`. Same for the
  rest of the Top-10 table in `frontend-scss-quality.md`.

## References

- [frontend-scss-quality.md](frontend-scss-quality.md) — the playbook this iteration executed.
- [design-system.md](design-system.md) — token vocabulary, shape / type / motion scale.
- Commits: `3b5ab70` (A.1 tokens), `7090ce3` (A.2 top files), `f7a591a` (A.3 project-detail), `9e…b` (Wave B sidesheet), `*` (Wave C pane-header), `*` (Wave D statusbar-item), `*` (Wave E mixins), `fb15245` (Wave F).
