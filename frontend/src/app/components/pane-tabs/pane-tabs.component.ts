import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  QueryList,
  ViewChild,
  ViewChildren,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { NgClass } from '@angular/common';
import type { Subscription } from 'rxjs';
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

export interface PaneTabWidth {
  readonly id: string;
  readonly width: number;
}

/**
 * Keeps tab order stable while moving trailing tabs into the shared overflow
 * menu. The active tab is pinned in the strip and displaces the last visible
 * inactive tab when necessary.
 */
export function fitPaneTabIds(
  tabs: readonly PaneTabDef[],
  widths: readonly PaneTabWidth[],
  availableWidth: number,
  overflowTriggerWidth: number,
  activeTabId: string,
): readonly string[] {
  const ids = tabs.map(tab => tab.id);
  if (ids.length <= 1 || availableWidth <= 0) return ids;

  const widthById = new Map(widths.map(item => [item.id, Math.max(0, item.width)]));
  const widthOf = (id: string) => widthById.get(id) ?? 72;
  let occupied = ids.reduce((sum, id) => sum + widthOf(id), 0);
  if (occupied <= availableWidth) return ids;

  const visible = new Set(ids);
  const removable = ids.filter(id => id !== activeTabId).reverse();
  while (occupied + overflowTriggerWidth > availableWidth && removable.length > 0) {
    const id = removable.shift();
    if (!id) break;
    visible.delete(id);
    occupied -= widthOf(id);
  }

  return ids.filter(id => visible.has(id));
}

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
  imports: [NgClass, StudioIconComponent, CountBadgeComponent, MenuComponent],
  templateUrl: './pane-tabs.component.html',
  styleUrl: './pane-tabs.component.scss',
})
export class PaneTabsComponent implements AfterViewInit, OnDestroy {
  readonly tabs = input.required<readonly PaneTabDef[]>();
  readonly activeTabId = input.required<string>();
  readonly variant = input<PaneTabsVariant>('header');
  /** Optional modifier suffix applied to the container (e.g. `activity-first`). */
  readonly listModifier = input<string | null>(null);
  /** `aria-label` for the tablist container. */
  readonly ariaLabel = input<string | null>(null);

  readonly tabChange = output<string>();

  @ViewChildren('tabMeasure', { read: ElementRef })
  private tabMeasures!: QueryList<ElementRef<HTMLElement>>;
  @ViewChild('overflowMeasure', { read: ElementRef })
  private overflowMeasure?: ElementRef<HTMLElement>;

  private readonly host = inject(ElementRef<HTMLElement>);
  private resizeObserver: ResizeObserver | null = null;
  private measureChanges: Subscription | null = null;
  private layoutQueued = false;

  private readonly visibleTabIds = signal<readonly string[] | null>(null);
  readonly overflowMenuOpen = signal(false);
  readonly overflowMenuAnchor = signal<HTMLElement | null>(null);

  readonly visibleTabs = computed(() => {
    const visible = this.visibleTabIds();
    if (visible === null) return this.tabs();
    const ids = new Set(visible);
    return this.tabs().filter(tab => ids.has(tab.id));
  });

  readonly overflowTabs = computed(() => {
    const visible = new Set(this.visibleTabs().map(tab => tab.id));
    return this.tabs().filter(tab => !visible.has(tab.id));
  });

  readonly overflowBadge = computed(() => this.overflowTabs().reduce((sum, tab) => {
    return sum + (typeof tab.badge === 'number' ? tab.badge : 0);
  }, 0));

  readonly totalNumericBadge = computed(() => this.tabs().reduce((sum, tab) => {
    return sum + (typeof tab.badge === 'number' ? tab.badge : 0);
  }, 0));

  readonly overflowMenuItems = computed<readonly MenuItem[]>(() => this.overflowTabs().map(tab => ({
    kind: 'row' as const,
    id: tab.id,
    label: tab.label,
    active: tab.id === this.activeTabId(),
    disabled: tab.disabled,
    ...(tab.badge !== null && tab.badge !== undefined && tab.badge !== '' && tab.badge !== 0
      ? { trailingBadge: String(tab.badge) }
      : {}),
  })));

  readonly containerClass = computed(() => {
    const base = `pane-tabs pane-tabs--${this.variant()}`;
    const mod = this.listModifier();
    return mod ? `${base} pane-tabs--${mod}` : base;
  });

  constructor() {
    effect(() => {
      this.tabs();
      this.activeTabId();
      this.queueLayout();
    });
  }

  ngAfterViewInit(): void {
    if (typeof ResizeObserver !== 'undefined') {
      this.resizeObserver = new ResizeObserver(() => this.queueLayout());
      this.observeLayoutElements();
    }
    this.measureChanges = this.tabMeasures.changes.subscribe(() => {
      this.observeLayoutElements();
      this.queueLayout();
    });
    this.queueLayout();
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    this.measureChanges?.unsubscribe();
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

  toggleOverflowMenu(event: MouseEvent): void {
    event.stopPropagation();
    this.overflowMenuAnchor.set(event.currentTarget as HTMLElement);
    this.overflowMenuOpen.update(open => !open);
  }

  closeOverflowMenu(): void {
    this.overflowMenuOpen.set(false);
  }

  onOverflowMenuItemClick(event: MenuItemClickEvent): void {
    const tab = this.tabs().find(item => item.id === event.id);
    if (!tab || tab.disabled) return;
    this.visibleTabIds.set(null);
    this.overflowMenuOpen.set(false);
    if (tab.id !== this.activeTabId()) this.tabChange.emit(tab.id);
    this.queueLayout();
  }

  private observeLayoutElements(): void {
    if (!this.resizeObserver) return;
    this.resizeObserver.disconnect();
    this.resizeObserver.observe(this.host.nativeElement);
    for (const measure of this.tabMeasures ?? []) {
      this.resizeObserver.observe(measure.nativeElement);
    }
    if (this.overflowMeasure) this.resizeObserver.observe(this.overflowMeasure.nativeElement);
  }

  private queueLayout(): void {
    if (this.layoutQueued) return;
    this.layoutQueued = true;
    queueMicrotask(() => {
      this.layoutQueued = false;
      this.recomputeLayout();
    });
  }

  private recomputeLayout(): void {
    if (!this.tabMeasures) return;
    const tabs = this.tabs();
    const widths = this.tabMeasures.toArray().map((measure, index) => ({
      id: tabs[index]?.id ?? '',
      width: measure.nativeElement.getBoundingClientRect().width,
    })).filter(item => item.id !== '');
    if (widths.length !== tabs.length) return;

    const availableWidth = this.host.nativeElement.getBoundingClientRect().width;
    const overflowWidth = this.overflowMeasure?.nativeElement.getBoundingClientRect().width ?? 32;
    const next = fitPaneTabIds(tabs, widths, availableWidth, overflowWidth, this.activeTabId());
    const current = this.visibleTabIds();
    if (current !== null && current.length === next.length && current.every((id, index) => id === next[index])) {
      return;
    }
    if (next.length === tabs.length) {
      this.visibleTabIds.set(null);
      this.overflowMenuOpen.set(false);
    } else {
      this.visibleTabIds.set(next);
    }
  }
}
