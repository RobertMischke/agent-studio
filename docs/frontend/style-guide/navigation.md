# Navigation

Navigation inside the Studio shell uses the same rail vocabulary everywhere a
user moves between surfaces: section headers group destinations, tree rows open
destinations, and active state is a filled row against the sidebar surface.

## Canonical components

| Need | Use | Notes |
|---|---|---|
| Sidebar group heading | [`<app-section-header>`](../../../frontend/src/app/components/section-header/section-header.component.ts) | Static or collapsible uppercase group header, optional count, optional divider. |
| Sidebar destination row | [`<app-tree-row>`](../../../frontend/src/app/components/tree-row/tree-row.component.ts) | Icon or glyph, label, optional chevron, active state, meta, count, and projected trailing content. |
| Flat non-tree row | [`<app-row>`](../../../frontend/src/app/components/row/row.component.ts) | Use for content lists, not left-rail navigation. |

## Rail Recipe

Use this structure for a left-side navigation list. When a rail has multiple
sections, make the section headers collapsible so operators can reduce visual
density without losing the shared navigation rhythm:

```html
<aside class="my-rail" aria-label="Project sections">
  <app-section-header
    title="Context"
    [collapsible]="true"
    [collapsed]="contextCollapsed"
    (collapsedChange)="contextCollapsed = $event" />
  <app-tree-row
    level="root"
    glyph="book"
    label="Wiki"
    [active]="active === 'wiki'"
    ariaCurrent="page"
    (selectRequest)="active = 'wiki'" />
</aside>
```

```scss
.my-rail {
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: auto;
  border-right: 1px solid var(--studio-border);
  background: var(--studio-bg-sidebar);
  padding: var(--studio-spacing-1) 0;
}
```

The rail itself owns only containment: width, scroll, border, sidebar surface,
and vertical padding. Row geometry, active state, hover, chevron spacing,
glyph spacing, and count alignment stay inside the shared components.

Secondary rails mounted inside a panel should abut their detail pane. Remove
the host panel padding for that rail, set the inner layout gap to `0`, and put
reading padding on the detail pane instead. If the secondary rail width must be
operator-controlled, use a visible `role="separator"` splitter between the rail
and detail pane. The rail column stays `auto`; the splitter updates the rail's
bounded pixel width.

## Active State

Set `[active]="..."` and `ariaCurrent="page"` on the active
`<app-tree-row>`. Do not create a per-feature `.item--active` recipe. The
shared active state already flips across light and dark themes through
`--studio-accent`, `--studio-bg-selected`, and `--studio-fg-strong`.

## Trailing Status

Projected trailing content is allowed when the row needs compact exceptional
status such as a local override icon with a tooltip. Do not show default or
shipped states in navigation rows; quiet rows are the default. Keep trailing
status to a small icon or a single count. If the row needs multi-line metadata,
it is a content list, not a rail.

## Do Not

- Do not render left-rail navigation with plain `<button>` groups and local
  active styles.
- Do not hardcode dark palette colors in a rail. Use `--studio-bg-sidebar`,
  `--studio-bg-hover`, `--studio-fg-*`, and accent tokens.
- Do not add icons to `<app-menu>` rows. Menus are command lists; rails are
  navigation.
- Do not use `<app-pane-tabs>` for sidebar navigation. Tabs switch sibling
  views inside a pane; rails switch larger project or workspace destinations.
