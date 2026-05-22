# Frontend SCSS quality — guidelines, audit, refactor plan

A working playbook every agent (and human) should consult before touching SCSS in this repo. It pairs three things:

1. **Concrete state** — the SCSS surface as it stands today, measured.
2. **Guidelines** — the rules new code must follow (now machine-enforced).
3. **Refactor plan** — the next steps that close the gap between today and the guidelines.

Skim section 1 to understand why the rules in section 2 exist. Use section 3 as the ordered backlog when a slice has the budget for cleanup.

Related docs: [design-system.md](design-system.md) carries the visual contract (token vocabulary, shape scale, type scale, component inventory); this doc is its operational counterpart for SCSS authoring.

## 0. Two-tier token system (TL;DR)

The colour + shadow palette lives in two files; **never reach past them from a component**:

| Tier | File                                           | Purpose                                                                                              |
| ---- | ---------------------------------------------- | ---------------------------------------------------------------------------------------------------- |
| 1    | `frontend/src/styles/_tokens-primitives.scss`  | Raw palette + 5-step shadow scale. `--color-orange-500`, `--shadow-200`. No semantic meaning.        |
| 2    | `frontend/src/styles/_tokens-semantic.scss`    | Purpose aliases. `--studio-accent`, `--elevation-card`, `--diff-add-bg`. Reads Tier 1 via `var()`.   |

The light theme rewrites Tier 2 only (`[data-studio-theme='light']` block at the bottom of `_tokens-semantic.scss`). Tier 1 is frozen. This is the standard industry pattern (W3C Design Tokens spec, IBM Carbon, Adobe Spectrum, Material 3, Tailwind themes).

Both files are loaded once globally via `src/styles.scss`. Components do **not** redeclare tokens, do **not** read Tier 1 directly, and do **not** introduce private hex literals — stylelint blocks all three.

**Adding a new token**:
1. Confirm an existing Tier 2 alias really doesn't fit.
2. Confirm the primitive you need is in Tier 1 (add it there first if missing).
3. Declare the alias in `_tokens-semantic.scss` in BOTH the `:root` and `[data-studio-theme='light']` blocks if it differs by theme.
4. Consume from the component via `var(--<your-alias>)`. Never go back to raw hex.

**Elevations** (drop shadows) use the same pattern:
- Tier 1: `--shadow-100` (resting card) → `--shadow-500` (floating modal).
- Tier 2: `--elevation-card`, `--elevation-popover`, `--elevation-dropdown`, `--elevation-modal`, `--elevation-tooltip`, `--elevation-floating`.

A component with a drop shadow reads `box-shadow: var(--elevation-modal)`, never an inline `0 24px 64px rgba(...)`.

### Machine-enforced (`npm run lint:scss`)

The contract is checked by stylelint with the `stylelint-declaration-strict-value` plugin. The rule:

- `color-no-hex`: ERROR — no `#rgb`, `#rrggbb`, `#rgba`, `#rrggbbaa` literals.
- `color-named`: ERROR — no `white`, `black`, `red`, etc.
- `scale-unlimited/declaration-strict-value` (color, background, border-color, fill, stroke): ERROR — value must be `var(...)`, `transparent`, `currentColor`, `color-mix(...)`, a gradient, `none`, or `0`.

Three carve-outs:

1. `_tokens-primitives.scss` and `_tokens-semantic.scss` — disabled (they declare the tokens themselves).
2. `_markdown-body.scss` — disabled (legacy `--md-*` palette inline; pending migration to Tier 2).
3. **Legacy file list** in `.stylelintrc.json` — `severity: warning` while migration is in progress. New SCSS files never appear here. As a file is migrated, remove its entry — the strict ERROR severity kicks in automatically.

The mockup tree (`src/mockups/**`) is exempt entirely; it's design scratch space, not production.

**Box-shadow** is not in the rule's property list (multi-part shadows like `0 1px 2px var(--c)` are awkward for the per-token parser). It is policed by convention: any component shadow must be `var(--elevation-*)`. PR review enforces this until a custom rule covers it.

CI runs `npm run lint:scss` as a blocking step ([.github/workflows/lint.yml](../.github/workflows/lint.yml)). A pull request that introduces a hex literal in a non-exempt component goes red.

## 1. Current state (measured)

Numbers taken from `frontend/src/**/*.scss` at the time of writing.

| Metric                          | Value      |
| ------------------------------- | ---------- |
| SCSS files                      | 88         |
| Total SCSS lines                | ~24 k      |
| Hardcoded hex literals          | **2 212**  |
| Catppuccin-palette occurrences  | 522        |
| `font-family:` declarations     | 148        |
| `!important` declarations       | 78 total (54 in `styles.scss`) |
| Files with CSS custom properties | 7 (out of 88) |

### Top-10 hardcoded colors

| Count | Hex      | Catppuccin name (or role)           |
| ----- | -------- | ----------------------------------- |
| 160   | `#cdd6f4` | mocha text                          |
| 149   | `#94a3b8` | tailwind slate-400                  |
| 131   | `#cbd5e1` | tailwind slate-300                  |
| 106   | `#1a1a1a` | brand near-black                    |
| 102   | `#f8fafc` | tailwind slate-50                   |
| 102   | `#e2e8f0` | tailwind slate-200                  |
| 65    | `#313244` | mocha surface0                      |
| 62    | `#a6adc8` | mocha subtext0                      |
| 60    | `#64748b` | tailwind slate-500                  |
| 56    | `#f9e2af` | mocha yellow                        |

These 10 colours alone appear **993 times**. They should resolve to ~6 design tokens.

### Top-10 SCSS files by hex literals

| Count | File                                                                                  |
| ----- | ------------------------------------------------------------------------------------- |
| 194   | `styles.scss` (light-theme bridge — `!important` reset zone, by design)               |
| 155   | `app/app.scss`                                                                        |
| 92    | `app/features/job-detail/job-detail.scss`                                              |
| 76    | `app/features/board/components/job-card/job-card.component.scss`                       |
| 75    | `app/features/project-detail/components/project-observability/project-observability-panel.component.scss` |
| 73    | `app/features/project-token-usage/components/project-token-usage-panel.component.scss` |
| 68    | `app/features/job-detail/components/protocol-pane/protocol-pane.component.scss`        |
| 66    | `app/features/job-detail/components/activity-log-view.scss`                            |
| 61    | `app/features/project-detail/components/project-product-runtime/project-product-runtime-panel.component.scss` |
| 60    | `app/features/project-detail/components/project-drift-overview-section.scss`           |

### Existing token namespaces (good baseline to build on)

- `--color-*`, `--shadow-*`, `--alpha-*` — **Tier 1 primitives**. Declared in `styles/_tokens-primitives.scss`. Components never read these directly.
- `--studio-*`, `--severity-*`, `--lane-*`, `--diff-*`, `--elevation-*` — **Tier 2 semantic aliases**. Declared in `styles/_tokens-semantic.scss`. This is what components consume.
- `--md-*` — markdown body palette. Declared in `styles/_markdown-body.scss` (legacy; pending Tier-2 migration).
- `--column-bg`, `--card-bg`, `--surface*`, `--border*`, `--text-*`, `--bg-page`, `--header-bg` — flat design tokens. Declared in `styles.scss` light theme bridge (legacy bridge zone).
- `--accent`, `--bg`, `--danger`, `--line`, `--line-strong` — partial namespace, scattered (legacy).
- `--header-btn-*` — local naming on header buttons (legacy).

The proliferation is the root cause: every refactor in the past introduced its own prefix instead of consolidating. Tier 1 + Tier 2 is the consolidation. New tokens land there exclusively.

### HTML duplication hotspots (observed)

The user explicitly asked for "wo Komponenten extrahiert werden können". Six surfaces have **near-identical HTML structure across N call sites** that should consolidate:

1. **Sidesheet skeleton** — `app-orchestrator-side-sheet`, `app-cli-usage-sheet`, `app-kanban-filter-sidesheet`, `app-create-job-dialog`, `app-orchestrator-settings-modal`, `app-e2e-cleanup-dialog`, `app-update-block-modal`, `app-update-center`, `app-confirm-dialog`, `app-error-dialog`, `app-media-lightbox`, `app-verbose-debug-overlay` all repeat `.sheet > .sheet__header (title + close) + .sheet__body + .sheet__footer`. Class names diverge but the layout is identical.
2. **Pane header** — `pane--prompt`, `pane--protocol`, `pane--git` each declare `.pane__header` + maximize + hide. The prompt pane now also carries a tab strip.
3. **Status-bar items** — every chip in `app-status-bar` is `<button class="statusbar__item statusbar__item--btn"><icon><label></button>`. Six instances.
4. **Lane / column header** — `.column__header` carries icon + title + count + collapse + (now) auto chip. Repeated visually by `.lane-group__head` (group level) and `.studio-explorer__group-head` (sidebar).
5. **Tree row** — `.studio-tree-row` (sidebar) and `.outline-row` (tasks panel) and `.tree-row` (legacy) duplicate the same chevron + icon + name + count + badge pattern.
6. **Empty state** — `.studio-empty`, `.sheet__empty`, `.evidence-view__empty`, `.cr-empty`, `.tree-row` empty all repeat "padded text, muted color, ~12 px font, italic".

### SCSS duplication hotspots (observed)

- **Mode-pill rows** (4+ implementations: `.chat-mode__pill`, `.studio-pill`, `.studio-titlebar__crumb`, `.flt-chip`, `.cr-row-toggle`). All small pills with optional active state.
- **Status badges** (e.g. `.cr-verdict-pass`, `.ev-section-head .ev-status.pass`, `.project-hygiene-badge--dirty`, `.triage__menu-blocked`) — same semantic-coloured chip pattern.
- **Icon-only buttons** — `.statusbar__icon`, `.studio-tab-action--icon`, `.studio-sidebar__action`, `.sheet__close`, `.column__collapse`, `.triage__menu-item`, `.column__archive-all` all share the same hover/disabled/cursor recipe.
- **Resizable splitter** — `.pane__splitter`, `.studio-sidebar__resize`, `.det-resize` (legacy from reference).
- **`color-mix(in srgb, currentColor X%, transparent)`** — only `conversation-view.scss` uses it. The pattern is repeat-worthy.

## 2. Guidelines (the rules new SCSS must follow)

### Rule 1 — Tokens for colour, font, size

> **Never hardcode a colour, font, or size value. Read a CSS custom property; if no token fits, propose one (this doc + design-system.md) before adding the constant.**

Tokens come from a single namespace per category. The studio shell vocabulary is canonical:

- Surface: `--studio-bg-titlebar`, `--studio-bg-activitybar`, `--studio-bg-sidebar`, `--studio-bg-editor`, `--studio-bg-elevated`, `--studio-bg-tab-active`, `--studio-bg-tab-inactive`, `--studio-bg-hover`, `--studio-bg-selected`.
- Border: `--studio-border`, `--studio-border-strong`.
- Foreground: `--studio-fg`, `--studio-fg-strong`, `--studio-fg-dim`, `--studio-fg-muted`.
- Accent: `--studio-accent`, `--studio-accent-2`, `--studio-accent-3`, `--studio-accent-4`, `--studio-accent-warn`, `--studio-accent-success`.
- Size: `--studio-titlebar-h`, `--studio-tabbar-h`, `--studio-activitybar-w`.

Type vocabulary is from `design-system.md` (Inter for UI, JetBrains Mono for code). Hardcoded `font-family:` declarations are allowed only at the global body level — everywhere else: `font-family: inherit` or `font: inherit`. **Today: 148 declarations; target: ~3.**

Severity colours (success/warn/error/info) live in tokens `--studio-accent-success`, `--studio-accent-warn`, `--studio-accent-6`, `--studio-accent-3`. If the value is needed at low alpha, derive with `color-mix(in srgb, var(--studio-accent-warn) 12%, transparent)` rather than a hardcoded `rgba()`.

#### Diff tokens

Diff-line backgrounds and foregrounds are token-bound. Both the
`run-git-viewer` modal (protocol pane) and the studio Diff tab read
the same tokens so add/remove lines share one palette per theme and
WCAG AA contrast is enforced in `_tokens-semantic.scss` instead of
per-file.

| Token            | Dark default      | Light override (`[data-studio-theme='light']`) | Use                                              |
| ---------------- | ----------------- | ---------------------------------------------- | ------------------------------------------------ |
| `--diff-add-bg`  | `rgba(34,197,94,0.18)` | `#dcfce7`  (green-100)                    | Hinzugefügte Zeile, Hintergrund.                 |
| `--diff-add-fg`  | `#bbf7d0`         | `#14532d`  (green-900)                         | Hinzugefügte Zeile, Text.                        |
| `--diff-rem-bg`  | `rgba(220,38,38,0.20)` | `#fee2e2`  (red-100)                      | Entfernte Zeile, Hintergrund.                    |
| `--diff-rem-fg`  | `#fecaca`         | `#7f1d1d`  (red-900)                           | Entfernte Zeile, Text.                           |
| `--diff-hunk-bg` | `rgba(56,189,248,0.08)` | `#e0f2fe`  (sky-100)                     | `@@ hunk @@`-Header-Hintergrund.                 |
| `--diff-hunk-fg` | `#93c5fd`         | `#075985`  (sky-800)                           | `@@ hunk @@`-Header-Text.                        |

**Rule.** When you add diff-rendering CSS (status pills, side-by-side
columns, inline diff renderers), reach for these six tokens. Don't
add a private `rgba(34, 197, 94, …)` literal in a component SCSS;
that's the pattern the F18 incident (2026-05-22) was filed against -
the light-theme path looked broken because every diff renderer had
its own hand-tuned palette. Token declarations live in
`_tokens-semantic.scss`; first consumers are
`run-git-viewer.component.scss` and `diff-tab-view.component.scss`.

### Rule 1a — No hex / rgb literal outside the token block

A stricter restatement of Rule 1, called out separately because it is
the rule new SCSS most often breaks:

> Component SCSS may contain **zero** hex colours and **zero** `rgb()` /
> `rgba()` literals. The only allowed colour expressions are
> `var(--studio-*)`, `var(--diff-*)`, `var(--severity-*)`,
> `var(--lane-*)`, `var(--elevation-*)`, and
> `color-mix(in srgb, var(--…) X%, transparent)`.

Enforced by `npm run lint:scss` (`color-no-hex` + `color-named` +
`scale-unlimited/declaration-strict-value`). The pre-commit and CI
gate exits non-zero on any new hex outside the carve-outs.

Exceptions (each one is allowlisted in `.stylelintrc.json`):

- `_tokens-primitives.scss` and `_tokens-semantic.scss` declare the
  tokens themselves; raw hex / rgba lives there and nowhere else.
- `_markdown-body.scss` carries the legacy `--md-*` palette pending
  Tier-2 migration.
- A small **legacy file list** under `overrides[].files` runs the rule
  at `severity: warning`. New SCSS files never land in that list -
  they conform from day one.

Diff-specific palette (the F18 case): every diff renderer must read
`--diff-add-bg` / `--diff-add-fg` / `--diff-rem-bg` / `--diff-rem-fg`
/ `--diff-hunk-bg` / `--diff-hunk-fg` from `_tokens-semantic.scss`.
Adding a private green/red literal re-opens the WCAG-AA regression
the tokens were created to fix.

### Rule 1b — When is a component-local token override justified?

**Answer: never.** If a component needs a value that doesn't fit any
existing Tier-2 alias, the right move is to add a new alias to
`_tokens-semantic.scss` (and the primitive it points at in
`_tokens-primitives.scss` if needed), not to invent a one-off
`--some-thing` inside the component.

If a slice of the codebase legitimately needs a *family* of extension
tokens that don't fit the standard semantic vocabulary (e.g. a
specific marketing or onboarding section), create
`src/styles/_tokens-overrides.scss` with its own `:root` block,
declare the extension tokens there, and `@use` it from `styles.scss`.
This keeps every token-defining file visible in one directory instead
of scattered across components, which is the failure mode the
two-tier system exists to prevent.

### Rule 2 — Three-layer SCSS strategy

A new style rule belongs in exactly one of these layers. Pick before you write.

| Layer                                              | Purpose                                                                                              | When                                                                                                            |
| -------------------------------------------------- | ---------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------- |
| **Global token + bridge** (`src/styles.scss`)      | Declare `--studio-*` palette flips, theme overrides, body resets.                                    | Only for cross-cutting concerns — a hex needed in ≥ 3 components, or a theme override no component can own.     |
| **Shared partials** (`src/styles/_*.scss`)         | Reusable typography blocks (the `markdown-body` lives here). Atomic mixins (e.g. `@mixin icon-button`).  | When 3+ components share a pattern verbatim. Each partial is `@use`d from `styles.scss`.                        |
| **Component SCSS** (`*.component.scss`)            | All styling that is **truly local** to that component.                                               | The default — > 95% of new SCSS should land here.                                                                |

The mistake to avoid: dumping component-local rules into `styles.scss` because "it's faster". The light-theme bridge already has 54 `!important` declarations partly because rules leaked out of components. **Component SCSS should not require `!important` to work.**

### Rule 3 — `!important` is a tool, not a habit

- Allowed in `styles.scss` light-theme bridge **only** for selectors fighting hardcoded dark-only literals inside legacy components.
- Forbidden in component SCSS. If your component-local rule needs `!important`, the rule is wrong or there's a cascade collision worth fixing at the source.
- Removing `!important` from `styles.scss` is a recurring cleanup goal — every time a component migrates to tokens, the corresponding bridge rule should disappear.

### Rule 4 — Theme overrides scope to a wrapper, never bare selectors

The light bridge in `styles.scss` is wrapped in `html[data-studio-theme='light'] { … }`. Any new theme override must live inside that wrapper. Bare `.sheet { color: #1a1a1a }` at module top-level applies in dark mode too and **will silently regress dark**.

### Rule 5 — Specificity is part of the contract

The recent indent fix taught us that `.studio button { padding: 0 }` (0,1,1) beats `.studio-tree-row--child` (0,1,0). When you write a BEM modifier expecting it to win against a generic `.app element {}` reset, you must either:

- raise the modifier's specificity (`.studio .studio-tree-row--child`)
- OR scope the reset narrower (`.studio > button` instead of `.studio button`)
- OR explicitly note the trade-off in a comment so the next refactor can catch it.

### Rule 6 — Selectors named for their purpose, not their look

Examples of selectors to favour vs avoid:

- ✓ `.evidence-card--high` — semantic (high-severity card).
- ✗ `.evidence-card--red` — look-coupled (becomes wrong when red ≠ severity).

This applies double to colour names. `--accent-orange` is brittle; `--studio-accent` survives a brand re-tone.

### Rule 7 — Co-locate component styles by file

One `*.component.scss` per component, declared on the component's `styleUrl`. No cross-component imports of component SCSS. If you need to share, that's the trigger to lift the rule into a `styles/_partial.scss` and consume from both.

### Rule 8 — Light theme is the daily driver

Every new component must be visually verified in light mode before merge. Dark must not regress. The flow that catches regressions: launch the dev server, open the component, toggle theme in the titlebar, screenshot both. The Playwright snap scripts under `frontend/.snap/` capture this if the slice is significant.

## 3. Refactor plan

The current state has technical debt; the rules in section 2 prevent new debt. This section is the migration path to the rules.

Six waves, ordered by ratio of "user-visible improvement" / "engineer hours". Each is shippable on its own.

### Wave A — Top-10 colour token consolidation (highest ratio)

Goal: collapse the ~1 400 remaining occurrences of the top-10 hex literals into the existing Tier-2 aliases. The two-tier system + stylelint gate landed in F19 (2026-05-22); the migration backlog is now an ordered list of files in `.stylelintrc.json` under `overrides[].files`. Each removed entry is one Wave-A commit.

Steps:
1. Pick a file from the legacy list (start with the highest hex count from section 1's table for the biggest user-visible win).
2. Sweep its hex literals: `#cdd6f4` → `var(--studio-fg)`, `#94a3b8` → `var(--studio-fg-dim)`, `#1a1a1a` → `var(--studio-on-accent)` or `var(--studio-fg-strong)`, etc. Use `_tokens-semantic.scss` as the lookup table; never introduce a new private hex.
3. Replace any raw `rgba(0, 0, 0, ...)` hover / overlay tints with the corresponding `var(--alpha-black-NN)` primitive or a `--studio-bg-hover` / `--studio-bg-selected` alias if one fits.
4. Replace box-shadows (`0 1px 2px rgba(...)`) with `var(--elevation-card)` / `--elevation-popover` / `--elevation-modal`.
5. Remove the file from the legacy list in `.stylelintrc.json` so the strict ERROR severity takes effect.
6. Run `npm run lint:scss` (expect 0 errors) and the matching Playwright spec for the surface.
7. After each file, delete the matching `!important` rule from `styles.scss` if it exists.

Expected outcome: each removed legacy entry shrinks the warning count and adds ~10-150 hex replacements. The end-state legacy list is empty.

### Wave B — Sidesheet skeleton component

Goal: extract the 12 sidesheet-style overlays into a single `<app-sidesheet>` (or `<app-overlay-panel>`) component that owns `.sheet > header(title + close) + body + footer`.

Steps:
1. Create `frontend/src/app/components/sidesheet/sidesheet.component.{ts,html,scss}`.
2. API: `[title]`, `[width]`, `(close)`, `<ng-content select="[header]">` slot, default slot for body, `<ng-content select="[footer]">` slot.
3. Migrate `app-orchestrator-side-sheet`, `app-cli-usage-sheet`, `app-kanban-filter-sidesheet` first (the three biggest); the dialogs follow with a `[modal]` variant.
4. Delete `.sheet__*` styles from each migrated component's local SCSS; they're inherited from the sidesheet now.

Expected outcome: ~600 lines of duplicate SCSS removed; one consistent close button / header layout.

### Wave C — Pane header component

Goal: the prompt / protocol / git panes share a 30-line `<pane-header>` (icon + title + actions slot + maximize + hide).

Steps:
1. Create `frontend/src/app/components/pane-header/pane-header.component.{ts,html,scss}`.
2. API: `[title]`, `[icon]` (StudioIconName), `[maximized]`, `(maximize)`, `(hide)`, content-projection slot for the tab strip / extra actions.
3. The prompt pane's three-tab strip becomes a child placed in the header's slot. The protocol pane's verdict banner stays in the body.

Expected outcome: ~200 lines saved across three pane SCSS files; the next pane (e.g. the future Diff pane) ships without re-implementing the header.

### Wave D — Status-bar item component

Goal: replace the six near-identical status-bar buttons with `<app-statusbar-item>`.

Steps:
1. Create `frontend/src/app/features/shell/components/statusbar-item/statusbar-item.component.{ts,html,scss}`.
2. API: `[icon]` (StudioIconName), `[label]`, `[active]`, `(click)`.
3. The pickers (CLI / model) keep their own logic but consume the item component for the trigger.

Expected outcome: ~150 lines saved; status bar can grow without copy-paste.

### Wave E — Icon-button + chip + empty-state mixins

Goal: three reusable patterns that don't deserve full components.

Steps:
1. Add `frontend/src/styles/_mixins.scss` with:
   - `@mixin icon-button($size: 22px)` — width/height grid, hover state, disabled state, transition.
   - `@mixin chip($accent: var(--studio-accent))` — pill background + border at low alpha + readable text.
   - `@mixin empty-state` — padded muted text.
2. Sweep the obvious call sites (`.studio-sidebar__action`, `.sheet__close`, `.column__collapse`, `.statusbar__item--btn`, `.cr-empty`, `.evidence-view__empty`, ...) and replace bodies with `@include icon-button;`.

Expected outcome: ~120 lines saved; new buttons reach for a one-line include.

### Wave F — Drop the !important from styles.scss

Goal: zero `!important` in the bridge once the components migrate to tokens (waves A-E).

Steps:
1. Each commit in waves A-E should remove the corresponding bridge rule. After all five waves, audit `styles.scss` for what's still left. Most remaining cases will be component CSS that genuinely needs cross-cutting overrides; document those.

Expected outcome: 54 → 0 `!important` in styles.scss.

## 4. Pre-merge checklist

Before merging any SCSS change:

- [ ] No hardcoded hex values for known semantic roles (use tokens).
- [ ] No new `!important` in component SCSS (and ideally fewer in the bridge).
- [ ] No new `font-family:` declaration outside `_markdown-body.scss` or body reset.
- [ ] Theme overrides scoped under `html[data-studio-theme='light']` (or scoped via a `:host-context` if truly component-local).
- [ ] Selector specificity is justified (any modifier with sub-class specificity should win or fall back to a token explicitly).
- [ ] If the rule could be reused: lift to `styles/_*.scss`; if used 3+ times the same way: extract a component.
- [ ] Both themes screenshot-verified (or "dark not regression-tested, light only" called out in PR).

## 5. References

- [design-system.md](design-system.md) — token vocabulary, shape scale, type scale, motion grammar.
- [design-principles.md](design-principles.md) — UX rules that the SCSS supports.
- `frontend/src/styles.scss` — the light-theme bridge (the `!important` zone).
- `frontend/src/styles/_markdown-body.scss` — the only shared partial today; the model for new ones.
- `frontend/src/app/features/studio-shell/studio-shell.component.scss` — canonical `:root` + light override block.
