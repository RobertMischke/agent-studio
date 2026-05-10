import {
  ChangeDetectionStrategy,
  Component,
  HostListener,
  OnDestroy,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { CliType } from '../../../models/job.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../../features/quota';
import type { AdHocUsageAggregate, TokenSummaryAggregate } from '../../../features/tokens';
import { cliTypeIcon } from '../../../services/format.util';
import { HeaderQuotaComponent } from '../../quota/components/header-quota';
import { TokensApiService } from '../../../features/tokens';
import { QuotaApiService } from '../../../features/quota';

interface QuotaRow {
  cliType: CliType;
  icon: string;
  label: string;
  plan: string | null;
  fetchedAt: string | null;
  freshness: string;
  stale: boolean;
  source: string | null;
  error: string | null;
  windows: QuotaWindow[];
  primary: QuotaWindow | null;
  primaryPct: number | null;
  primaryTone: 'ok' | 'warn' | 'hot' | 'unknown';
}

/**
 * Large hover modal anchored to the status-bar's quota strip. Shows:
 *
 * - **Quota** per CLI (top): plan, freshness, source, every window with
 *   used%/limit/reset, refresh button. Reads from
 *   `/api/cli/quota` (filesystem-cached on the backend).
 * - **Tokens** workspace-wide (bottom): real input/output/cache totals,
 *   theoretical API-cost estimate, per-model breakdown, per-project
 *   breakdown. Reads `/api/runner/token-summary-aggregate` and falls
 *   back to the on-disk cache so the value is visible immediately on
 *   first paint.
 *
 * The strip itself (donut chips) is delegated to <app-header-quota>;
 * this component adds the JS-driven hover overlay around it. Hover has
 * a 120 ms open delay so accidental fly-throughs don't pop the panel,
 * and a 220 ms close grace so the user can move their cursor onto the
 * panel itself without losing it.
 */
@Component({
  selector: 'app-usage-hover-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HeaderQuotaComponent],
  templateUrl: './usage-hover-panel.html',
  styleUrl: './usage-hover-panel.scss'
})
export class UsageHoverPanelComponent implements OnInit, OnDestroy {
  private readonly tokensApi = inject(TokensApiService);
  private readonly quotaApi = inject(QuotaApiService);
  private readonly jobService = inject(JobService);

  readonly open = signal(false);
  readonly report = signal<QuotaReport | null>(null);
  readonly tokens = signal<TokenSummaryAggregate | null>(null);
  readonly adhoc = signal<AdHocUsageAggregate | null>(null);
  readonly refreshing = signal<{ [k: string]: boolean }>({});
  readonly refreshingAll = signal(false);
  readonly nowTick = signal(Date.now());

  private quotaPollTimer: VisibleIntervalHandle | null = null;
  private tokenPollTimer: VisibleIntervalHandle | null = null;
  private adhocPollTimer: VisibleIntervalHandle | null = null;
  // tickTimer stays as raw setInterval - it's a 1 s relative-time
  // refresh, paused-on-hidden would show a stale "5 min ago" the moment
  // the user comes back to the tab. Same exception as NowTickService.
  private tickTimer: ReturnType<typeof setInterval> | null = null;
  private openTimer: ReturnType<typeof setTimeout> | null = null;
  private closeTimer: ReturnType<typeof setTimeout> | null = null;

  readonly quotaRows = computed<QuotaRow[]>(() => {
    const r = this.report();
    if (!r) return [];
    const ttlMs = (r.ttlSeconds ?? 600) * 1000;
    const now = this.nowTick();
    return r.snapshots.map(s => this.buildRow(s, ttlMs, now));
  });

  ngOnInit(): void {
    this.fetchQuota();
    this.fetchTokensCached();
    this.fetchTokensFresh();
    this.fetchAdHoc();
    this.quotaPollTimer = setVisibleInterval(() => this.fetchQuota(), 60_000);
    this.tokenPollTimer = setVisibleInterval(() => this.fetchTokensFresh(), 30_000);
    this.adhocPollTimer = setVisibleInterval(() => this.fetchAdHoc(), 60_000);
    this.tickTimer = setInterval(() => this.nowTick.set(Date.now()), 1_000);
  }

  ngOnDestroy(): void {
    if (this.quotaPollTimer != null) clearVisibleInterval(this.quotaPollTimer);
    if (this.tokenPollTimer != null) clearVisibleInterval(this.tokenPollTimer);
    if (this.adhocPollTimer != null) clearVisibleInterval(this.adhocPollTimer);
    if (this.tickTimer != null) clearInterval(this.tickTimer);
    if (this.openTimer != null) clearTimeout(this.openTimer);
    if (this.closeTimer != null) clearTimeout(this.closeTimer);
  }

  // ---- Hover gating with grace periods ----

  onAnchorEnter(): void { this.scheduleOpen(); }
  onAnchorLeave(): void { this.scheduleClose(); }
  onPopEnter(): void   { this.cancelClose(); }
  onPopLeave(): void   { this.scheduleClose(); }

  @HostListener('document:keydown.escape')
  onEscape(): void { this.openTimer && clearTimeout(this.openTimer); this.open.set(false); }

  private scheduleOpen(): void {
    this.cancelClose();
    if (this.open()) return;
    if (this.openTimer != null) return;
    this.openTimer = setTimeout(() => {
      this.open.set(true);
      this.openTimer = null;
      // Refresh on open so the user sees a current view, not a stale poll.
      this.fetchQuota();
      this.fetchTokensFresh();
      this.fetchAdHoc();
    }, 120);
  }
  private scheduleClose(): void {
    if (this.openTimer != null) { clearTimeout(this.openTimer); this.openTimer = null; }
    if (!this.open()) return;
    this.cancelClose();
    this.closeTimer = setTimeout(() => {
      this.open.set(false);
      this.closeTimer = null;
    }, 220);
  }
  private cancelClose(): void {
    if (this.closeTimer != null) { clearTimeout(this.closeTimer); this.closeTimer = null; }
  }

  // ---- Data fetches ----

  fetchQuota(): void {
    this.quotaApi.getQuotaReport().subscribe({
      next: (r) => this.report.set(r),
      error: () => { /* keep last value */ },
    });
  }

  fetchTokensCached(): void {
    this.tokensApi.getTokenSummaryAggregateCached().subscribe({
      next: (resp) => {
        if (resp.status === 200 && resp.body) this.tokens.set(resp.body);
      },
      error: () => { /* tolerated */ },
    });
  }

  fetchTokensFresh(): void {
    this.tokensApi.getTokenSummaryAggregate().subscribe({
      next: (a) => this.tokens.set(a),
      error: () => { /* keep last value */ },
    });
  }

  fetchAdHoc(): void {
    this.tokensApi.getAdHocUsage().subscribe({
      next: (a) => this.adhoc.set(a),
      error: () => { /* keep last value */ },
    });
  }

  refreshAll(ev: Event): void {
    ev.stopPropagation();
    if (this.refreshingAll()) return;
    this.refreshingAll.set(true);
    this.quotaApi.refreshQuotaAll().subscribe({
      next: () => { this.fetchQuota(); this.refreshingAll.set(false); },
      error: () => this.refreshingAll.set(false),
    });
    this.fetchTokensFresh();
  }

  refreshOne(cliType: CliType, ev: Event): void {
    ev.stopPropagation();
    if (this.refreshing()[cliType]) return;
    this.refreshing.update(m => ({ ...m, [cliType]: true }));
    this.quotaApi.refreshQuotaForCli(cliType).subscribe({
      next: () => {
        this.fetchQuota();
        this.refreshing.update(m => ({ ...m, [cliType]: false }));
      },
      error: () => this.refreshing.update(m => ({ ...m, [cliType]: false })),
    });
  }

  // ---- Formatting ----

  toneFor(pct: number | null): 'ok' | 'warn' | 'hot' | 'unknown' {
    if (pct == null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
  }

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

  formatBytes(n: number): string {
    if (!Number.isFinite(n) || n <= 0) return '0 B';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1024 / 1024).toFixed(1) + ' MB';
  }

  formatLogModified(iso: string | null): string {
    if (!iso) return 'never';
    const ms = Date.parse(iso);
    if (!Number.isFinite(ms)) return 'never';
    return this.formatAgo(Date.now() - ms);
  }

  private buildRow(s: QuotaSnapshot, ttlMs: number, now: number): QuotaRow {
    const fetchedMs = s.fetchedAt ? Date.parse(s.fetchedAt) : NaN;
    const ageMs = Number.isFinite(fetchedMs) ? Math.max(0, now - fetchedMs) : Number.POSITIVE_INFINITY;
    const stale = !s.fetchedAt || ageMs > ttlMs;
    const freshness = !s.fetchedAt ? 'never refreshed' : 'updated ' + this.formatAgo(ageMs);
    const primary = s.windows.length > 0 ? s.windows[0] : null;
    const primaryPct = primary?.usedPct == null ? null : Math.round(primary.usedPct);
    return {
      cliType: s.cliType as CliType,
      icon: cliTypeIcon(s.cliType as CliType),
      label: this.cliLabel(s.cliType),
      plan: s.plan,
      fetchedAt: s.fetchedAt,
      stale,
      freshness,
      source: s.source,
      error: s.error,
      windows: s.windows,
      primary,
      primaryPct,
      primaryTone: this.toneFor(primaryPct),
    };
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
