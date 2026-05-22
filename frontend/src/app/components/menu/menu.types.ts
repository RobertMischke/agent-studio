/**
 * MenuItem — discriminated union driving <app-menu>.
 *
 * Three kinds:
 *   - `row`       a clickable row. `id` round-trips through the click output.
 *   - `separator` a thin hairline; never focusable, never clickable.
 *   - `header`    a small uppercase section label; never focusable, never clickable.
 *
 * Optional row fields cover every existing menu surface the F23 migration
 * touches without forcing callers into bespoke variants:
 *
 *   - `leadingGlyph` — coloured circular initial (project picker chip).
 *   - `trailingBadge` — count chip on the right (project picker job count).
 *   - `tooltip` — passed to the existing [appTooltip] directive.
 */
export type MenuItem =
  | MenuRow
  | { kind: 'separator' }
  | { kind: 'header'; label: string };

export interface MenuRow {
  kind: 'row';
  id: string;
  label: string;
  hint?: string;
  icon?: string;
  disabled?: boolean;
  danger?: boolean;
  active?: boolean;
  tooltip?: string;
  leadingGlyph?: { background: string; initial: string };
  trailingBadge?: string;
}

export interface MenuItemClickEvent {
  id: string;
  item: MenuRow;
}

export type MenuPlacement = 'below' | 'above' | 'right';
