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

## Light + dark

Both variants flip automatically because they read `--studio-bg-tab-active`, `--studio-bg-hover`, `--studio-fg-strong`, `--studio-fg-dim`, `--studio-accent`. Tested by `e2e/pane-tabs.spec.ts` (if extended) and visually in [`docs/quality/frontend/design-system.md`](../design-system.md).
