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
  private measurementFrame: number | null = null;
  private destroyed = false;

  readonly tabs = input.required<readonly PaneTabDef[]>();
  readonly activeTabId = input.required<string>();
  readonly variant = input<PaneTabsVariant>('header');
  /** Optional modifier suffix applied to the container (e.g. `activity-first`). */
  readonly listModifier = input<string | null>(null);
  /** `aria-label` for the tablist container. */
  readonly ariaLabel = input<string | null>(null);
  /** Minimum readable width reserved for each inline tab. */
  readonly minimumTabWidth = input<number>(72);
  /** Width reserved for the overflow trigger, including its aggregate badge. */
  readonly overflowButtonWidth = input<number>(48);

  readonly tabChange = output<string>();
  readonly overflowOpen = signal(false);
  readonly overflowAnchor = signal<HTMLElement | null>(null);
  readonly availableWidth = signal<number | null>(null);

  readonly inlineTabs = computed<readonly PaneTabDef[]>(() => {
    const tabs = this.tabs();
    const width = this.availableWidth();
    const minimumTabWidth = Math.max(1, this.minimumTabWidth());
    const allTabsFit = width === null || tabs.length * minimumTabWidth <= width;
    const widthLimit = allTabsFit
      ? tabs.length
      : Math.max(
          1,
          Math.floor(
            (Math.max(0, width ?? 0) - Math.max(0, this.overflowButtonWidth())) /
              minimumTabWidth,
          ),
        );
    const limit = Math.min(tabs.length, widthLimit);
    if (limit >= tabs.length) return tabs;

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

  readonly overflowBadgeTotal = computed<number | null>(() => {
    let total = 0;
    let hasNumericBadge = false;
    for (const tab of this.overflowTabs()) {
      if (tab.badge === null || tab.badge === undefined || tab.badge === '') continue;
      const value = typeof tab.badge === 'number' ? tab.badge : Number(tab.badge);
      if (!Number.isFinite(value) || value <= 0) continue;
      total += value;
      hasNumericBadge = true;
    }
    return hasNumericBadge ? total : null;
  });

  readonly overflowAriaLabel = computed(() => {
    const hiddenCount = this.overflowTabs().length;
    const badgeTotal = this.overflowBadgeTotal();
    const badgeLabel = badgeTotal === null
      ? ''
      : `, ${badgeTotal} badge ${badgeTotal === 1 ? 'item' : 'items'}`;
    return `More tabs: ${hiddenCount} hidden ${hiddenCount === 1 ? 'tab' : 'tabs'}${badgeLabel}`;
  });

  readonly containerClass = computed(() => {
    const base = `pane-tabs pane-tabs--${this.variant()}`;
    const mod = this.listModifier();
    return mod ? `${base} pane-tabs--${mod}` : base;
  });

  ngAfterViewInit(): void {
    const host = this.host.nativeElement;
    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(() => this.scheduleMeasurement());
      this.resizeObserver.observe(host);
      if (host.parentElement) this.resizeObserver.observe(host.parentElement);
    }

    // The header gives this flex item exactly the space left after telemetry,
    // info, maximize, and close controls. Read that laid-out width instead of
    // applying a tab-count breakpoint that can manufacture overflow in empty
    // space. A second pass after fonts settle prevents an early zero or stale
    // glyph layout from becoming the lasting overflow state.
    this.scheduleMeasurement();
    const fonts = host.ownerDocument.fonts;
    if (fonts) void fonts.ready.then(() => this.scheduleMeasurement());
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.resizeObserver?.disconnect();
    const view = this.host.nativeElement.ownerDocument.defaultView;
    if (view && this.measurementFrame !== null) {
      view.cancelAnimationFrame(this.measurementFrame);
    }
  }

  private scheduleMeasurement(): void {
    if (this.destroyed || this.measurementFrame !== null) return;
    const view = this.host.nativeElement.ownerDocument.defaultView;
    if (!view || typeof view.requestAnimationFrame !== 'function') {
      this.measureAvailableWidth();
      return;
    }
    this.measurementFrame = view.requestAnimationFrame(() => {
      this.measurementFrame = null;
      this.measureAvailableWidth();
    });
  }

  private measureAvailableWidth(): void {
    if (this.destroyed) return;
    const width = this.host.nativeElement.getBoundingClientRect().width;
    // Hidden or not-yet-laid-out panes report zero. Keep the unlatched null
    // state (all tabs inline) until a real layout measurement is available.
    if (width <= 0) return;
    this.availableWidth.set(width);
    if (this.overflowTabs().length === 0) this.closeOverflow();
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
