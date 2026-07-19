# Frontend SCSS quality — refactor evaluation 2026-05-17

## Update — Tier 1.3 batch adoption (2026-05-18, late)

Fifth iteration. Real sidesheet + dialog migrations after the
sidesheet/dialog split landed.

### Sidesheet adoptions (3 of 5)

| Component                     | Status     | Notes                                              |
| ----------------------------- | ---------- | -------------------------------------------------- |
| kanban-filter-sidesheet       | ✅ migrated | Outer slide animation kept; chrome owned by skeleton |
| cli-usage-sheet               | ✅ migrated | Added `subtitle` input to <app-sidesheet>            |
| orchestrator-side-sheet       | ✅ migrated | Added `[header-actions]` slot for project picker + buttons |
| workspace-screenshots         | ⏸ skipped  | Rendered inside workspace-overlays as a modal panel, not a self-contained sidesheet |
| update-center                 | ⏸ skipped  | Flush-right sidesheet WITH a backdrop — needs a `[backdrop]` input or its own component |

### Dialog adoptions (3 of 8)

| Component                     | Status     | Notes                                              |
| ----------------------------- | ---------- | -------------------------------------------------- |
| error-dialog                  | ✅ migrated | First adopter, drove the dialog/sheet split        |
| confirm-dialog                | ✅ migrated | Two action buttons in `[footer]` slot              |
| e2e-cleanup-dialog            | ✅ migrated | Per-phase footer (Retry / Delete / Close)          |
| create-job-dialog             | ⏸ skipped  | Form panel with drag/paste handlers on the panel itself; carries its own structure |
| update-block-modal            | ⏸ skipped  | Busy indicator, non-closable, no header chrome — different shape from a dialog |
| media-lightbox                | ⏸ skipped  | Image-only viewer, no body/footer chrome           |
| verbose-debug-overlay         | ⏸ skipped  | Reviewed; sizing + scroll body shape diverges      |
| orchestrator-settings-modal   | ⏸ skipped  | Rail + panel split layout — would need a horizontal-split variant of <app-dialog> |

### `<app-sidesheet>` API additions

- `[subtitle]` — optional caption under the title.
- `[header-actions]` content projection slot — accepts callers'
  project pickers + secondary buttons; renders between title and
  close button.

### `<app-dialog>` API additions

- `[subtitle]` — optional caption under the title.

### Cumulative refactor stats (since iteration 1)

  Hardcoded hex occurrences   : 2 212 → 1 664  (−25 %)
  !important total            : 78    → 33     (−58 %)
  !important in styles.scss   : 54    → 2      (−96 %)
  font-family via tokens      : 0     → 92     (62 %)
  Studio-shell tokens         : 16    → 36     (+20)
  Reusable layout components  : 0     → 7      (+7)
  SCSS mixins                 : 0     → 3      (+3)
  Existing call-sites migrated to extracted skeletons:
    <app-section-header>   2
    <app-tree-row>         6
    <app-empty-state>      5
    <app-sidesheet>        3
    <app-dialog>           3

### What's left (carry-over)

- The 5 deferred sidesheets/dialogs above. Each needs either a
  scoped <app-sidesheet>/<app-dialog> input (backdrop / rail / drag
  handlers) or stays with its own component because its shape is
  genuinely different.
- Long-tail hex literal cleanup in mockup zones
  (mockups/next-gen-chat/) — not shipped in the studio layout.

---

## Update — Tier 4 adoption + <app-dialog> extraction (2026-05-18 evening)

Fourth iteration. The new structural extractions from the previous
pass now adopt their first real call sites, and a missing piece
surfaces: the sidesheet/dialog split.

### What shipped in this pass

- **Tier 4 — `<app-section-header>` adopted in 2 studio-shell call
  sites.** Workspace group head + Open Tabs group head. The
  workspace head is now `[interactive]` so clicking it scopes the
  board to "All projects" (mirrors the titlebar picker).

- **Tier 4 — `<app-tree-row>` adopted in 6 studio-shell call sites.**
  Every project row + its five lane children (backlog / active /
  human review / Project Hub / archive) now render via
  `<app-tree-row>`. Each row went from ~9 lines to ~4. The hub-link
  button slots into the row via projection. The `(chevronClick) /
  (select) / (secondary)` outputs preserve the existing click +
  double-click semantics.

- **Tier 4 — `<app-empty-state>` adopted in 5 studio-shell call
  sites.** All single-line "No projects loaded" / "No jobs loaded
  yet" / etc. blocks migrated.

- **Sidesheet/dialog split.** The user flagged that the original
  `<app-sidesheet>` was conflating two distinct UI shapes:

    Side panel (sheet)         | Modal dialog
    ---------------------------+-----------------------------------
    Pinned to viewport edge    | Centred, backdrop-overlaid
    role="region"              | role="alertdialog"
    Persistent / toggleable    | One decision, then close
    No backdrop click-to-close | Backdrop click-to-close
    No focus trap              | Focus trap
    kanban-filter / cli-usage  | error / confirm / create-job /
                               | media-lightbox / verbose-debug

  Fix: `<app-sidesheet>` narrowed to side-panel-only (the
  `variant="dialog"` option dropped, role flipped from `dialog` to
  `region`); a new `<app-dialog>` skeleton owns the modal shape.

- **`<app-dialog>` extracted** as the companion component:
    - Centred panel with backdrop overlay
    - `[role]="dialog | alertdialog"`
    - eyebrow + title + close header
    - body via default `<ng-content>`, footer via `<ng-content
      select="[footer]">`
    - `kind: default | danger | primary` drives a top accent stripe
    - `(close)` + `(backdropClick)` outputs let callers keep custom
      cancellation semantics

- **Two migrations validate the new skeleton:**
    - `error-dialog` — uses `<app-dialog kind="danger">`. The custom
      .overlay + .error-dialog__header + .error-dialog__close all
      go away; the inner sections (source / message / actions /
      output / stack-trace) project into the body slot.
    - `confirm-dialog` — uses `<app-dialog kind="danger | primary">`
      driven by the live dialog state. The two action buttons
      project into the `[footer]` slot. The keydown handler stays
      on the host. Template went from 50 → 22 lines.

### Component inventory grew to 7 Quality-Pass extractions

Under `src/app/components/`:
  app-dialog/ (host wrapper)   chat/               concept-help/
  dialog/                  ⭐  empty-state/    ⭐  error-dialog/
  info-button/                 media-lightbox/     pane-header/   ⭐
  section-header/          ⭐  sidesheet/      ⭐  studio-icon/   ⭐
  tooltip/                     tree-row/       ⭐

  ⭐ = SCSS-quality-pass extraction (now seven: dialog +
       empty-state + pane-header + section-header + sidesheet +
       studio-icon + tree-row).

### What remains (lower priority, deferred)

- **Real `<app-sidesheet>` adoption.** The five existing sidesheets
  (kanban-filter, cli-usage, orchestrator-side-sheet,
  workspace-screenshots, update-center) carry their own slide
  animation + open-state binding. Migration safely happens one PR
  per sheet; the component is ready.

- **Remaining `<app-dialog>` adoptions** for: create-job,
  e2e-cleanup, update-block, media-lightbox, verbose-debug,
  orchestrator-settings. Mechanical — each follows the
  error-dialog / confirm-dialog pattern.

---

## Update — Tier 2 + 4 follow-up pass (2026-05-18)

Third iteration: the remaining structural extractions and the
icon-button mixin sweep.

| Metric                            | Initial | After Wave A-F | After Tier 1-3 | After Tier 2+4 | Δ total |
| --------------------------------- | ------: | -------------: | -------------: | -------------: | ------- |
| Hardcoded hex occurrences         | 2 212   | 1 816          | 1 664          | **1 664**      | **−548 (−25 %)** |
| `!important` declarations total   | 78      | 68             | 33             | **33**         | **−45 (−58 %)** |
| `!important` in `styles.scss`     | 54      | 41             | 2              | **2**          | **−52 (−96 %)** |
| Reusable layout components        | 0       | 3              | 3              | **6**          | +6      |
| Reusable SCSS mixins              | 0       | 3              | 3              | **3**          | +3      |
| Studio-shell tokens               | 16      | 22             | 36             | **36**         | +20     |

### What shipped in this pass

- **Tier 2 — Icon-button mixin adopted in 3 components.**
  `.pane-header__btn` (24 px), `.sidesheet__close` (26 px), and
  `.studio-sidebar__action` (22 px) all swapped 17–18 lines of
  hand-rolled chrome for one `@include m.icon-button(<size>)` call.
  Net SCSS saved in this commit: ~46 lines.

- **Tier 4a — `<app-empty-state>`.** Padded muted text for "nothing
  to show" panels. Two shapes: headline + body via `[icon] [title]
  [body]` inputs, or single-line via content projection. A
  `[compact]` variant tightens padding for dense lists. Five
  `<div class="studio-empty">` blocks in `studio-shell.component.html`
  migrated as the first adopter.

- **Tier 4b — `<app-tree-row>`.** The Explorer workspace tree, the
  Tasks outline, and the legacy `.tree-row` all repeated the same
  chevron + glyph + name + meta + count layout. The component owns
  it with two levels (`root` 8 px / `child` 44 px padding) and an
  optional SVG icon vs initial-character glyph. ViewEncapsulation.None
  + the legacy BEM class names so the existing styles.scss bridge
  still applies during incremental migration.

- **Tier 4c — `<app-section-header>`.** Uppercase title bar used by
  kanban columns, sidebar groups, lane groups, and project-hub rails.
  Inputs cover icon (SVG or single char), title, count, active state;
  an `actions` slot projects trailing chips/buttons. An `[interactive]`
  flag flips between `<header>` and `<button>` rendering for the
  cases (Workspace group head, lane filter chip) that need to be
  clickable.

### Final component inventory (src/app/components/)

  app-dialog/        chat/        concept-help/
  empty-state/       error-dialog/info-button/
  media-lightbox/    pane-header/ section-header/
  sidesheet/         studio-icon/ tooltip/
  tree-row/          markdown-rich-editor*

  (20 total — 6 of those are SCSS-quality-pass extractions.)

### Final !important location count

`styles.scss` has exactly **2** `!important` declarations remaining:

  background: rgba(0, 0, 0, 0.03) !important;  // beats inline style="background:rgba(15,23,42,…)"
  color: #1a1a1a !important;                    // beats inline style="color:#cdd6f4"

Both fight inline `style=""` attributes (specificity 1,0,0,0) inside
the activity-log bubble markup — the only way around them is to
remove the inline-style React-ported markup, which is a much larger
change than the value of those two lines.

### What still remains (lower priority, deferred)

- **Tier 1.3 — Migrate the 12 existing sidesheet/dialog overlays to
  `<app-sidesheet>`.** Each carries its own focus-trap + animation
  + route binding; safest as one PR per overlay. Component is ready.

- **Adoption of `<app-tree-row>` and `<app-section-header>` in their
  existing call sites.** Mechanical template edits. Skipped here to
  avoid blocking the metric-improvement commits behind 6+ template
  rewrites; the next pass picks them up.

- **Long-tail hex literal cleanup.** 1 664 hex occurrences remain.
  About 700 live in `styles.scss` (the light-theme bridge — these
  describe *the light values*, can't be tokenised because the bridge
  is what *defines* the tokens). Another ~400 live in mockup zones
  that aren't shipped (`mockups/next-gen-chat/`). The realistic
  floor is ~600-700 in active code.

---

## Update — Tier 1+2+3 follow-up pass (same day)

A second iteration drove the metrics further. Updated table:

| Metric                              | Initial | After Wave A-F | After Tier 1-3 | Δ total |
| ----------------------------------- | ------: | -------------: | -------------: | ------- |
| Hardcoded hex occurrences           | 2 212   | 1 816          | **1 664**      | **−548 (−25 %)** |
| `!important` declarations total     | 78      | 68             | **33**         | **−45 (−58 %)** |
| `!important` declarations in `styles.scss` | 54 | 41          | **2** (load-bearing) | **−52 (−96 %)** |
| `font-family:` declarations using tokens | 0  | 0              | **92** (of 149) | **+92** |
| Studio-shell tokens                 | 16      | 22             | **36**         | **+20** |
| Reusable layout components          | 0       | 3              | 3              | +3      |
| Reusable SCSS mixins                | 0       | 3              | 3              | +3      |

### What shipped in the follow-up pass

- **Tier 1.1 — Semantic + lane tokens.** Added 14 new tokens to
  `studio-shell.component.scss`: `--severity-pass / -warn / -high /
  -info / -pending` (with darker light-theme overrides for tinted
  pills) and a nine-state lane palette (`--lane-backlog / -prep /
  -ready / -progress / -auto-review / -human-review / -completed /
  -archive / -failed`). Brings the studio-shell token vocabulary to
  36 named variables.
- **Tier 1.2 — Hex sweep across 18 more component SCSS files.** 158
  severity / state / grey literals migrated to `var(--severity-*)` /
  `var(--lane-*)` / `var(--studio-fg-*)`. Cumulative Wave A + Tier 1
  reach: **568 hex literals migrated**.
- **Tier 1.4 — Prompt pane adopts `<app-pane-header>`.** First real
  call-site for the shared component. The maximize / hide buttons
  + spacer logic move into the shared component; the prompt pane
  only projects its three-tab strip into the header's `tabs` slot.
- **Tier 1.5 — Status-bar left chips adopt `<app-statusbar-item>`.**
  Added `[pulsing]` and `[bullet]` inputs so the legacy "● running"
  pulse chip lands as a one-liner. All seven status-bar chips
  (two read-only + five clickable) now go through one component.
- **Tier 3.1 — Drop 20 more redundant `!important` from
  `styles.scss`.** The light-theme bridge wrapping at
  `html[data-studio-theme='light']` gives selectors specificity
  (0,2,0), which already beats every component-side (0,1,0)
  declaration. The `!important` on `.sheet`, `.panel`,
  `.proj-shell__*`, `.pd-*`, `.tup__*`, `.drift-card`,
  `observability-panel`, `security-panel`, `uxui-panel`,
  `runtime-panel`, `project-shell` etc. became dead weight after
  Wave A's token migration. Result: 54 → 2 in `styles.scss`. The two
  remaining declarations fight inline `style=""` attributes
  (specificity 1,0,0,0) — load-bearing and cannot be removed.
- **Tier 3.2 — Font-family centralisation.** Added `--font-ui` and
  `--font-mono` token stacks to the studio-shell `:root`. Bulk
  sweep migrated 49 `font-family:` declarations across 19 files to
  read the tokens. **62 % of all `font-family` declarations now
  resolve through a CSS variable.** Adding a new typeface needs a
  one-line edit to `studio-shell.component.scss`.

### Deferred to next pass

Two Wave items are tracked but unimplemented in this iteration:

- **Tier 1.3 — Existing sidesheets adopting `<app-sidesheet>`.** The
  twelve overlays differ in animation, focus-trap, and route
  binding. A single bulk migration is too risky; each migration
  should ship in its own slice. The component is **ready**, the
  existing call sites stay until next touch.
- **Tier 2 — Icon-button mixin sweep.** `@include m.icon-button` is
  mechanically applied — 15+ call sites identified. The mixin is
  **ready**; call sites adopt when next touched.
- **Tier 4 — Empty-state, tree-row, section-header extractions.**
  Lower priority; flagged for the next pass.

---



Result of executing the six-wave plan from
[frontend-scss-quality.md](scss-quality.md). All waves shipped
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

- [frontend-scss-quality.md](scss-quality.md) — the playbook this iteration executed.
- [design-system.md](../design-system.md) — token vocabulary, shape / type / motion scale.
- Commits: `3b5ab70` (A.1 tokens), `7090ce3` (A.2 top files), `f7a591a` (A.3 project-detail), `9e…b` (Wave B sidesheet), `*` (Wave C pane-header), `*` (Wave D statusbar-item), `*` (Wave E mixins), `fb15245` (Wave F).
