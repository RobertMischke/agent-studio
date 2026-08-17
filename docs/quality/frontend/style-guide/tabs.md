# Tabs

Canonical: [`<app-pane-tabs>`](../../../../frontend/src/app/components/pane-tabs/pane-tabs.component.ts) (F38). Two variants share one component, so callers do not pick between "the header tabs" and "the pill tabs" — they pass `variant`.

## Variants

| `variant` | Use it for                                                  | Active-tab cue                                  |
| --------- | ----------------------------------------------------------- | ----------------------------------------------- |
| `header`  | Full-height tab strip projected into `<app-pane-header>`'s `tabs` slot. Pane-level surfaces (Description / Files / Overview, Protocol / Activity). | 2px accent `border-bottom`, background lifts to `--studio-bg-tab-active` |
| `pill`    | Compact pill toggle group inside a pane body. Small switches like "Wrap / No-wrap". | Active tab raises against `--studio-bg-hover` background |

Both variants share:

- `.pane-tab` shape: `display: inline-flex; gap: 6px; font-size: 12px; transition: 120ms ease`.
- `.pane-tab__badge`: numeric badge (canonical for this family).
- `.pane-tab__icon`: leading icon slot.
- `.pane-tab__spinner`: 10px spinner for "loading" tab state.
- `.pane-tab__livedot`: 6px pulse dot for "live" tab state.

## Usage

```html
<app-pane-tabs variant="header">
  <button
    class="pane-tab"
    [class.pane-tab--active]="active() === 'protocol'"
    (click)="set('protocol')">
    <app-studio-icon name="protocol" [size]="14" class="pane-tab__icon"></app-studio-icon>
    <span class="pane-tab__label">Protocol</span>
    @if (protocolCount() > 0) {
      <span class="pane-tab__badge">{{ protocolCount() }}</span>
    }
  </button>
  <button
    class="pane-tab"
    [class.pane-tab--active]="active() === 'activity'"
    (click)="set('activity')">
    <app-studio-icon name="activity" [size]="14" class="pane-tab__icon"></app-studio-icon>
    <span class="pane-tab__label">Activity</span>
  </button>
</app-pane-tabs>
```

For the `pill` variant:

```html
<app-pane-tabs variant="pill">
  <button class="pane-tab" [class.pane-tab--active]="mode() === 'preview'" (click)="setMode('preview')">Preview</button>
  <button class="pane-tab" [class.pane-tab--active]="mode() === 'source'" (click)="setMode('source')">Source</button>
</app-pane-tabs>
```

## Re-ordering (data-attribute hook)

When a host wants to flip the visual order without changing DOM order, set the `pane-tabs--activity-first` modifier on the component:

```html
<app-pane-tabs variant="header" [listModifier]="taskActive() ? 'activity-first' : null">
  ...
</app-pane-tabs>
```

The modifier writes a CSS class on the `<app-pane-tabs>` host that re-orders the children via `order:`. The visual flip is a CSS detail; the DOM order stays stable so screen-readers read the original sequence.

## Pattern class: pinned-tab anatomy (AGT-2672)

An **editor-style tab strip** (a strip whose tabs are a user-managed, closable,
persisted collection — today only the studio-shell strip) has a second tab
state beyond active/inactive: **pinned**. This is a pattern class, not a
component variant; `<app-pane-tabs>` deliberately has no pinned state because
its tabs are a fixed set the user cannot open or close.

Anatomy of a pinned tab, in render order:

| Slot | Pinned | Unpinned |
| --- | --- | --- |
| Identity glyphs (project dot, num chip, surface icon) | unchanged | unchanged |
| Label | shortest string that still identifies the target; full label moves into the tooltip and the accessible name | full label |
| Trailing affordance | pin glyph, always visible, unpins on click | close glyph, hover-only |
| Width | roughly half a normal tab | full |
| Position | leftmost block, before every unpinned tab | after the pinned block |

Rules that come with the class:

- **Pinned is quiet.** The pin glyph reads `--studio-fg-muted`, not
  `--studio-accent`. A pin is a durable preference, not an acute state (hard
  rule R4), so it must not compete with status colour in the same strip.
- **The pin glyph is the exit.** Never render a pinned tab with no affordance
  at all; the glyph that states "pinned" is also the one click back out. Pair
  it with a Pin / Unpin row in the tab context menu.
- **Protection is against casual closing, not intent.** Drop the close glyph,
  ignore middle click, and skip the tab in bulk closes. Keep the explicit
  Close and Close All working.
- **No group separator.** The compact form plus the pin glyph already mark the
  boundary; a divider or tint band between the two groups is new visual noise.
- **Order is an invariant, not a sort.** Keep the tab list itself partitioned
  (`[pinned…][unpinned…]`) so drag-reorder, bulk closes, and any sidebar list
  of the same collection agree without re-deriving the order per surface.

### Quiet close affordance

In the same strip family, an unpinned tab's close glyph is a **hover
affordance**: `opacity: 0` at rest, `1` on tab hover and on the active tab, and
`1` on `:focus-visible`. Reserve its box either way so revealing it never
reflows the strip, and never swap `visibility`/`display` for it — that drops
the button out of the tab order and strands keyboard users.

The Explorer Open-tabs list mirrors both rules, since it is a second view of
the same collection. Reveal-on-active there is bound as a class from the host
template (`--revealed`), because `<app-list-row>`'s active class lives inside
its own template and emulated encapsulation cannot reach it with a descendant
selector.

Locked by `e2e/studio-shell/tab-pin-and-quiet-close.spec.ts`.

## Non-canonical tab strips today

Two surfaces still have their own tab implementation:

- **Studio-shell main tab strip** (`.studio-tab` in `studio-shell.component.scss`) — editor tab host with drag-reorder + close buttons.
- **Project-tabs strip** (`.project-tab` in `app.scss`) — top-header project switcher with brand-coloured chips.

Both predate F38 and have feature-specific requirements that would need a `variant="strip"` on `<app-pane-tabs>`. **Decision deferred** — see [migration-status.md](./migration-status.md) "T-Tabs: should studio-shell tabs migrate to `<app-pane-tabs>`?".

## DON'Ts

- **Do not** build a new tab class for a new feature. Use `<app-pane-tabs>` with `variant="header"` or `"pill"`.
- **Do not** add icons to the `pill` variant tabs unless the case really needs it. The pill variant is the compact one.
- **Do not** override `.pane-tab` typography. The font-size, weight, and transition are shared on purpose.
- **Do not** ship a tab without a `data-testid` (the underlying tab buttons should expose one).
- **Do not** add a pinned state to `<app-pane-tabs>`. Pinning belongs to the editor-style strip class above, whose tabs the user actually opens, closes, and reorders.
- **Do not** tint a pin glyph with `--studio-accent`, or add a separator between the pinned and unpinned groups. Both turn a calm preference into a signal.
- **Do not** hide a hover-only close glyph with `visibility` or `display`. Use `opacity` so the button stays focusable, and reveal it on `:focus-visible`.
- **Do not** let a pin remove the deliberate exits. Explicit Close and Close All must keep working on a pinned tab.

## Light + dark

Both variants flip automatically because they read `--studio-bg-tab-active`, `--studio-bg-hover`, `--studio-fg-strong`, `--studio-fg-dim`, `--studio-accent`. Tested by `e2e/pane-tabs.spec.ts` (if extended) and visually in [`docs/quality/frontend/design-system.md`](../design-system.md).
