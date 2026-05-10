import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { CliType } from '../../../models/job.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../../features/quota';
import { cliTypeIcon } from '../../../services/format.util';
import { QuotaApiService } from '../../../features/quota';

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
  templateUrl: './header-quota.html',
  styleUrl: './header-quota.scss'
})
export class HeaderQuotaComponent implements OnInit, OnDestroy {
  private readonly quotaApi = inject(QuotaApiService);
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
    this.quotaApi.getQuotaReport().subscribe({
      next: (r) => this.report.set(r),
      error: () => { /* keep last value, do not clear */ }
    });
  }

  refresh(cliType: CliType, ev: Event): void {
    ev.stopPropagation();
    if (this.refreshing()[cliType]) return;
    this.refreshing.update(m => ({ ...m, [cliType]: true }));
    this.quotaApi.refreshQuotaForCli(cliType).subscribe({
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
