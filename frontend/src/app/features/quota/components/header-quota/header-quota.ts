import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { CliType } from '../../../../models/task.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../../../features/quota';
import { cliTypeIcon } from '../../../../services/format.util';
import { QuotaApiService } from '../../../../features/quota';

import { TooltipDirective } from '../../../../components/tooltip';

interface QuotaWindowDisplay {
  value: string;
  barPct: number;
  tone: 'ok' | 'warn' | 'hot' | 'unknown';
  tooltip: string;
  windowKind: 'five_hour' | 'weekly';
}

interface QuotaCardModel {
  cliType: CliType;
  icon: string;
  label: string;
  ariaLabel: string;
  plan: string | null;
  shortWindow?: QuotaWindowDisplay;
  weekWindow?: QuotaWindowDisplay;
  tone: 'ok' | 'warn' | 'hot' | 'unknown';
  fetchedAt: string | null;
  stale: boolean;
  freshness: string;
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
  imports: [TooltipDirective],
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
    const label = this.cliLabel(s.cliType);
    const shortWindow = this.buildWindowDisplay(s.windows, 'five_hour');
    const weekWindow = this.buildWindowDisplay(s.windows, 'weekly');
    const tone = this.cardTone(shortWindow, weekWindow, !!s.error);
    return {
      cliType: s.cliType as CliType,
      icon: cliTypeIcon(s.cliType as CliType),
      label,
      ariaLabel: this.cardAriaLabel(label, shortWindow, weekWindow),
      plan: s.plan,
      shortWindow,
      weekWindow,
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
    const label = this.cliLabel(cliType);
    return {
      cliType,
      icon: cliTypeIcon(cliType),
      label,
      ariaLabel: `${label} quota: no data yet`,
      plan: null,
      tone: 'unknown',
      fetchedAt: null,
      stale: true,
      freshness: 'never refreshed',
      windows: [],
      error: null,
      source: null
    };
  }

  private buildWindowDisplay(windows: QuotaWindow[], kind: 'five_hour' | 'weekly'): QuotaWindowDisplay | undefined {
    const w = this.findWindow(windows, kind);
    if (!w) return undefined;
    const pct = w.usedPct == null ? null : Math.round(w.usedPct);
    const tone = this.toneFor(pct);
    const value = pct == null ? '—' : `${pct}%`;
    const barPct = Math.max(0, Math.min(100, pct ?? 0));
    const kindLabel = kind === 'five_hour' ? '5-hour rolling window' : 'Weekly window';
    let tooltip = kindLabel;
    if (w.used != null && w.limit != null) {
      tooltip += `: ${pct ?? '?'}% used (${w.used}/${w.limit}${w.unit ? ' ' + w.unit : ''})`;
    }
    if (w.resetLabel) tooltip += `, reset ${w.resetLabel}`;
    return { value, barPct, tone, tooltip, windowKind: kind };
  }

  private findWindow(windows: QuotaWindow[], kind: 'five_hour' | 'weekly'): QuotaWindow | null {
    for (const w of windows) {
      const lower = (w.label ?? '').toLowerCase();
      if (kind === 'five_hour' && (lower.includes('5h') || lower.includes('5-hour') || lower.includes('session'))) return w;
      if (kind === 'weekly' && (lower.includes('weekly') || lower.includes('week'))) return w;
    }
    return null;
  }

  private cardTone(sw?: QuotaWindowDisplay, ww?: QuotaWindowDisplay, hasError?: boolean): QuotaCardModel['tone'] {
    if (hasError && !sw && !ww) return 'unknown';
    const tones: QuotaCardModel['tone'][] = [];
    if (sw) tones.push(sw.tone);
    if (ww) tones.push(ww.tone);
    if (tones.length === 0) return 'unknown';
    if (tones.includes('hot')) return 'hot';
    if (tones.includes('warn')) return 'warn';
    return 'ok';
  }

  private cardAriaLabel(label: string, sw?: QuotaWindowDisplay, ww?: QuotaWindowDisplay): string {
    const parts = [`${label} quota`];
    if (sw) parts.push(`5h: ${sw.value}`);
    if (ww) parts.push(`weekly: ${ww.value}`);
    if (!sw && !ww) parts.push('no data yet');
    return parts.join(', ');
  }

  private toneFor(pct: number | null): QuotaCardModel['tone'] {
    if (pct === null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
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
