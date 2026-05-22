# Frontend SCSS quality — guidelines, audit, refactor plan

A working playbook every agent (and human) should consult before touching SCSS in this repo. It pairs three things:

1. **Concrete state** — the SCSS surface as it stands today, measured.
2. **Guidelines** — the rules new code must follow.
3. **Refactor plan** — the next steps that close the gap between today and the guidelines.

Skim section 1 to understand why the rules in section 2 exist. Use section 3 as the ordered backlog when a slice has the budget for cleanup.

Related docs: [design-system.md](design-system.md) carries the visual contract (token vocabulary, shape scale, type scale, component inventory); this doc is its operational counterpart for SCSS authoring.

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

- `--studio-*` — studio shell chrome (surface / border / fg / accent / size). Declared in `app/features/studio-shell/studio-shell.component.scss`.
- `--md-*` — markdown body palette. Declared in `styles/_markdown-body.scss`.
- `--column-bg`, `--card-bg`, `--surface*`, `--border*`, `--text-*`, `--bg-page`, `--header-bg` — flat design tokens. Declared in `styles.scss` light theme bridge.
- `--accent`, `--bg`, `--danger`, `--line`, `--line-strong` — partial namespace, scattered.
- `--header-btn-*` — local naming on header buttons.
- `--md-*` — markdown body palette.

The proliferation is the root cause: every refactor in the past introduced its own prefix instead of consolidating.

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
WCAG AA contrast is enforced in the bridge instead of per-file.

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
its own hand-tuned palette. Cf. `studio-shell.component.scss` for
the token declarations and `run-git-viewer.component.scss` /
`diff-tab-view.component.scss` for the first consumers.

### Rule 1a — No hex / rgb literal outside the token block

A stricter restatement of Rule 1, called out separately because it is
the rule new SCSS most often breaks:

> Component SCSS may contain **zero** hex colours and **zero** `rgb()` /
> `rgba()` literals. The only allowed colour expressions are
> `var(--studio-*)`, `var(--diff-*)`, `var(--severity-*)`,
> `var(--lane-*)`, and `color-mix(in srgb, var(--…) X%, transparent)`.

Exceptions:

- The token block in `studio-shell.component.scss` is the one place
  where raw hex values live (declaring the tokens themselves).
- The light-theme bridge in `src/styles.scss` may use raw hex for
  legacy components still pending Wave-A migration; new components
  must not lean on the bridge.

Diff-specific palette (the F18 case): every diff renderer must read
`--diff-add-bg` / `--diff-add-fg` / `--diff-rem-bg` / `--diff-rem-fg`
/ `--diff-hunk-bg` / `--diff-hunk-fg` from
`studio-shell.component.scss`. Adding a private green/red literal
re-opens the WCAG-AA regression the tokens were created to fix.

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

Goal: collapse the 993 occurrences of the top-10 hex literals into 6 tokens.

Steps:
1. Add to `studio-shell.component.scss` `:root` and the `[data-studio-theme='light']` override block any tokens still missing (e.g. `--studio-accent-warn`, `--studio-accent-success`).
2. Sweep one file at a time, ordered by hex-literal count from section 1's table. For each: replace `#cdd6f4` with `var(--studio-fg)`, `#94a3b8` with `var(--studio-fg-dim)`, etc. Each file becomes one commit.
3. After each file, delete the matching `!important` rule from `styles.scss` if it exists.

Expected outcome: ~993 hex occurrences → ~50 (theme override declarations only). 54 → 20 `!important`.

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
