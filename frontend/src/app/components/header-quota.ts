import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { JobService } from '../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../utils/visible-interval';
import { CliType, QuotaReport, QuotaSnapshot, QuotaWindow } from '../models/job.model';
import { cliTypeIcon } from '../services/format.util';

interface DonutSlot {
  /** Short label shown under the ring ("5h", "rest", "weekly"). */
  short: string;
  /** Full label kept for the hover overlay. */
  full: string;
  /** Percentage 0..100 (or null when the CLI did not report a number). */
  pct: number | null;
  /** Tone derived from pct for color. */
  tone: 'ok' | 'warn' | 'hot' | 'unknown';
}

interface QuotaCardModel {
  cliType: CliType;
  icon: string;
  label: string;
  plan: string | null;
  donuts: DonutSlot[];
  fetchedAt: string | null;
  /** Whether the snapshot is older than the backend TTL ("stale"). */
  stale: boolean;
  /** Pretty "x min ago" label. */
  freshness: string;
  /** Detailed window list for the hover overlay. */
  windows: QuotaWindow[];
  error: string | null;
  source: string | null;
}

/**
 * Compact CLI-quota donut group for the app header. One card per CLI
 * vendor; up to two ring indicators per card mirroring the CLI's two
 * primary windows (e.g. "5h" + "rest" / "weekly"). Hover surfaces a
 * rich HTML overlay with last-refresh timestamp, every window the
 * snapshot carries, the source ("/usage" / "/status" / etc.), and a
 * one-click "↻ Refresh" button that triggers a synchronous re-probe.
 *
 * The header reads the quota report from the backend's filesystem-
 * cached store on app start (no spinner; data appears immediately).
 * Stale snapshots show a small "stale" badge so the user knows the
 * number is from the previous run.
 */
@Component({
  selector: 'app-header-quota',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="hquota">
      @for (card of cards(); track card.cliType) {
        <div class="hquota__card"
             [attr.data-testid]="'hquota-card-' + card.cliType"
             [class.hquota__card--error]="!!card.error"
             [class.hquota__card--stale]="card.stale">
          <div class="hquota__head">
            <span class="hquota__icon" aria-hidden="true">{{ card.icon }}</span>
            <span class="hquota__label">{{ card.label }}</span>
            @if (card.stale) {
              <span class="hquota__stale-dot" title="Stale: snapshot is older than the cache TTL.">●</span>
            }
          </div>

          @if (card.donuts.length === 0) {
            <span class="hquota__empty">{{ card.error ? '⚠' : '…' }}</span>
          } @else {
            <div class="hquota__donuts">
              @for (d of card.donuts; track d.short) {
                <div class="hquota__donut" [attr.data-tone]="d.tone">
                  <svg viewBox="0 0 36 36" class="hquota__svg">
                    <circle class="hquota__svg-track" cx="18" cy="18" r="15.9155" />
                    @if (d.pct !== null) {
                      <circle class="hquota__svg-fill"
                              cx="18" cy="18" r="15.9155"
                              [attr.stroke-dasharray]="dashFor(d.pct)"
                              transform="rotate(-90 18 18)" />
                    }
                    <text class="hquota__svg-text" x="18" y="20" text-anchor="middle">
                      {{ d.pct === null ? '?' : (d.pct + '%') }}
                    </text>
                  </svg>
                  <span class="hquota__donut-label">{{ d.short }}</span>
                </div>
              }
            </div>
          }

          <!--
            Rich hover overlay: pure CSS (:hover), no JS state, no flicker.
            Carries the full window list, the source, the freshness, and a
            ↻ button that triggers a per-CLI re-probe.
          -->
          <div class="hquota__pop" role="tooltip">
            <div class="hquota__pop-head">
              <strong>{{ card.label }}</strong>
              @if (card.plan) { <span class="hquota__pop-plan">{{ card.plan }}</span> }
              <button type="button"
                      class="hquota__pop-refresh"
                      [disabled]="refreshing()[card.cliType]"
                      (click)="refresh(card.cliType, $event)"
                      title="Re-probe {{ card.label }} now (slow, several seconds)">
                {{ refreshing()[card.cliType] ? '⏳' : '↻' }}
              </button>
            </div>
            <div class="hquota__pop-meta">
              <span [class.hquota__pop-meta--stale]="card.stale">{{ card.freshness }}</span>
              @if (card.source) { <span> · source: {{ card.source }}</span> }
            </div>
            @if (card.error) {
              <div class="hquota__pop-error">{{ card.error }}</div>
            }
            @if (card.windows.length > 0) {
              <table class="hquota__pop-table">
                <tbody>
                  @for (w of card.windows; track w.label) {
                    <tr>
                      <td>{{ w.label }}</td>
                      <td class="hquota__pop-num">
                        @if (w.usedPct !== null) { {{ w.usedPct }}% }
                        @if (w.used !== null && w.limit !== null) {
                          <span class="hquota__pop-sub">({{ w.used }} / {{ w.limit }}{{ w.unit ? ' ' + w.unit : '' }})</span>
                        }
                      </td>
                      <td class="hquota__pop-reset">
                        @if (w.resetLabel) { resets {{ w.resetLabel }} }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          </div>
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: inline-flex; }
    .hquota { display: inline-flex; gap: 8px; align-items: stretch; }
    .hquota__card {
      position: relative;
      display: inline-flex;
      flex-direction: column;
      align-items: center;
      gap: 4px;
      padding: 6px 12px 8px;
      border-radius: 12px;
      border: 1px solid rgba(255,255,255,0.12);
      background: rgba(255,255,255,0.03);
      cursor: help;
      min-width: 96px;
    }
    .hquota__card:hover { background: rgba(255,255,255,0.06); border-color: rgba(255,255,255,0.20); }
    .hquota__card--stale { border-color: rgba(249, 226, 175, 0.40); }
    .hquota__card--error { border-color: rgba(244, 63, 94, 0.40); }
    .hquota__head {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 0.72rem;
      color: rgba(255,255,255,0.75);
      letter-spacing: 0.04em;
    }
    .hquota__icon { font-size: 0.85rem; }
    .hquota__label { font-weight: 600; }
    .hquota__stale-dot { color: #fcd34d; }
    .hquota__empty { color: rgba(255,255,255,0.50); padding: 6px 0; font-size: 0.85rem; }

    .hquota__donuts { display: inline-flex; gap: 8px; align-items: center; }
    .hquota__donut {
      display: inline-flex;
      flex-direction: column;
      align-items: center;
      gap: 2px;
    }
    .hquota__svg { width: 36px; height: 36px; }
    .hquota__svg-track { fill: none; stroke: rgba(255,255,255,0.10); stroke-width: 3; }
    .hquota__svg-fill { fill: none; stroke-width: 3; stroke-linecap: round; transition: stroke-dasharray 0.3s ease; }
    .hquota__donut[data-tone="ok"] .hquota__svg-fill { stroke: #86efac; }
    .hquota__donut[data-tone="warn"] .hquota__svg-fill { stroke: #fcd34d; }
    .hquota__donut[data-tone="hot"] .hquota__svg-fill { stroke: #fda4af; }
    .hquota__donut[data-tone="unknown"] .hquota__svg-fill { stroke: rgba(255,255,255,0.20); }
    .hquota__svg-text {
      fill: #cdd6f4;
      font-size: 9px;
      font-weight: 700;
      font-family: var(--font-mono, ui-monospace, monospace);
    }
    .hquota__donut-label {
      font-size: 0.62rem;
      color: rgba(255,255,255,0.55);
      text-transform: uppercase;
      letter-spacing: 0.06em;
    }

    .hquota__pop {
      position: absolute;
      top: calc(100% + 6px);
      right: 0;
      min-width: 320px;
      max-width: 480px;
      background: #1e1e2e;
      border: 1px solid rgba(255,255,255,0.18);
      border-radius: 10px;
      padding: 10px 12px;
      box-shadow: 0 12px 36px rgba(0,0,0,0.55);
      opacity: 0;
      pointer-events: none;
      transform: translateY(-4px);
      transition: opacity 0.10s ease, transform 0.10s ease;
      z-index: 50;
    }
    .hquota__card:hover .hquota__pop {
      opacity: 1;
      pointer-events: auto;
      transform: translateY(0);
    }
    .hquota__pop-head {
      display: flex;
      align-items: baseline;
      gap: 8px;
      margin-bottom: 4px;
      color: #cdd6f4;
    }
    .hquota__pop-plan {
      font-size: 0.72rem;
      color: rgba(255,255,255,0.55);
    }
    .hquota__pop-refresh {
      margin-left: auto;
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 6px;
      padding: 2px 8px;
      cursor: pointer;
      font-size: 0.85rem;
    }
    .hquota__pop-refresh:hover:not(:disabled) { background: rgba(255,255,255,0.12); }
    .hquota__pop-refresh:disabled { opacity: 0.5; cursor: progress; }
    .hquota__pop-meta {
      font-size: 0.72rem;
      color: rgba(255,255,255,0.55);
      margin-bottom: 6px;
    }
    .hquota__pop-meta--stale { color: #fcd34d; font-weight: 600; }
    .hquota__pop-error {
      color: #fda4af;
      background: rgba(244, 63, 94, 0.10);
      border: 1px solid rgba(244, 63, 94, 0.25);
      padding: 4px 8px;
      border-radius: 6px;
      font-size: 0.74rem;
      margin-bottom: 6px;
    }
    .hquota__pop-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.78rem;
    }
    .hquota__pop-table td {
      padding: 3px 4px;
      vertical-align: top;
      color: #cdd6f4;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .hquota__pop-table tr:last-child td { border-bottom: 0; }
    .hquota__pop-num { font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; }
    .hquota__pop-sub { color: rgba(255,255,255,0.50); font-size: 0.72rem; margin-left: 4px; }
    .hquota__pop-reset { color: rgba(255,255,255,0.55); font-size: 0.72rem; }
  `]
})
export class HeaderQuotaComponent implements OnInit, OnDestroy {
  private readonly jobService = inject(JobService);
  readonly report = signal<QuotaReport | null>(null);
  readonly refreshing = signal<{ [k: string]: boolean }>({});
  /** Re-evaluated every second so the freshness label ticks live. */
  readonly nowTick = signal(Date.now());

  private pollTimer: VisibleIntervalHandle | null = null;
  // tickTimer stays raw - 1 s relative-time refresh; pause-on-hidden
  // would show stale "5 min ago" the moment the user comes back.
  private tickTimer: ReturnType<typeof setInterval> | null = null;

  readonly cards = computed<QuotaCardModel[]>(() => {
    const r = this.report();
    if (!r) return [];
    const ttlMs = (r.ttlSeconds ?? 600) * 1000;
    const now = this.nowTick();
    return r.snapshots.map(s => this.buildCard(s, ttlMs, now));
  });

  ngOnInit(): void {
    this.fetch();
    // Poll the backend every 60s. The backend serves from cache and
    // background-refreshes stale entries, so we get fresh data without
    // forcing a re-probe.
    this.pollTimer = setVisibleInterval(() => this.fetch(), 60_000);
    this.tickTimer = setInterval(() => this.nowTick.set(Date.now()), 1_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    if (this.tickTimer != null) clearInterval(this.tickTimer);
  }

  fetch(): void {
    this.jobService.getQuotaReport().subscribe({
      next: (r) => this.report.set(r),
      error: () => { /* keep last value, do not clear */ }
    });
  }

  refresh(cliType: CliType, ev: Event): void {
    ev.stopPropagation();
    if (this.refreshing()[cliType]) return;
    this.refreshing.update(m => ({ ...m, [cliType]: true }));
    this.jobService.refreshQuotaForCli(cliType).subscribe({
      next: () => { this.fetch(); this.refreshing.update(m => ({ ...m, [cliType]: false })); },
      error: () => { this.refreshing.update(m => ({ ...m, [cliType]: false })); }
    });
  }

  /** SVG dasharray for a 0..100 fill (full circle circumference is 100). */
  dashFor(pct: number | null): string {
    const p = Math.max(0, Math.min(100, pct ?? 0));
    return `${p} ${100 - p}`;
  }

  private buildCard(s: QuotaSnapshot, ttlMs: number, now: number): QuotaCardModel {
    const fetchedMs = s.fetchedAt ? Date.parse(s.fetchedAt) : NaN;
    const ageMs = Number.isFinite(fetchedMs) ? Math.max(0, now - fetchedMs) : Number.POSITIVE_INFINITY;
    const stale = !s.fetchedAt || ageMs > ttlMs;
    const freshness = !s.fetchedAt
      ? 'never refreshed'
      : 'updated ' + this.formatAgo(ageMs);
    const donuts = s.windows.slice(0, 2).map(w => this.donutFor(w));
    return {
      cliType: s.cliType as CliType,
      icon: cliTypeIcon(s.cliType as CliType),
      label: this.cliLabel(s.cliType),
      plan: s.plan,
      donuts,
      fetchedAt: s.fetchedAt,
      stale,
      freshness,
      windows: s.windows,
      error: s.error,
      source: s.source
    };
  }

  private donutFor(w: QuotaWindow): DonutSlot {
    const pct = w.usedPct == null ? null : Math.round(w.usedPct);
    let tone: DonutSlot['tone'];
    if (pct === null) tone = 'unknown';
    else if (pct < 70) tone = 'ok';
    else if (pct < 90) tone = 'warn';
    else tone = 'hot';
    return { short: this.shortLabel(w.label), full: w.label, pct, tone };
  }

  /**
   * Compact label for under the ring. We strip the most common
   * boilerplate ("Current session ", "(all models)") so the visible
   * label is a couple of characters - matching the screenshot's
   * "5h" / "rest" aesthetic.
   */
  private shortLabel(label: string): string {
    const lower = (label ?? '').toLowerCase();
    if (lower.includes('5h') || lower.includes('5-hour') || lower.includes('session')) return '5h';
    if (lower.includes('weekly') || lower.includes('week')) return 'wk';
    if (lower.includes('monthly') || lower.includes('month')) return 'mo';
    if (lower.includes('daily') || lower.includes('day')) return 'd';
    if (lower.includes('rest')) return 'rest';
    if (lower.includes('premium')) return 'prem';
    // Fallback: first short token, capped at 4 chars.
    const first = (label ?? '').split(/\s+/)[0] ?? '';
    return first.slice(0, 4) || '?';
  }

  private cliLabel(cli: string): string {
    switch (cli) {
      case 'claude': return 'Claude';
      case 'codex': return 'Codex';
      case 'copilot': return 'Copilot';
      case 'gemini': return 'Gemini';
      default: return cli;
    }
  }

  private formatAgo(ms: number): string {
    if (!Number.isFinite(ms)) return 'never';
    const sec = Math.floor(ms / 1000);
    if (sec < 5) return 'just now';
    if (sec < 60) return `${sec} s ago`;
    const min = Math.floor(sec / 60);
    if (min < 60) return `${min} min ago`;
    const hr = Math.floor(min / 60);
    if (hr < 24) return `${hr} h ago`;
    const d = Math.floor(hr / 24);
    return `${d} d ago`;
  }
}
