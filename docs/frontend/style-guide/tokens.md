# Tokens

Every styling decision in the shell reads a token. **No component hardcodes a hex, a px, or a shadow value.** This page is the operator-facing index of the token vocabulary; the load-bearing source-of-truth is the SCSS:

- [`frontend/src/styles/_tokens-primitives.scss`](../../../frontend/src/styles/_tokens-primitives.scss) — Tier 1, raw palette + shadow scale. Never changes between themes.
- [`frontend/src/styles/_tokens-semantic.scss`](../../../frontend/src/styles/_tokens-semantic.scss) — Tier 2, semantic aliases. Flips per theme via `[data-studio-theme='light']`.

Tier 2 is the **only** layer components are allowed to read. Reaching for a Tier-1 primitive from a component is a regression (the theme switch stops working).

## Spacing

A 4px base scale. Material-aligned. Components use it for `padding`, `margin`, and `gap`; surface contracts (modal, row, section) compose larger inset tokens on top of it.

| Token                | Value | Where it shows up                              |
| -------------------- | ----- | ---------------------------------------------- |
| `--studio-spacing-1` | 4px   | Row gap inside a card, icon-icon gap           |
| `--studio-spacing-2` | 8px   | Small inline gap, kbd padding, footer gap      |
| `--studio-spacing-3` | 12px  | Default card / row padding, dialog header gap  |
| `--studio-spacing-4` | 16px  | Modal header / footer padding, sm-dialog body  |
| `--studio-spacing-5` | 24px  | Modal body padding, section inset, dialog viewport gutter |
| `--studio-spacing-6` | 32px  | Hero / empty-state padding                      |
| `--studio-spacing-7` | 48px  | Rare — first-run welcome cards                 |

**Use the lowest token that fits.** A `padding: 12px` should become `padding: var(--studio-spacing-3)`. A `gap: 8px` becomes `gap: var(--studio-spacing-2)`. The legacy codebase still has raw px in many places; that is baseline debt tracked in [migration-status.md](./migration-status.md), not a green light to add more.

### Density tokens (existing, not new)

Density is a separate axis from the spacing scale. Surfaces pick a density variant (`compact` / `default` / `cozy`) and the density tokens resolve to the right row geometry without the surface knowing the underlying px. Already in `_tokens-semantic.scss`; see [`<app-row>`](../../../frontend/src/app/components/row/row.component.ts) for the consumer.

| Token                                | Default (compact) |
| ------------------------------------ | ----------------- |
| `--studio-row-pad-block`             | 3px               |
| `--studio-row-pad-inline`            | 6px               |
| `--studio-row-gap`                   | 3px               |
| `--studio-section-gap`               | 10px              |
| `--studio-row-min-h`                 | 20px              |
| `--studio-row-min-h-interactive`     | 32px (WCAG-AA)    |

A surface that wants `default` or `cozy` overrides these four vars in its own scope (`:host { --studio-row-pad-block: var(--studio-row-pad-block-default); }`). Density and the spacing scale do not compete — density is for row geometry, spacing is for inset and gap.

## Modal padding

Single knob the operator can dial without touching every consumer.

| Token                              | Default                       | Used by                          |
| ---------------------------------- | ----------------------------- | -------------------------------- |
| `--studio-modal-padding`           | `var(--studio-spacing-5)` 24px | Base modal body padding knob     |
| `--studio-modal-padding-body`      | `var(--studio-modal-padding)` | `<app-dialog size="md">` body   |
| `--studio-modal-padding-header`    | `var(--studio-spacing-4)` 16px | `<app-dialog>` header           |
| `--studio-modal-padding-footer`    | `var(--studio-spacing-4)` 16px | `<app-dialog>` footer           |
| `--studio-modal-padding-body-sm`   | `var(--studio-spacing-4)` 16px | `<app-dialog size="sm">` body (confirm-style) |

Bumping `--studio-modal-padding` widens every default dialog body the shell renders. `--studio-modal-padding-body` aliases that base token so older call sites and docs stay readable. Picking `size="sm"` is for confirm-style dialogs that intentionally stay tight (one-line message + two buttons).

## Color

The palette is in [`_tokens-primitives.scss`](../../../frontend/src/styles/_tokens-primitives.scss); aliases are in [`_tokens-semantic.scss`](../../../frontend/src/styles/_tokens-semantic.scss). The aliases are what components read.

| Family                | Token(s)                                                                                 |
| --------------------- | ---------------------------------------------------------------------------------------- |
| Surfaces              | `--studio-bg-titlebar`, `--studio-bg-activitybar`, `--studio-bg-sidebar`, `--studio-bg-editor`, `--studio-bg-elevated`, `--studio-bg-tab-active`, `--studio-bg-tab-inactive`, `--studio-bg-hover`, `--studio-bg-selected` |
| Borders               | `--studio-border`, `--studio-border-strong`                                              |
| Foreground (text)     | `--studio-fg`, `--studio-fg-strong`, `--studio-fg-dim`, `--studio-fg-muted`              |
| Accents               | `--studio-accent` (brand orange), `--studio-accent-2` (teal), `--studio-accent-3` (blue), `--studio-accent-warn`, `--studio-accent-success`, `--studio-accent-6` (red) |
| Strong accents (F39)  | `--studio-accent-3-strong`, `--studio-accent-success-strong`, `--studio-accent-warn-strong`, `--studio-accent-6-strong` |
| Scrim (modal backdrop)| `--studio-scrim`, `--studio-scrim-soft`                                                  |
| Severity              | `--severity-pass`, `--severity-warn`, `--severity-high`, `--severity-info`, `--severity-pending` |
| Lane palette          | `--lane-backlog`, `--lane-prep`, `--lane-ready`, `--lane-progress`, `--lane-auto-review`, `--lane-human-review`, `--lane-completed`, `--lane-archive`, `--lane-failed` |
| Diff palette (F18/F53)| `--diff-add-*`, `--diff-rem-*`, `--diff-hunk-*`                                          |
| Syntax (F20)          | `--syntax-comment`, `--syntax-keyword`, `--syntax-type`, ...                             |
| Notifications (F37)   | `--notify-surface-*`, `--notify-success-*`, `--notify-info-*`, `--notify-warning-*`, `--notify-error-*`, `--notify-accent-*` |
| Tooltip (F36)         | `--studio-tooltip-bg`, `--studio-tooltip-fg`, `--studio-tooltip-border`                  |

**Why so many.** The semantic split (`accent-success` vs `severity-pass`) is intentional: an accent is a brand-driven UI affordance ("the running pill"); a severity is a meaning ("this assertion passed"). They drift independently across themes and a future redesign of "running" should not change what a "pass" looks like.

## Shape — corner radius

The shell uses a deliberately small scale. Each step is ~1.5× the previous so the eye can perceive hierarchy without breaking the IDE-flat look. Documented in [`docs/frontend/design-system.md`](../design-system.md#shape-scale); duplicated here for the operator-facing index.

| Step  | Radius | Where it's used                                              |
| ----- | ------ | ------------------------------------------------------------ |
| `xs`  | 3px    | Icon buttons, pickers in the titlebar / status bar           |
| `sm`  | 4px    | Tabs, search box, small chips, kbd hints                     |
| `md`  | 6px    | Menus, dropdowns, secondary cards                            |
| `lg`  | 8px    | Job cards, panel cards, sheet headers                        |
| `xl`  | 10px   | Status-bar dropdown menus, default modal dialog              |
| `2xl` | 12px   | Sidesheets                                                   |

These are not yet exported as `--studio-radius-*` tokens — the shell uses raw px today. Promoting them is a Phase-2 follow-up. See [migration-status.md](./migration-status.md).

## Elevation — shadow

Use-case aliases over the Tier-1 `--shadow-*` ladder. Components read the alias, never the primitive — that is how dark + light variants stay in sync.

| Token                  | Use case                                  |
| ---------------------- | ----------------------------------------- |
| `--elevation-resting`  | `none` — flat surface, no lift            |
| `--elevation-card`     | Resting card chrome                       |
| `--elevation-popover`  | Popovers, dropdowns                       |
| `--elevation-dropdown` | Medium menus / panels                     |
| `--elevation-modal`    | Modal dialogs                             |
| `--elevation-tooltip`  | Tooltip                                   |
| `--elevation-floating` | Floating modal over backdrop              |

## Type

Two families, one ramp. See [`docs/frontend/design-system.md`](../design-system.md#type-scale) for the role table (`heading-xl`, `heading-lg`, ..., `body`, `ui`, `caption`, `kbd`, `code`).

| Token        | Value                                                                       |
| ------------ | --------------------------------------------------------------------------- |
| `--font-ui`  | `'Inter', -apple-system, BlinkMacSystemFont, 'Segoe UI', system-ui, sans-serif` |
| `--font-mono`| `'JetBrains Mono', 'SF Mono', Menlo, Consolas, monospace`                   |

Font sizes are still raw px in component SCSS (12px / 13px / 15px / ...). Promoting them to `--studio-font-size-*` tokens is a Phase-2 follow-up.

## Adding a new token

1. Confirm an existing alias really doesn't fit. "I want a slightly different orange" → check `--studio-accent`, `--studio-accent-2`, `--studio-accent-3` first. If none, add a primitive in Tier 1 and an alias in Tier 2.
2. Add the alias to **both** theme blocks if it differs by theme. Forget the light block and the light theme breaks.
3. Document it in this page in the same commit.
4. If the new token replaces a literal in N components, file an opportunistic migration row in [migration-status.md](./migration-status.md). Do not migrate all N in the same commit — small slices, not big-bang.

## Where the literals still live

Tracked in [migration-status.md](./migration-status.md). Highlights:

- `frontend/src/app/app.scss` still ships `.btn`, `.btn--primary`, `.btn--danger`, `.btn--ghost`, `.btn--create` with raw `rgba(...)` and px values; baseline debt.
- `frontend/src/styles.scss` is the legacy light-theme bridge; raw hex by design, scheduled to shrink as components migrate.
- Several `*.scss` files under `frontend/src/app/features/` are in the [`frontend/.stylelintrc.json`](../../../frontend/.stylelintrc.json) `severity: "warning"` override list; each removed entry is a migration win.
