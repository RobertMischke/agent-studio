import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { CliType } from '../../../../models/task.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../../../features/quota';
import { cliTypeIcon } from '../../../../services/format.util';
import { QuotaApiService } from '../../../../features/quota';

import { TooltipDirective } from '../../../../components/tooltip';
import type { StructuredTooltip } from '../../../../components/tooltip';

interface QuotaWindowDisplay {
  value: string;
  barPct: number;
  tone: 'ok' | 'warn' | 'hot' | 'unknown';
  tooltip: string;
  windowKind: 'five_hour' | 'weekly';
}

/**
 * The single most-constraining window rendered in the strip. Every CLI
 * card shows exactly one of these so the three pills line up with an
 * identical icon + name + value + bar + tag shape (the full per-window
 * breakdown stays one hover / click away in the detail modal).
 */
interface QuotaPrimaryDisplay {
  value: string;
  /** Short window tag: 5H / WK / MO / DY / ... — names what the % is for. */
  tag: string;
  barPct: number;
  hasValue: boolean;
  tone: 'ok' | 'warn' | 'hot' | 'unknown';
}

/**
 * Semantic state of a CLI quota card. Drives the highlight reading
 * for the operator: idle = quiet, warn = approaching threshold, hot =
 * over threshold, stale = snapshot older than TTL, unavailable = no
 * data, error = probe failed. F50 follow-up: the card tooltip names
 * the state explicitly so "warum ist Codex gehighlighted?" is
 * answerable without reading code.
 */
type QuotaCardState = 'idle' | 'warn' | 'hot' | 'stale' | 'unavailable' | 'error';

interface QuotaCardModel {
  cliType: CliType;
  icon: string;
  label: string;
  ariaLabel: string;
  plan: string | null;
  shortWindow?: QuotaWindowDisplay;
  weekWindow?: QuotaWindowDisplay;
  primary: QuotaPrimaryDisplay;
  tone: 'ok' | 'warn' | 'hot' | 'unknown';
  state: QuotaCardState;
  tooltip: StructuredTooltip;
  fetchedAt: string | null;
  stale: boolean;
  freshness: string;
  windows: QuotaWindow[];
  error: string | null;
  source: string | null;
}

/**
 * Compact CLI-quota row for the app status bar. One card per primary
 * routing CLI, all built from an identical shape so the three pills sit
 * on one clean line: icon, label, the single most-constraining window's
 * used%, a short window tag (5H / WK / MO), and a small usage bar.
 *
 * Showing one primary value per CLI (instead of a per-CLI-variable set
 * of window cells) is what keeps the strip uniform and readable - the
 * full per-window breakdown lives one hover (tooltip) or click (detail
 * modal) away. The primary is the highest-used window, mirroring the
 * modal's "routing headroom" so the at-a-glance number and the drill-in
 * agree.
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
    const primary = this.buildPrimaryDisplay(s.windows);
    const tone = this.cardTone(shortWindow, weekWindow, !!s.error, primary);
    const state = this.cardState(tone, stale, !!s.error, shortWindow, weekWindow, primary);
    return {
      cliType: s.cliType as CliType,
      icon: cliTypeIcon(s.cliType as CliType),
      label,
      ariaLabel: this.cardAriaLabel(label, primary),
      plan: s.plan,
      shortWindow,
      weekWindow,
      primary,
      tone,
      state,
      tooltip: this.cardTooltip(label, state, s.plan, freshness, shortWindow, weekWindow, s.error, primary),
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
      primary: { value: '—', tag: '', barPct: 0, hasValue: false, tone: 'unknown' },
      tone: 'unknown',
      state: 'unavailable',
      tooltip: this.cardTooltip(label, 'unavailable', null, 'never refreshed', undefined, undefined, null),
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

  private cardTone(
    sw?: QuotaWindowDisplay,
    ww?: QuotaWindowDisplay,
    hasError?: boolean,
    primary?: QuotaPrimaryDisplay,
  ): QuotaCardModel['tone'] {
    if (hasError && !sw && !ww && !primary?.hasValue) return 'unknown';
    const tones: QuotaCardModel['tone'][] = [];
    if (sw) tones.push(sw.tone);
    if (ww) tones.push(ww.tone);
    // The primary covers windows the 5H / WK lookup misses (e.g.
    // Copilot's monthly premium-request window) so the card highlight
    // never goes quiet just because the constraining window isn't a
    // session/weekly bucket.
    if (primary?.hasValue) tones.push(primary.tone);
    if (tones.length === 0) return 'unknown';
    if (tones.includes('hot')) return 'hot';
    if (tones.includes('warn')) return 'warn';
    return 'ok';
  }

  /**
   * The single window the strip renders: the most-constraining
   * (highest used%) of all reported windows, with a short tag naming
   * which window it is. Returns a muted placeholder when the CLI has no
   * usable window so every card keeps the same shape on the line.
   */
  private buildPrimaryDisplay(windows: QuotaWindow[]): QuotaPrimaryDisplay {
    const ranked = [...windows].sort((a, b) => (b.usedPct ?? -1) - (a.usedPct ?? -1));
    const w = ranked[0];
    if (!w || w.usedPct == null) {
      return { value: '—', tag: w ? this.windowTag(w.label) : '', barPct: 0, hasValue: false, tone: 'unknown' };
    }
    const pct = Math.round(w.usedPct);
    return {
      value: `${pct}%`,
      tag: this.windowTag(w.label),
      barPct: Math.max(0, Math.min(100, pct)),
      hasValue: true,
      tone: this.toneFor(pct),
    };
  }

  /** Short uppercase tag for a window label, e.g. "5H", "WK", "MO". */
  private windowTag(label: string): string {
    const l = (label ?? '').toLowerCase();
    if (l.includes('5h') || l.includes('5-hour') || l.includes('session')) return '5H';
    if (l.includes('week')) return 'WK';
    if (l.includes('month')) return 'MO';
    if (l.includes('dai') || l.includes('day')) return 'DY';
    if (l.includes('hour')) return 'HR';
    const word = (label ?? '').trim().split(/\s+/)[0] ?? '';
    return word.slice(0, 2).toUpperCase();
  }

  private cardAriaLabel(label: string, primary: QuotaPrimaryDisplay): string {
    if (!primary.hasValue) return `${label} quota: no data yet`;
    const tag = primary.tag ? ` ${primary.tag} window` : '';
    return `${label} quota: ${primary.value} used${tag}`;
  }

  private toneFor(pct: number | null): QuotaCardModel['tone'] {
    if (pct === null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
  }

  private cardState(
    tone: QuotaCardModel['tone'],
    stale: boolean,
    hasError: boolean,
    sw?: QuotaWindowDisplay,
    ww?: QuotaWindowDisplay,
    primary?: QuotaPrimaryDisplay,
  ): QuotaCardState {
    if (hasError) return 'error';
    // A card is only "unavailable" when it has nothing to show. The
    // primary covers CLIs whose constraining window isn't a 5H / WK
    // bucket (e.g. Copilot's monthly), so the state stays consistent
    // with the value the pill renders.
    if (!sw && !ww && !primary?.hasValue) return 'unavailable';
    if (tone === 'hot') return 'hot';
    if (tone === 'warn') return 'warn';
    if (stale) return 'stale';
    return 'idle';
  }

  private cardTooltip(
    label: string,
    state: QuotaCardState,
    plan: string | null,
    freshness: string,
    sw?: QuotaWindowDisplay,
    ww?: QuotaWindowDisplay,
    error?: string | null,
    primary?: QuotaPrimaryDisplay,
  ): StructuredTooltip {
    const stateLine = (() => {
      switch (state) {
        case 'idle':        return `<b>${label}</b> — idle (under 70% on every window)`;
        case 'warn':        return `<b>${label}</b> — quota warning (≥ 70% on at least one window)`;
        case 'hot':         return `<b>${label}</b> — quota blocked (≥ 90% on at least one window)`;
        case 'stale':       return `<b>${label}</b> — snapshot is older than the cache TTL`;
        case 'unavailable': return `<b>${label}</b> — no data yet`;
        case 'error':       return `<b>${label}</b> — probe failed`;
      }
    })();

    const lines: string[] = [stateLine];
    if (sw) lines.push(`5H rolling: ${sw.value}`);
    if (ww) lines.push(`Weekly: ${ww.value}`);
    // Surface the rendered primary when it isn't already one of the
    // 5H / WK lines (e.g. Copilot's monthly window) so the tooltip
    // explains the pill's number instead of going silent.
    if (!sw && !ww && primary?.hasValue) lines.push(`${primary.tag || 'Usage'}: ${primary.value}`);
    if (plan) lines.push(`Plan: ${plan}`);
    lines.push(freshness);
    if (error) lines.push(`Error: ${error}`);
    lines.push('<i>Click for usage detail.</i>');
    return { body: lines.join('<br>') };
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
