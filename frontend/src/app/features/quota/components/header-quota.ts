import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { CliType } from '../../../models/job.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../../features/quota';
import { cliTypeIcon } from '../../../services/format.util';
import { QuotaApiService } from '../../../features/quota';

interface QuotaCardModel {
  cliType: CliType;
  icon: string;
  label: string;
  plan: string | null;
  value: string;
  windowLabel: string;
  absolute: string | null;
  barPct: number;
  trend: string;
  trendLabel: string;
  tone: 'ok' | 'warn' | 'hot' | 'unknown';
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
 * Compact CLI-quota row for the app status bar. One item per primary
 * routing CLI with the same visual pattern: icon, label, current pressure,
 * trend marker, and a small usage bar.
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
  readonly report = signal<QuotaReport | null>(null);
  /** Re-evaluated every second so the freshness label ticks live. */
  readonly nowTick = signal(Date.now());
  readonly displayedCliTypes: CliType[] = ['copilot', 'claude', 'codex'];

  private pollTimer: VisibleIntervalHandle | null = null;
  // tickTimer stays raw - 1 s relative-time refresh; pause-on-hidden
  // would show stale "5 min ago" the moment the user comes back.
  private tickTimer: ReturnType<typeof setInterval> | null = null;

  readonly cards = computed<QuotaCardModel[]>(() => {
    const r = this.report();
    if (!r) return this.displayedCliTypes.map(cli => this.emptyCard(cli));
    const ttlMs = (r.ttlSeconds ?? 600) * 1000;
    const now = this.nowTick();
    return this.displayedCliTypes.map(cli => {
      const snap = r.snapshots.find(s => s.cliType === cli);
      return snap ? this.buildCard(snap, ttlMs, now) : this.emptyCard(cli);
    });
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

  private buildCard(s: QuotaSnapshot, ttlMs: number, now: number): QuotaCardModel {
    const fetchedMs = s.fetchedAt ? Date.parse(s.fetchedAt) : NaN;
    const ageMs = Number.isFinite(fetchedMs) ? Math.max(0, now - fetchedMs) : Number.POSITIVE_INFINITY;
    const stale = !s.fetchedAt || ageMs > ttlMs;
    const freshness = !s.fetchedAt
      ? 'never refreshed'
      : 'updated ' + this.formatAgo(ageMs);
    const primary = this.primaryWindow(s.windows);
    const pct = primary?.usedPct == null ? null : Math.round(primary.usedPct);
    const tone = this.toneFor(pct);
    return {
      cliType: s.cliType as CliType,
      icon: cliTypeIcon(s.cliType as CliType),
      label: this.cliLabel(s.cliType),
      plan: s.plan,
      value: pct == null ? (s.error ? '!' : '?') : `${pct}%`,
      windowLabel: primary ? this.shortLabel(primary.label) : 'quota',
      absolute: this.absoluteLabel(primary),
      barPct: Math.max(0, Math.min(100, pct ?? 0)),
      trend: this.trendFor(tone),
      trendLabel: this.trendLabelFor(tone),
      tone,
      fetchedAt: s.fetchedAt,
      stale,
      freshness,
      windows: s.windows,
      error: s.error,
      source: s.source
    };
  }

  private emptyCard(cliType: CliType): QuotaCardModel {
    return {
      cliType,
      icon: cliTypeIcon(cliType),
      label: this.cliLabel(cliType),
      plan: null,
      value: '?',
      windowLabel: 'quota',
      absolute: null,
      barPct: 0,
      trend: '→',
      trendLabel: 'No quota snapshot yet',
      tone: 'unknown',
      fetchedAt: null,
      stale: true,
      freshness: 'never refreshed',
      windows: [],
      error: null,
      source: null
    };
  }

  private primaryWindow(windows: QuotaWindow[]): QuotaWindow | null {
    if (windows.length === 0) return null;
    return [...windows].sort((a, b) => (b.usedPct ?? -1) - (a.usedPct ?? -1))[0] ?? null;
  }

  private toneFor(pct: number | null): QuotaCardModel['tone'] {
    let tone: QuotaCardModel['tone'];
    if (pct === null) tone = 'unknown';
    else if (pct < 70) tone = 'ok';
    else if (pct < 90) tone = 'warn';
    else tone = 'hot';
    return tone;
  }

  private trendFor(tone: QuotaCardModel['tone']): string {
    switch (tone) {
      case 'hot': return '↑';
      case 'warn': return '↗';
      case 'ok': return '→';
      default: return '·';
    }
  }

  private trendLabelFor(tone: QuotaCardModel['tone']): string {
    switch (tone) {
      case 'hot': return 'Rate-limit pressure is high';
      case 'warn': return 'Rate-limit pressure is rising';
      case 'ok': return 'Headroom available';
      default: return 'Quota pressure unknown';
    }
  }

  private absoluteLabel(w: QuotaWindow | null): string | null {
    if (!w || w.used === null || w.limit === null) return null;
    return `${w.used}/${w.limit}${w.unit ? ' ' + w.unit : ''}`;
  }

  /** Compact window label for the status-bar row. */
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
