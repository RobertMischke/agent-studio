import {
  AfterViewInit,
  AfterViewChecked,
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
export class PaneTabsComponent implements AfterViewInit, AfterViewChecked, OnDestroy {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private resizeObserver: ResizeObserver | null = null;
  private measurementFrame: number | null = null;
  private measurementFingerprint = '';
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

  readonly tabChange = output<string>();
  readonly overflowOpen = signal(false);
  readonly overflowAnchor = signal<HTMLElement | null>(null);
  readonly availableWidth = signal<number | null>(null);
  readonly measuredTabWidths = signal<Readonly<Record<string, number>>>({});
  readonly measuredOverflowButtonWidth = signal<number | null>(null);

  readonly inlineTabs = computed<readonly PaneTabDef[]>(() => {
    const tabs = this.tabs();
    const width = this.availableWidth();
    const minimumTabWidth = Math.max(1, this.minimumTabWidth());
    const measuredWidths = this.measuredTabWidths();

    // A zero or pre-layout measurement is not evidence of overflow. Keep all
    // tabs inline until the hidden sizing rail has measured every real label,
    // badge, icon, and indicator.
    if (width === null || width <= 0 || tabs.some(tab => measuredWidths[tab.id] === undefined)) {
      return tabs;
    }

    const tabWidth = (tab: PaneTabDef) =>
      Math.max(minimumTabWidth, measuredWidths[tab.id] ?? minimumTabWidth);
    const totalWidth = tabs.reduce((total, tab) => total + tabWidth(tab), 0);
    if (totalWidth <= width + 1) return tabs;

    const overflowWidth = Math.max(
      0,
      this.measuredOverflowButtonWidth() ?? minimumTabWidth,
    );
    const availableForTabs = Math.max(0, width - overflowWidth);
    const active = tabs.find(tab => tab.id === this.activeTabId());

    // Try the largest possible prefix first. The active tab replaces the last
    // prefix item when necessary, then the measured widths are checked again.
    for (let limit = tabs.length - 1; limit >= 1; limit -= 1) {
      const visible = tabs.slice(0, limit);
      const candidate = !active || visible.some(tab => tab.id === active.id)
        ? visible
        : [...visible.slice(0, -1), active];
      const candidateWidth = candidate.reduce((total, tab) => total + tabWidth(tab), 0);
      if (candidateWidth <= availableForTabs + 1) return candidate;
    }

    return active ? [active] : tabs.slice(0, 1);
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
    if (this.availableWidth() === null) {
      this.availableWidth.set(this.host.nativeElement.getBoundingClientRect().width);
    }
    this.scheduleMeasurement();

    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(([entry]) => {
        if (!entry) return;
        this.availableWidth.set(entry.contentRect.width);
        this.scheduleMeasurement();
        if (this.overflowTabs().length === 0) this.closeOverflow();
      });
      this.resizeObserver.observe(this.host.nativeElement);
    }

    const fonts = this.host.nativeElement.ownerDocument.fonts;
    void fonts?.ready.then(() => this.scheduleMeasurement());
  }

  ngAfterViewChecked(): void {
    const fingerprint = [
      this.variant(),
      this.minimumTabWidth(),
      ...this.tabs().map(tab =>
        [tab.id, tab.label, tab.icon, tab.emoji, tab.badge, tab.indicator].join(':'),
      ),
    ].join('|');
    if (fingerprint === this.measurementFingerprint) return;
    this.measurementFingerprint = fingerprint;
    this.scheduleMeasurement();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.resizeObserver?.disconnect();
    if (this.measurementFrame !== null) cancelAnimationFrame(this.measurementFrame);
  }

  private scheduleMeasurement(): void {
    if (this.destroyed || this.measurementFrame !== null) return;
    this.measurementFrame = requestAnimationFrame(() => {
      this.measurementFrame = null;
      if (this.destroyed) return;

      const widths: Record<string, number> = {};
      for (const element of this.host.nativeElement.querySelectorAll<HTMLElement>(
        '[data-pane-tab-measure-id]',
      )) {
        const id = element.dataset['paneTabMeasureId'];
        if (id) widths[id] = Math.ceil(element.getBoundingClientRect().width);
      }
      this.measuredTabWidths.set(widths);

      const overflow = this.host.nativeElement.querySelector<HTMLElement>(
        '[data-pane-tab-overflow-measure]',
      );
      this.measuredOverflowButtonWidth.set(
        overflow ? Math.ceil(overflow.getBoundingClientRect().width) : null,
      );
      if (this.overflowTabs().length === 0) this.closeOverflow();
    });
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
