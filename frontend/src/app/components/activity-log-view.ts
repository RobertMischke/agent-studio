import { AfterViewInit, Component, ElementRef, OnDestroy, computed, effect, input, signal, viewChild } from '@angular/core';
import { CliOutputLine } from '../models/job.model';
import {
  ActivityLogFilters,
  ActivityLogGroup,
  ActivityLogKind,
  activityKindLabel,
  activityLogKinds,
  defaultActivityLogFilters,
  filterActivityGroups,
  flattenActivityLines,
  parseActivityLog
} from './activity-log.parser';

@Component({
  selector: 'app-activity-log-view',
  standalone: true,
  template: `
    <div class="activity-log" [class.activity-log--embedded]="variant() === 'embedded'">
      <div class="activity-log__toolbar">
        <div class="activity-log__tabs">
          <button class="activity-log__tab"
                  [class.activity-log__tab--active]="mode() === 'parsed'"
                  (click)="mode.set('parsed')">
            Parsed
          </button>
          <button class="activity-log__tab"
                  [class.activity-log__tab--active]="mode() === 'raw'"
                  (click)="mode.set('raw')">
            Raw
          </button>
        </div>
        <div class="activity-log__actions">
          <button class="activity-log__btn" (click)="expandAll()">Expand all</button>
          <button class="activity-log__btn" (click)="collapseAll()">Collapse all</button>
        </div>
      </div>

      <div class="activity-log__filters" aria-label="Activity log filters">
        @for (kind of filterKinds; track kind) {
          <label class="activity-log__filter">
            <input type="checkbox"
                   [checked]="filters()[kind]"
                   (change)="toggleFilter(kind)" />
            <span>{{ kindLabel(kind) }}</span>
          </label>
        }
      </div>

      <div class="activity-log__summary">
        <span>{{ visibleGroups().length }} / {{ parsedGroups().length }} groups</span>
        <span>{{ visibleLines().length }} / {{ lines().length }} lines</span>
      </div>

      <div #body
           class="activity-log__body"
           [style.max-height]="bodyMaxHeight()"
           data-testid="activity-log-body"
           (scroll)="onBodyScroll()">
        @if (!stickToBottom()) {
          <button type="button"
                  class="activity-log__jump"
                  data-testid="activity-log-jump-bottom"
                  (click)="jumpToBottom()">↓ Jump to latest</button>
        }
        @if (mode() === 'parsed') {
          @for (group of visibleGroups(); track group.id) {
            <article class="activity-group"
                     [class.activity-group--error]="group.status === 'error'"
                     [class.activity-group--neutral]="group.status === 'neutral'">
              <button class="activity-group__header" (click)="toggleGroup(group)">
                <span class="activity-group__chevron">{{ isExpanded(group) ? 'v' : '>' }}</span>
                <span class="activity-group__kind">{{ kindLabel(group.kind) }}</span>
                <span class="activity-group__title">{{ group.title }}</span>
                <span class="activity-group__count">{{ group.lines.length }}</span>
              </button>
              @if (group.subtitle) {
                <div class="activity-group__subtitle">{{ group.subtitle }}</div>
              }
              @if (isExpanded(group)) {
                <div class="activity-group__lines">
                  @for (line of group.lines; track $index) {
                    <div class="activity-line" [class.activity-line--stderr]="line.stream === 'stderr'">
                      <span class="activity-line__time">{{ formatTime(line.timestamp) }}</span>
                      <span class="activity-line__stream">{{ line.stream === 'stderr' ? 'ERR' : 'OUT' }}</span>
                      <span class="activity-line__text">{{ line.text }}</span>
                    </div>
                  }
                </div>
              }
            </article>
          }
        } @else {
          <div class="activity-raw">
            @for (line of visibleLines(); track $index) {
              <div class="activity-line" [class.activity-line--stderr]="line.stream === 'stderr'">
                <span class="activity-line__time">{{ formatTime(line.timestamp) }}</span>
                <span class="activity-line__stream">{{ line.stream === 'stderr' ? 'ERR' : 'OUT' }}</span>
                <span class="activity-line__text">{{ line.text }}</span>
              </div>
            }
          </div>
        }

        @if (visibleLines().length === 0) {
          <div class="activity-log__empty">
            {{ lines().length === 0 ? 'No activity output yet.' : 'No activity entries match the current filters.' }}
          </div>
        }
      </div>
    </div>
  `,
  styles: [`
    :host {
      display: flex;
      min-height: 0;
      flex: 1;
    }
    .activity-log {
      display: flex;
      flex-direction: column;
      min-height: 0;
      flex: 1;
      background: #0d0d1a;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      overflow: hidden;
    }
    .activity-log--embedded {
      background: transparent;
      border: 0;
      border-radius: 0;
    }
    .activity-log__toolbar,
    .activity-log__filters,
    .activity-log__summary {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 12px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .activity-log__toolbar {
      justify-content: space-between;
      background: rgba(255,255,255,0.03);
    }
    .activity-log--embedded .activity-log__toolbar {
      padding: 0 0 6px;
      background: transparent;
    }
    .activity-log--embedded .activity-log__filters {
      padding: 4px 0 6px;
    }
    .activity-log--embedded .activity-log__summary {
      padding: 3px 0 6px;
    }
    .activity-log--embedded .activity-log__body {
      padding: 2px 0 0;
      min-height: 132px;
    }
    .activity-log__tabs,
    .activity-log__actions,
    .activity-log__filters {
      flex-wrap: wrap;
    }
    .activity-log__tab,
    .activity-log__btn {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      border-radius: 4px;
      padding: 4px 9px;
      font-size: 12px;
      cursor: pointer;
    }
    .activity-log__tab--active {
      color: #d8b4fe;
      border-color: rgba(216,180,254,0.35);
      background: rgba(126,34,206,0.18);
    }
    .activity-log__filter {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      color: #cbd5e1;
      font-size: 12px;
      white-space: nowrap;
    }
    .activity-log__filter input {
      accent-color: #7c3aed;
    }
    .activity-log__summary {
      justify-content: space-between;
      color: #64748b;
      font-size: 11px;
    }
    .activity-log__body {
      overflow-y: auto;
      padding: 10px;
      min-height: 160px;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 12px;
      line-height: 1.5;
      position: relative;
      scroll-behavior: smooth;
    }
    .activity-log__jump {
      position: sticky;
      top: 0;
      float: right;
      margin: 0 0 6px 8px;
      padding: 4px 10px;
      font-size: 11px;
      color: #c4b5fd;
      background: rgba(76,29,149,0.65);
      border: 1px solid rgba(196,181,253,0.4);
      border-radius: 999px;
      cursor: pointer;
      z-index: 2;
      box-shadow: 0 2px 6px rgba(0,0,0,0.4);
    }
    .activity-log__jump:hover {
      background: rgba(124,58,237,0.85);
      color: #ede9fe;
    }
    .activity-group {
      border: 1px solid rgba(148,163,184,0.16);
      border-left: 3px solid #38bdf8;
      border-radius: 8px;
      margin-bottom: 8px;
      background: rgba(15,23,42,0.5);
      overflow: hidden;
    }
    .activity-log--embedded .activity-group {
      border-radius: 6px;
      margin-bottom: 6px;
      background: rgba(15,23,42,0.38);
    }
    .activity-group--error {
      border-left-color: #fb7185;
      background: rgba(127,29,29,0.18);
    }
    .activity-group--neutral {
      border-left-color: #94a3b8;
    }
    .activity-group__header {
      width: 100%;
      display: grid;
      grid-template-columns: 18px minmax(86px, auto) minmax(0, 1fr) auto;
      gap: 8px;
      align-items: center;
      padding: 8px 10px;
      color: #e2e8f0;
      background: transparent;
      border: 0;
      text-align: left;
      cursor: pointer;
      font: inherit;
    }
    .activity-log--embedded .activity-group__header {
      padding: 6px 8px;
    }
    .activity-group__chevron,
    .activity-group__count {
      color: #94a3b8;
      font-variant-numeric: tabular-nums;
    }
    .activity-group__kind {
      color: #a7f3d0;
      font-size: 10px;
      text-transform: uppercase;
      font-weight: 700;
      letter-spacing: 0.04em;
    }
    .activity-group__title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .activity-group__subtitle {
      padding: 0 10px 8px 126px;
      color: #94a3b8;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .activity-group__lines,
    .activity-raw {
      border-top: 1px solid rgba(255,255,255,0.05);
      padding: 6px 0;
    }
    .activity-line {
      display: grid;
      grid-template-columns: 72px 34px minmax(0, 1fr);
      gap: 8px;
      padding: 2px 10px;
      align-items: baseline;
    }
    .activity-line:hover {
      background: rgba(255,255,255,0.04);
    }
    .activity-line__time {
      color: #64748b;
      font-size: 10px;
      font-variant-numeric: tabular-nums;
    }
    .activity-line__stream {
      color: #4ade80;
      background: rgba(34,197,94,0.1);
      border-radius: 3px;
      text-align: center;
      font-size: 9px;
      font-weight: 700;
      padding: 1px 4px;
    }
    .activity-line--stderr .activity-line__stream {
      color: #fb7185;
      background: rgba(251,113,133,0.1);
    }
    .activity-line--stderr .activity-line__text {
      color: #fca5a5;
    }
    .activity-line__text {
      color: #e2e8f0;
      white-space: pre-wrap;
      word-break: break-word;
    }
    .activity-log__empty {
      padding: 24px 12px;
      color: #64748b;
      text-align: center;
    }
    @media (max-width: 720px) {
      .activity-log__toolbar,
      .activity-log__summary {
        align-items: flex-start;
        flex-direction: column;
      }
      .activity-group__header,
      .activity-line {
        grid-template-columns: 1fr;
      }
      .activity-group__subtitle {
        padding-left: 10px;
      }
    }
  `]
})
export class ActivityLogViewComponent implements AfterViewInit, OnDestroy {
  readonly lines = input<CliOutputLine[]>([]);
  readonly bodyMaxHeight = input('400px');
  readonly variant = input<'framed' | 'embedded'>('framed');
  readonly mode = signal<'parsed' | 'raw'>('parsed');
  readonly filterKinds = activityLogKinds;
  readonly filters = signal<ActivityLogFilters>({ ...defaultActivityLogFilters });
  readonly expanded = signal<Record<string, boolean>>({});
  readonly stickToBottom = signal(true);

  readonly parsedGroups = computed(() => parseActivityLog(this.lines()));
  readonly visibleGroups = computed(() => filterActivityGroups(this.parsedGroups(), this.filters()));
  readonly visibleLines = computed(() => flattenActivityLines(this.visibleGroups()));

  private readonly bodyRef = viewChild<ElementRef<HTMLDivElement>>('body');
  private scrollFrame: number | null = null;
  private suppressScrollEvent = false;

  private readonly autoScrollEffect = effect(() => {
    // Re-run whenever lines, mode, filters, or expansion change.
    this.lines();
    this.mode();
    this.visibleGroups();
    this.expanded();
    if (!this.stickToBottom()) return;
    this.scheduleScrollToBottom();
  });

  ngAfterViewInit(): void {
    this.scheduleScrollToBottom();
  }

  ngOnDestroy(): void {
    if (this.scrollFrame !== null && typeof cancelAnimationFrame !== 'undefined') {
      cancelAnimationFrame(this.scrollFrame);
    }
    this.autoScrollEffect.destroy();
  }

  onBodyScroll(): void {
    if (this.suppressScrollEvent) return;
    const el = this.bodyRef()?.nativeElement;
    if (!el) return;
    const distanceFromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    this.stickToBottom.set(distanceFromBottom <= 24);
  }

  jumpToBottom(): void {
    this.stickToBottom.set(true);
    this.scheduleScrollToBottom();
  }

  private scheduleScrollToBottom(): void {
    if (typeof requestAnimationFrame === 'undefined') return;
    if (this.scrollFrame !== null) cancelAnimationFrame(this.scrollFrame);
    this.scrollFrame = requestAnimationFrame(() => {
      this.scrollFrame = null;
      const el = this.bodyRef()?.nativeElement;
      if (!el) return;
      this.suppressScrollEvent = true;
      el.scrollTop = el.scrollHeight;
      // Release the suppression on the next frame so programmatic scroll
      // doesn't toggle stick-to-bottom off via the scroll event.
      requestAnimationFrame(() => { this.suppressScrollEvent = false; });
    });
  }

  kindLabel(kind: ActivityLogKind): string {
    return activityKindLabel(kind);
  }

  toggleFilter(kind: ActivityLogKind): void {
    this.filters.update((filters) => ({ ...filters, [kind]: !filters[kind] }));
  }

  isExpanded(group: ActivityLogGroup): boolean {
    return this.expanded()[group.id] ?? !group.collapsedByDefault;
  }

  toggleGroup(group: ActivityLogGroup): void {
    const next = !this.isExpanded(group);
    this.expanded.update((expanded) => ({ ...expanded, [group.id]: next }));
  }

  expandAll(): void {
    const expanded: Record<string, boolean> = {};
    for (const group of this.visibleGroups()) {
      expanded[group.id] = true;
    }
    this.expanded.set(expanded);
  }

  collapseAll(): void {
    const expanded: Record<string, boolean> = {};
    for (const group of this.visibleGroups()) {
      expanded[group.id] = false;
    }
    this.expanded.set(expanded);
  }

  formatTime(dateStr: string): string {
    return new Date(dateStr).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }
}
