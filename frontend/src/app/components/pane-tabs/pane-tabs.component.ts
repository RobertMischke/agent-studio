import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  ViewEncapsulation,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { NgClass } from '@angular/common';
import { StudioIconComponent, StudioIconName } from '../studio-icon/studio-icon.component';
import { CountBadgeComponent } from '../count-badge/count-badge.component';
import { MenuComponent } from '../menu/menu.component';
import type { MenuItem, MenuItemClickEvent } from '../menu/menu.types';

/**
 * Shape of a single tab in the shared {@link PaneTabsComponent} strip.
 *
 * The component is intentionally data-driven (vs. content projection per
 * tab) so panes can hand it an array of tabs and the visual chrome —
 * active-state, hover, focus, badge slot, indicator slot — stays
 * identical across surfaces.
 */
export interface PaneTabDef {
  /** Stable identifier emitted via `tabChange`. */
  readonly id: string;
  /** User-facing label rendered next to the icon. */
  readonly label: string;
  /** Optional studio-icon (preferred over emoji when both supplied). */
  readonly icon?: StudioIconName;
  /** Optional emoji/glyph; rendered when {@link icon} is omitted. */
  readonly emoji?: string;
  /** Numeric badge shown after the label (e.g. evidence count). */
  readonly badge?: number | string | null;
  /** Disables the button but keeps it in the strip. */
  readonly disabled?: boolean;
  /** Stable test hook; falls back to no `data-testid`. */
  readonly testid?: string;
  /**
   * Right-side indicator:
   *  - `spinner` for in-flight async work next to the label
   *  - `live` for a pulsing dot signalling an active stream
   */
  readonly indicator?: 'spinner' | 'live';
  /**
   * Optional CSS class suffix appended to the button (`pane-tab--<suffix>`).
   * Used by callers that want per-tab modifiers like activity ordering;
   * the component itself never reads it.
   */
  readonly modifier?: string;
}

export type PaneTabsVariant = 'header' | 'pill';

/**
 * Shared tab control used by panes that surface multiple sub-views in a
 * single header. Two visual variants:
 *
 *   - `header` — the canonical pane-header tab strip used by the prompt
 *     pane (Description / Evidence / Code Review). Full-height cells
 *     with a bottom-border accent on the active tab; sits inside the
 *     `tabs` slot of `<app-pane-header>`.
 *   - `pill`   — a compact pill toggle group used inside a pane body
 *     (e.g. the protocol pane's Protocol / Activity inspector switch).
 *
 * The component owns ARIA wiring (`role="tablist"` / `role="tab"`,
 * `aria-selected`) so every caller gets accessible tabs by default.
 */
@Component({
  selector: 'app-pane-tabs',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  host: {
    '[class.pane-tabs-host--header]': 'variant() === "header"',
  },
  imports: [NgClass, StudioIconComponent, CountBadgeComponent, MenuComponent],
  templateUrl: './pane-tabs.component.html',
  styleUrl: './pane-tabs.component.scss',
})
export class PaneTabsComponent implements AfterViewInit, OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private resizeObserver: ResizeObserver | null = null;

  readonly tabs = input.required<readonly PaneTabDef[]>();
  readonly activeTabId = input.required<string>();
  readonly variant = input<PaneTabsVariant>('header');
  /** Optional modifier suffix applied to the container (e.g. `activity-first`). */
  readonly listModifier = input<string | null>(null);
  /** `aria-label` for the tablist container. */
  readonly ariaLabel = input<string | null>(null);
  /**
   * Optional responsive inline limit. Remaining tabs move into the shared
   * text-only overflow menu. If the active tab would be hidden, it replaces
   * the final inline slot so current location remains visible.
  */
  readonly overflowAfter = input<number | null>(null);
  /** Host width below which `overflowAfter` is applied. */
  readonly overflowBelow = input<number>(440);

  readonly tabChange = output<string>();
  readonly overflowOpen = signal(false);
  readonly overflowAnchor = signal<HTMLElement | null>(null);
  readonly availableWidth = signal<number | null>(null);

  readonly inlineTabs = computed<readonly PaneTabDef[]>(() => {
    const tabs = this.tabs();
    const requestedLimit = this.overflowAfter();
    const width = this.availableWidth();
    const compact = width === null || width < this.overflowBelow();
    if (!compact || requestedLimit === null || requestedLimit >= tabs.length) return tabs;

    const limit = Math.max(1, requestedLimit);
    const visible = tabs.slice(0, limit);
    const active = tabs.find(tab => tab.id === this.activeTabId());
    if (!active || visible.some(tab => tab.id === active.id)) return visible;
    return [...visible.slice(0, -1), active];
  });

  readonly overflowTabs = computed<readonly PaneTabDef[]>(() => {
    const visibleIds = new Set(this.inlineTabs().map(tab => tab.id));
    return this.tabs().filter(tab => !visibleIds.has(tab.id));
  });

  readonly overflowMenuItems = computed<readonly MenuItem[]>(() =>
    this.overflowTabs().map(tab => ({
      kind: 'row',
      id: tab.id,
      label: tab.label,
      disabled: tab.disabled,
      active: tab.id === this.activeTabId(),
      trailingBadge: tab.badge ? String(tab.badge) : undefined,
    })),
  );

  readonly containerClass = computed(() => {
    const base = `pane-tabs pane-tabs--${this.variant()}`;
    const mod = this.listModifier();
    return mod ? `${base} pane-tabs--${mod}` : base;
  });

  ngAfterViewInit(): void {
    if (typeof ResizeObserver === 'undefined') return;
    this.resizeObserver = new ResizeObserver(([entry]) => {
      if (!entry) return;
      this.availableWidth.set(entry.contentRect.width);
      if (entry.contentRect.width >= this.overflowBelow()) this.closeOverflow();
    });
    this.resizeObserver.observe(this.host.nativeElement);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
  }

  trackTab(_index: number, tab: PaneTabDef): string {
    return tab.id;
  }

  tabClass(tab: PaneTabDef): Record<string, boolean> {
    const active = tab.id === this.activeTabId();
    const classes: Record<string, boolean> = {
      'pane-tab': true,
      'pane-tab--active': active,
    };
    if (tab.modifier) {
      classes[`pane-tab--${tab.modifier}`] = true;
    }
    return classes;
  }

  onClick(tab: PaneTabDef): void {
    if (tab.disabled) return;
    if (tab.id === this.activeTabId()) return;
    this.tabChange.emit(tab.id);
  }

  toggleOverflow(event: MouseEvent): void {
    event.stopPropagation();
    this.overflowAnchor.set(event.currentTarget as HTMLElement);
    this.overflowOpen.update(open => !open);
  }

  closeOverflow(): void {
    this.overflowOpen.set(false);
  }

  onOverflowItemClick(event: MenuItemClickEvent): void {
    const tab = this.overflowTabs().find(candidate => candidate.id === event.id);
    this.closeOverflow();
    if (tab) this.onClick(tab);
  }
}
