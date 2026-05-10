import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { TokenTimeline, TokenTimelineCell, TokenTimelineProject } from '../../../features/tokens';

const STORAGE_DISABLED_KEY = 'workspaceTokens.disabledProjects';
const STORAGE_WINDOW_KEY = 'workspaceTokens.windowHours';

type WindowHours = 1 | 6 | 24 | 168;

interface BucketBar {
  bucketStart: string;
  bucketEnd: string;
  total: number;
  segments: BucketSegment[];
  xPct: number;
  widthPct: number;
}

interface BucketSegment {
  project: string;
  cell: TokenTimelineCell;
  total: number;
  yPct: number;
  hPct: number;
  color: string;
}

/**
 * Workspace-wide token-usage timeline. One central view, segmented by
 * project, plotted on a time axis. Sourced from the orchestrator log
 * via `GET /api/workspace/tokens/timeline` (cheap, no recompute).
 *
 * Chart shape: stacked bars per bucket. The x-axis is wall-clock time
 * across the selected window (1 / 6 / 24 / 168 hours); y-axis is total
 * tokens (input + output + cache read + cache write). Each bar is split
 * vertically into one segment per project, coloured from a stable
 * hue palette so a project keeps the same colour across reloads.
 *
 * Project legend toggles cells on/off (saved per-window in localStorage)
 * so a busy project does not crowd out a quieter one. Hover any segment
 * to reveal a popover with the cell's full record (calls, per-stream
 * tokens, dollars when known).
 */
@Component({
  selector: 'app-workspace-token-timeline',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="wtt" data-testid="workspace-token-timeline">
      <header class="wtt__head">
        <div>
          <h2 class="wtt__title">Workspace token usage</h2>
          <p class="wtt__sub">
            Orchestrator LLM calls across every watched project, bucketed
            on the wall clock.
            @if (timeline(); as t) {
              <span class="wtt__sub-stats">
                · {{ totalCalls() }} call{{ totalCalls() === 1 ? '' : 's' }}
                · {{ formatTokens(grandTotalTokens()) }} tokens
              </span>
            }
          </p>
        </div>
        <div class="wtt__controls">
          <div class="wtt__win" role="radiogroup" aria-label="Window">
            @for (opt of windowOptions; track opt.hours) {
              <button type="button"
                      class="wtt__win-btn"
                      [class.wtt__win-btn--active]="windowHours() === opt.hours"
                      [attr.data-testid]="'wtt-win-' + opt.testId"
                      role="radio"
                      [attr.aria-checked]="windowHours() === opt.hours"
                      (click)="setWindow(opt.hours)">
                {{ opt.label }}
              </button>
            }
          </div>
          <button type="button"
                  class="wtt__refresh"
                  data-testid="wtt-refresh"
                  [disabled]="loading()"
                  (click)="refresh()">
            {{ loading() ? '⏳' : '↻' }}
          </button>
        </div>
      </header>

      <div class="wtt__chart" data-testid="wtt-chart" (mouseleave)="hoverCell.set(null)">
        @if (loading() && !timeline()) {
          <div class="wtt__empty">Loading timeline...</div>
        } @else if (!hasAnyData()) {
          <div class="wtt__empty">No orchestrator activity in this window.</div>
        } @else {
          <svg class="wtt__svg"
               role="img"
               aria-label="Workspace token-usage timeline"
               [attr.viewBox]="'0 0 ' + svgW + ' ' + svgH"
               preserveAspectRatio="none">
            <!-- Y-axis grid lines + labels -->
            @for (g of gridLines(); track g.pct) {
              <line class="wtt__grid"
                    [attr.x1]="padL"
                    [attr.x2]="svgW - padR"
                    [attr.y1]="yFromPct(g.pct)"
                    [attr.y2]="yFromPct(g.pct)" />
              <text class="wtt__y-lbl"
                    [attr.x]="padL - 6"
                    [attr.y]="yFromPct(g.pct) + 3">{{ g.label }}</text>
            }

            <!-- X-axis tick labels -->
            @for (tk of xTicks(); track tk.iso) {
              <text class="wtt__x-lbl"
                    [attr.x]="xFromPct(tk.pct)"
                    [attr.y]="svgH - 6"
                    text-anchor="middle">{{ tk.label }}</text>
            }

            <!-- Stacked bars -->
            @for (bar of bars(); track bar.bucketStart) {
              <g class="wtt__bar"
                 [attr.data-testid]="'wtt-bar-' + bar.bucketStart">
                @for (seg of bar.segments; track seg.project) {
                  <rect class="wtt__seg"
                        [attr.data-testid]="'wtt-seg-' + seg.project"
                        [attr.x]="xFromPct(bar.xPct)"
                        [attr.y]="yFromPct(seg.yPct + seg.hPct)"
                        [attr.width]="widthFromPct(bar.widthPct)"
                        [attr.height]="heightFromPct(seg.hPct)"
                        [attr.fill]="seg.color"
                        (mouseenter)="hoverCell.set(seg.cell)" />
                }
              </g>
            }
          </svg>

          @if (hoverCell(); as c) {
            <div class="wtt__pop" data-testid="wtt-popover" role="status">
              <div class="wtt__pop-head">
                <span class="wtt__pop-disk"
                      [style.background]="colorFor(c.project)"></span>
                <strong>{{ c.project }}</strong>
                <span class="wtt__pop-time">{{ formatBucketRange(c) }}</span>
              </div>
              <div class="wtt__pop-grid">
                <div><span class="wtt__pop-lbl">↑ input</span><span class="wtt__pop-num">{{ formatTokens(c.input) }}</span></div>
                <div><span class="wtt__pop-lbl">↓ output</span><span class="wtt__pop-num">{{ formatTokens(c.output) }}</span></div>
                @if (c.cacheRead > 0) {
                  <div><span class="wtt__pop-lbl">⚡ cache read</span><span class="wtt__pop-num">{{ formatTokens(c.cacheRead) }}</span></div>
                }
                @if (c.cacheWrite > 0) {
                  <div><span class="wtt__pop-lbl">+ cache write</span><span class="wtt__pop-num">{{ formatTokens(c.cacheWrite) }}</span></div>
                }
                <div><span class="wtt__pop-lbl">total</span><span class="wtt__pop-num">{{ formatTokens(c.total) }}</span></div>
                <div><span class="wtt__pop-lbl">calls</span><span class="wtt__pop-num">{{ c.calls }}</span></div>
                <div>
                  <span class="wtt__pop-lbl">$ theoretical</span>
                  <span class="wtt__pop-num">
                    @if (c.dollars !== null) {
                      {{ formatUsd(c.dollars) }}
                      @if (!c.allModelsPriced) {
                        <span class="wtt__pop-partial">(partial)</span>
                      }
                    } @else {
                      <span class="wtt__na">n/a</span>
                    }
                  </span>
                </div>
              </div>
            </div>
          }
        }
      </div>

      @if (timeline(); as t) {
        @if (t.projects.length > 0) {
          <div class="wtt__legend" data-testid="wtt-legend">
            @for (p of t.projects; track p.project) {
              <button type="button"
                      class="wtt__chip"
                      [class.wtt__chip--off]="isProjectDisabled(p.project)"
                      [attr.data-testid]="'wtt-legend-' + p.project"
                      [attr.aria-pressed]="!isProjectDisabled(p.project)"
                      (click)="toggleProject(p.project)">
                <span class="wtt__chip-dot"
                      [style.background]="colorFor(p.project)"></span>
                <span class="wtt__chip-name">{{ p.project }}</span>
                <span class="wtt__chip-num">{{ formatTokens(p.total) }}</span>
              </button>
            }
          </div>
        }

        <table class="wtt__tab" data-testid="wtt-table">
          <thead>
            <tr>
              <th>Project</th>
              <th class="wtt__num">Calls</th>
              <th class="wtt__num">Total tokens</th>
              <th class="wtt__num">$ theoretical</th>
              <th>Peak bucket</th>
              <th>Last activity</th>
            </tr>
          </thead>
          <tbody>
            @for (p of t.projects; track p.project) {
              <tr [class.wtt__row--off]="isProjectDisabled(p.project)">
                <td>
                  <span class="wtt__row-dot" [style.background]="colorFor(p.project)"></span>
                  {{ p.project }}
                </td>
                <td class="wtt__num">{{ p.calls }}</td>
                <td class="wtt__num">{{ formatTokens(p.total) }}</td>
                <td class="wtt__num">
                  @if (p.dollars !== null) {
                    {{ formatUsd(p.dollars) }}
                    @if (!p.allModelsPriced) {
                      <span class="wtt__pop-partial">(partial)</span>
                    }
                  } @else {
                    <span class="wtt__na">n/a</span>
                  }
                </td>
                <td>
                  @if (p.peakBucketStart) {
                    {{ formatTime(p.peakBucketStart) }}
                    <span class="wtt__row-sub">{{ formatTokens(p.peakBucketTotal) }}</span>
                  } @else {
                    <span class="wtt__na">none</span>
                  }
                </td>
                <td>
                  @if (p.lastActivity) {
                    {{ formatAgo(p.lastActivity) }}
                  } @else {
                    <span class="wtt__na">none</span>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>

        <p class="wtt__disc">{{ t.disclaimer }}</p>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .wtt {
      padding: 14px 16px;
      color: #e2e8f0;
    }
    .wtt__head {
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: 16px;
      margin-bottom: 10px;
    }
    .wtt__title {
      margin: 0;
      font-size: 1.05rem;
      font-weight: 700;
      color: #f8fafc;
    }
    .wtt__sub {
      margin: 2px 0 0;
      color: rgba(255,255,255,0.55);
      font-size: 0.78rem;
    }
    .wtt__sub-stats { font-variant-numeric: tabular-nums; }

    .wtt__controls {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .wtt__win {
      display: inline-flex;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      overflow: hidden;
    }
    .wtt__win-btn {
      background: transparent;
      border: 0;
      color: rgba(255,255,255,0.65);
      padding: 4px 10px;
      font-size: 0.78rem;
      cursor: pointer;
      font-weight: 600;
      letter-spacing: 0.02em;
    }
    .wtt__win-btn:hover { color: #f8fafc; background: rgba(255,255,255,0.06); }
    .wtt__win-btn--active {
      background: rgba(139,92,246,0.30);
      color: #f8fafc;
    }
    .wtt__refresh {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
      color: rgba(255,255,255,0.65);
      border-radius: 8px;
      padding: 4px 10px;
      font-size: 0.85rem;
      cursor: pointer;
    }
    .wtt__refresh:hover:not([disabled]) { color: #f8fafc; }

    .wtt__chart {
      position: relative;
      width: 100%;
      height: 280px;
      background: rgba(15,23,42,0.45);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 8px;
      padding: 6px;
      box-sizing: border-box;
    }
    .wtt__svg {
      width: 100%;
      height: 100%;
      display: block;
    }
    .wtt__grid {
      stroke: rgba(255,255,255,0.06);
      stroke-width: 1;
    }
    .wtt__y-lbl {
      fill: rgba(255,255,255,0.45);
      font-size: 9px;
      text-anchor: end;
      font-family: 'Segoe UI', system-ui, sans-serif;
    }
    .wtt__x-lbl {
      fill: rgba(255,255,255,0.55);
      font-size: 9px;
      font-family: 'Segoe UI', system-ui, sans-serif;
    }
    .wtt__seg {
      stroke: rgba(15,23,42,0.45);
      stroke-width: 0.5;
      cursor: pointer;
      transition: opacity 0.12s;
    }
    .wtt__seg:hover { opacity: 0.85; }

    .wtt__empty {
      display: grid;
      place-items: center;
      height: 100%;
      color: rgba(255,255,255,0.45);
      font-size: 0.85rem;
      font-style: italic;
    }

    .wtt__pop {
      position: absolute;
      top: 8px;
      right: 8px;
      max-width: 280px;
      background: #181825;
      border: 1px solid rgba(196, 181, 253, 0.45);
      border-radius: 8px;
      padding: 8px 10px;
      box-shadow: 0 6px 20px rgba(0,0,0,0.5);
      pointer-events: none;
      font-size: 0.78rem;
    }
    .wtt__pop-head {
      display: flex;
      align-items: center;
      gap: 6px;
      margin-bottom: 6px;
    }
    .wtt__pop-disk {
      width: 10px;
      height: 10px;
      border-radius: 999px;
      flex: 0 0 auto;
    }
    .wtt__pop-time {
      color: rgba(255,255,255,0.55);
      font-size: 0.72rem;
      margin-left: auto;
      font-variant-numeric: tabular-nums;
    }
    .wtt__pop-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 4px 14px;
    }
    .wtt__pop-grid > div {
      display: flex;
      justify-content: space-between;
      gap: 8px;
    }
    .wtt__pop-lbl { color: rgba(255,255,255,0.55); }
    .wtt__pop-num { font-variant-numeric: tabular-nums; color: #f8fafc; }
    .wtt__pop-partial { color: rgba(255,255,255,0.45); font-size: 0.7rem; margin-left: 4px; }
    .wtt__na { color: rgba(255,255,255,0.40); font-style: italic; }

    .wtt__legend {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-top: 10px;
    }
    .wtt__chip {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.12);
      color: #e2e8f0;
      padding: 3px 10px 3px 6px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 0.78rem;
      transition: all 0.12s;
    }
    .wtt__chip:hover { background: rgba(255,255,255,0.10); }
    .wtt__chip-dot {
      width: 10px;
      height: 10px;
      border-radius: 999px;
      flex: 0 0 auto;
    }
    .wtt__chip-num {
      color: rgba(255,255,255,0.55);
      font-variant-numeric: tabular-nums;
      font-size: 0.74rem;
    }
    .wtt__chip--off { opacity: 0.4; }
    .wtt__chip--off .wtt__chip-dot { filter: grayscale(0.6); }

    .wtt__tab {
      width: 100%;
      border-collapse: collapse;
      margin-top: 10px;
      font-size: 0.82rem;
    }
    .wtt__tab th, .wtt__tab td {
      padding: 6px 8px;
      text-align: left;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .wtt__tab th {
      color: rgba(255,255,255,0.45);
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      font-size: 0.66rem;
    }
    .wtt__num { text-align: right; font-variant-numeric: tabular-nums; }
    .wtt__row-dot {
      display: inline-block;
      width: 10px;
      height: 10px;
      border-radius: 999px;
      margin-right: 6px;
      vertical-align: middle;
    }
    .wtt__row-sub {
      color: rgba(255,255,255,0.50);
      font-size: 0.72rem;
      margin-left: 6px;
    }
    .wtt__row--off { opacity: 0.45; }

    .wtt__disc {
      margin: 10px 0 0;
      font-size: 0.74rem;
      color: rgba(255,255,255,0.55);
      line-height: 1.4;
      font-style: italic;
    }
  `]
})
export class WorkspaceTokenTimelineComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(JobService);

  // Hard-coded SVG canvas. CSS scales the rendered box; the viewBox keeps
  // the math simple and lets the chart resize without a ResizeObserver.
  readonly svgW = 800;
  readonly svgH = 220;
  readonly padL = 36;
  readonly padR = 8;
  readonly padT = 8;
  readonly padB = 18;

  readonly windowOptions: { hours: WindowHours; label: string; testId: string }[] = [
    { hours: 1,   label: '1h',  testId: '1h' },
    { hours: 6,   label: '6h',  testId: '6h' },
    { hours: 24,  label: '24h', testId: '24h' },
    { hours: 168, label: '7d',  testId: '7d' },
  ];

  readonly windowHours = signal<WindowHours>(this.readSavedWindow());
  readonly bucketMinutes = computed<number>(() => {
    const w = this.windowHours();
    if (w === 1) return 5;
    if (w === 6) return 15;
    return 60;
  });

  readonly timeline = signal<TokenTimeline | null>(null);
  readonly loading = signal(false);
  readonly hoverCell = signal<TokenTimelineCell | null>(null);
  readonly disabledProjects = signal<Set<string>>(this.readSavedDisabled());

  private pollTimer: VisibleIntervalHandle | null = null;

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setVisibleInterval(() => this.refresh(), 10_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }

  setWindow(h: WindowHours): void {
    if (this.windowHours() === h) return;
    this.windowHours.set(h);
    try { localStorage.setItem(STORAGE_WINDOW_KEY, String(h)); } catch { /* ignore */ }
    this.refresh();
  }

  refresh(): void {
    this.loading.set(true);
    this.jobService.getWorkspaceTokensTimeline(this.windowHours(), this.bucketMinutes())
      .subscribe({
        next: (t) => { this.timeline.set(t); this.loading.set(false); },
        error: () => { this.loading.set(false); /* keep last value */ },
      });
  }

  // ---- Project filter ----

  isProjectDisabled(project: string): boolean {
    return this.disabledProjects().has(project);
  }

  toggleProject(project: string): void {
    const next = new Set(this.disabledProjects());
    if (next.has(project)) next.delete(project); else next.add(project);
    this.disabledProjects.set(next);
    try { localStorage.setItem(STORAGE_DISABLED_KEY, JSON.stringify([...next])); } catch { /* ignore */ }
  }

  private readSavedDisabled(): Set<string> {
    try {
      const raw = localStorage.getItem(STORAGE_DISABLED_KEY);
      if (!raw) return new Set();
      const parsed = JSON.parse(raw);
      if (Array.isArray(parsed)) return new Set(parsed.filter(x => typeof x === 'string'));
    } catch { /* ignore */ }
    return new Set();
  }

  private readSavedWindow(): WindowHours {
    try {
      const raw = localStorage.getItem(STORAGE_WINDOW_KEY);
      if (raw) {
        const n = Number.parseInt(raw, 10);
        if (n === 1 || n === 6 || n === 24 || n === 168) return n as WindowHours;
      }
    } catch { /* ignore */ }
    return 24;
  }

  // ---- Derived chart data ----

  readonly hasAnyData = computed(() => {
    const t = this.timeline();
    if (!t) return false;
    const off = this.disabledProjects();
    return t.cells.some(c => !off.has(c.project) && c.total > 0);
  });

  readonly bars = computed<BucketBar[]>(() => {
    const t = this.timeline();
    if (!t) return [];
    const start = Date.parse(t.windowStart);
    const end = Date.parse(t.windowEnd);
    const span = Math.max(1, end - start);
    const bucketMs = t.bucketMinutes * 60 * 1000;
    const widthPct = (bucketMs / span) * 100;

    // Bucket key (ISO start) -> bar accumulator. Filter disabled projects.
    const off = this.disabledProjects();
    const byBucket = new Map<string, { startMs: number; endIso: string; cells: TokenTimelineCell[] }>();
    for (const c of t.cells) {
      if (off.has(c.project)) continue;
      const key = c.bucketStart;
      let acc = byBucket.get(key);
      if (!acc) {
        acc = { startMs: Date.parse(c.bucketStart), endIso: c.bucketEnd, cells: [] };
        byBucket.set(key, acc);
      }
      acc.cells.push(c);
    }

    // Find max bucket total for y-axis scale.
    let maxTotal = 0;
    for (const acc of byBucket.values()) {
      let s = 0;
      for (const c of acc.cells) s += c.total;
      if (s > maxTotal) maxTotal = s;
    }
    if (maxTotal <= 0) return [];

    // Project order (for stable stacking) — sort by total descending using the
    // server-supplied per-project totals, projects not in cells go last.
    const order = new Map<string, number>();
    t.projects.forEach((p, i) => order.set(p.project, i));

    const bars: BucketBar[] = [];
    for (const [key, acc] of byBucket) {
      acc.cells.sort((a, b) => (order.get(a.project) ?? 999) - (order.get(b.project) ?? 999));
      let yAcc = 0;
      const total = acc.cells.reduce((s, c) => s + c.total, 0);
      const segments: BucketSegment[] = acc.cells.map(c => {
        const hPct = (c.total / maxTotal) * 100;
        const seg: BucketSegment = {
          project: c.project,
          cell: c,
          total: c.total,
          yPct: yAcc,
          hPct,
          color: this.colorFor(c.project),
        };
        yAcc += hPct;
        return seg;
      });
      const xPct = ((acc.startMs - start) / span) * 100;
      bars.push({
        bucketStart: key,
        bucketEnd: acc.endIso,
        total,
        segments,
        xPct,
        widthPct,
      });
    }
    bars.sort((a, b) => a.bucketStart.localeCompare(b.bucketStart));
    return bars;
  });

  readonly gridLines = computed<{ pct: number; label: string }[]>(() => {
    const t = this.timeline();
    if (!t) return [];
    const off = this.disabledProjects();
    const byBucket = new Map<string, number>();
    for (const c of t.cells) {
      if (off.has(c.project)) continue;
      byBucket.set(c.bucketStart, (byBucket.get(c.bucketStart) ?? 0) + c.total);
    }
    let maxTotal = 0;
    for (const v of byBucket.values()) if (v > maxTotal) maxTotal = v;
    if (maxTotal <= 0) return [];
    return [0, 25, 50, 75, 100].map(pct => ({
      pct,
      label: this.formatTokens(Math.round((pct / 100) * maxTotal)),
    }));
  });

  readonly xTicks = computed<{ pct: number; label: string; iso: string }[]>(() => {
    const t = this.timeline();
    if (!t) return [];
    const start = Date.parse(t.windowStart);
    const end = Date.parse(t.windowEnd);
    const ticks: { pct: number; label: string; iso: string }[] = [];
    const w = this.windowHours();
    const count = w === 1 ? 4 : w === 6 ? 6 : w === 24 ? 6 : 7;
    for (let i = 0; i <= count; i++) {
      const ms = start + ((end - start) * i) / count;
      const d = new Date(ms);
      let label: string;
      if (w >= 168) {
        label = `${d.getMonth() + 1}/${d.getDate()}`;
      } else if (w >= 24) {
        label = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
      } else {
        label = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
      }
      ticks.push({ pct: (i / count) * 100, label, iso: d.toISOString() });
    }
    return ticks;
  });

  readonly totalCalls = computed(() => {
    const t = this.timeline();
    if (!t) return 0;
    const off = this.disabledProjects();
    let s = 0;
    for (const p of t.projects) if (!off.has(p.project)) s += p.calls;
    return s;
  });

  readonly grandTotalTokens = computed(() => {
    const t = this.timeline();
    if (!t) return 0;
    const off = this.disabledProjects();
    let s = 0;
    for (const p of t.projects) if (!off.has(p.project)) s += p.total;
    return s;
  });

  // ---- Layout helpers (viewBox math) ----

  yFromPct(pct: number): number {
    const usable = this.svgH - this.padT - this.padB;
    return this.padT + usable - (pct / 100) * usable;
  }
  xFromPct(pct: number): number {
    const usable = this.svgW - this.padL - this.padR;
    return this.padL + (pct / 100) * usable;
  }
  widthFromPct(pct: number): number {
    const usable = this.svgW - this.padL - this.padR;
    return Math.max(1, (pct / 100) * usable - 1);
  }
  heightFromPct(pct: number): number {
    const usable = this.svgH - this.padT - this.padB;
    return Math.max(0, (pct / 100) * usable);
  }

  // ---- Colour palette: stable per project name ----

  colorFor(project: string): string {
    // Hash the name to a hue; saturation/lightness fixed so the dark
    // theme stays coherent. Same algorithm runs on every load so a
    // project always gets the same band colour.
    let h = 0;
    for (let i = 0; i < project.length; i++) {
      h = (h * 31 + project.charCodeAt(i)) >>> 0;
    }
    const hue = h % 360;
    return `hsl(${hue}, 65%, 55%)`;
  }

  // ---- Formatting ----

  formatTokens(n: number): string {
    if (!Number.isFinite(n)) return '0';
    if (n < 1_000) return n.toString();
    if (n < 1_000_000) return (n / 1_000).toFixed(n < 10_000 ? 1 : 0) + 'K';
    return (n / 1_000_000).toFixed(n < 10_000_000 ? 2 : 1) + 'M';
  }

  formatUsd(n: number): string {
    if (!Number.isFinite(n) || n === 0) return '$0.00';
    if (n < 0.1) return '$' + n.toFixed(4);
    if (n < 1)   return '$' + n.toFixed(3);
    return '$' + n.toFixed(2);
  }

  formatBucketRange(c: TokenTimelineCell): string {
    const a = new Date(c.bucketStart);
    const b = new Date(c.bucketEnd);
    return `${pad2(a.getHours())}:${pad2(a.getMinutes())} – ${pad2(b.getHours())}:${pad2(b.getMinutes())}`;
  }

  formatTime(iso: string): string {
    const d = new Date(iso);
    return `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
  }

  formatAgo(iso: string): string {
    const ms = Date.now() - Date.parse(iso);
    if (!Number.isFinite(ms)) return 'never';
    const sec = Math.floor(ms / 1000);
    if (sec < 60) return `${sec}s ago`;
    const min = Math.floor(sec / 60);
    if (min < 60) return `${min}m ago`;
    const hr = Math.floor(min / 60);
    if (hr < 24) return `${hr}h ago`;
    const d = Math.floor(hr / 24);
    return `${d}d ago`;
  }
}

function pad2(n: number): string {
  return n < 10 ? '0' + n : String(n);
}
