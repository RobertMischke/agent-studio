import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { ModalStackService } from '../../../services/modal-stack.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import type { CliType } from '../../../models/job.model';
import type { QuotaReport, QuotaSnapshot, QuotaWindow } from '../../../features/quota';
import type { AdHocUsageAggregate, TokenSummaryAggregate, TokenTimeline, WorkspaceExpensiveJob } from '../../../features/tokens';
import { cliTypeIcon } from '../../../services/format.util';
import { HeaderQuotaComponent } from '../../quota';
import { TokensApiService } from '../../../features/tokens';
import { QuotaApiService } from '../../../features/quota';
import { CliUsageDetailModalComponent } from './cli-usage-detail-modal';

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
 * Status-bar quota trigger plus deferred detail dialog. Shows:
 *
 * - **Quota** per CLI (top): plan, freshness, source, every window with
 *   used%/limit/reset, refresh button. Reads from
 *   `/api/cli/quota` (filesystem-cached on the backend).
 * - **Tokens** workspace-wide: real input/output/cache totals, trend
 *   buckets, model breakdown, top jobs, and theoretical API-cost estimate.
 *   Reads through the token endpoints backed by `ITokenAggregator` and
 *   falls back to the on-disk cache so the value is visible immediately.
 *
 * The strip itself is delegated to <app-header-quota>; this component
 * owns the hover / click / keyboard modal state and preloads all
 * read-only data so the dialog renders immediately when opened.
 *
 * Hover open has a 120 ms grace period (so accidental fly-throughs do
 * not pop the modal) and a 220 ms close grace (so the user can move
 * their cursor from the trigger onto the modal itself without losing
 * it). Click and Enter still open the modal instantly for keyboard /
 * touch users.
 */
@Component({
  selector: 'app-usage-hover-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HeaderQuotaComponent, CliUsageDetailModalComponent],
  templateUrl: './usage-hover-panel.html',
  styleUrl: './usage-hover-panel.scss'
})
export class UsageHoverPanelComponent implements OnInit, OnDestroy {
  private readonly tokensApi = inject(TokensApiService);
  private readonly quotaApi = inject(QuotaApiService);

  readonly open = signal(false);
  readonly report = signal<QuotaReport | null>(null);
  readonly tokens = signal<TokenSummaryAggregate | null>(null);
  readonly adhoc = signal<AdHocUsageAggregate | null>(null);
  readonly timeline24h = signal<TokenTimeline | null>(null);
  readonly timeline7d = signal<TokenTimeline | null>(null);
  readonly expensiveJobs = signal<WorkspaceExpensiveJob[]>([]);
  readonly refreshing = signal<Record<string, boolean>>({});
  readonly refreshingAll = signal(false);
  readonly nowTick = signal(Date.now());

  private quotaPollTimer: VisibleIntervalHandle | null = null;
  private tokenPollTimer: VisibleIntervalHandle | null = null;
  private adhocPollTimer: VisibleIntervalHandle | null = null;
  private detailPollTimer: VisibleIntervalHandle | null = null;
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
    this.fetchDetailCached();
    this.fetchDetail();
    this.quotaPollTimer = setVisibleInterval(() => this.fetchQuota(), 60_000);
    this.tokenPollTimer = setVisibleInterval(() => this.fetchTokensFresh(), 30_000);
    this.adhocPollTimer = setVisibleInterval(() => this.fetchAdHoc(), 60_000);
    this.detailPollTimer = setVisibleInterval(() => this.fetchDetail(), 60_000);
    this.tickTimer = setInterval(() => this.nowTick.set(Date.now()), 1_000);
  }

  ngOnDestroy(): void {
    if (this.quotaPollTimer != null) clearVisibleInterval(this.quotaPollTimer);
    if (this.tokenPollTimer != null) clearVisibleInterval(this.tokenPollTimer);
    if (this.adhocPollTimer != null) clearVisibleInterval(this.adhocPollTimer);
    if (this.detailPollTimer != null) clearVisibleInterval(this.detailPollTimer);
    if (this.tickTimer != null) clearInterval(this.tickTimer);
    if (this.openTimer != null) clearTimeout(this.openTimer);
    if (this.closeTimer != null) clearTimeout(this.closeTimer);
  }

  openPanel(ev?: Event): void {
    ev?.stopPropagation();
    this.cancelOpen();
    this.cancelClose();
    this.open.set(true);
    this.fetchQuota();
    this.fetchTokensFresh();
    this.fetchAdHoc();
    this.fetchDetail();
  }

  closePanel(): void {
    this.cancelOpen();
    this.cancelClose();
    this.open.set(false);
  }

  onTriggerKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Enter' && event.key !== ' ') return;
    event.preventDefault();
    this.openPanel(event);
  }

  // ---- Hover gating with grace periods ----
  // Hover open after 120 ms so a cursor flying through the bar doesn't
  // pop the modal; close after 220 ms so the user can move from the
  // trigger onto the modal itself without losing it.

  onAnchorEnter(): void { this.scheduleOpen(); }
  onAnchorLeave(): void { this.scheduleClose(); }
  onPopEnter(): void { this.cancelClose(); }
  onPopLeave(): void { this.scheduleClose(); }

  // Escape routes through ModalStack so a confirm/error dialog above
  // wins first. The panel registers itself only while it is open.
  private readonly modalStack = inject(ModalStackService);
  private readonly hoverDestroyRef = inject(DestroyRef);
  private hoverStackDispose: (() => void) | null = null;
  private readonly hoverStackEffect = effect(() => {
    const isOpen = this.open();
    if (isOpen && !this.hoverStackDispose) {
      this.hoverStackDispose = this.modalStack.push('usage-hover-panel', () => this.closePanel());
    } else if (!isOpen && this.hoverStackDispose) {
      this.hoverStackDispose();
      this.hoverStackDispose = null;
    }
  });
  private readonly hoverStackTeardown = this.hoverDestroyRef.onDestroy(() => this.hoverStackDispose?.());

  private scheduleOpen(): void {
    this.cancelClose();
    if (this.open() || this.openTimer != null) return;
    this.openTimer = setTimeout(() => {
      this.openTimer = null;
      this.open.set(true);
      this.fetchQuota();
      this.fetchTokensFresh();
      this.fetchAdHoc();
      this.fetchDetail();
    }, 120);
  }

  private scheduleClose(): void {
    this.cancelOpen();
    if (!this.open() || this.closeTimer != null) return;
    this.closeTimer = setTimeout(() => {
      this.closeTimer = null;
      this.open.set(false);
    }, 220);
  }

  private cancelOpen(): void {
    if (this.openTimer != null) {
      clearTimeout(this.openTimer);
      this.openTimer = null;
    }
  }

  private cancelClose(): void {
    if (this.closeTimer != null) {
      clearTimeout(this.closeTimer);
      this.closeTimer = null;
    }
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

  fetchDetail(): void {
    this.tokensApi.getWorkspaceTokensTimeline(24, 60).subscribe({
      next: (t) => this.timeline24h.set(t),
      error: () => { /* keep last value */ },
    });
    this.tokensApi.getWorkspaceTokensTimeline(168, 60).subscribe({
      next: (t) => this.timeline7d.set(t),
      error: () => { /* keep last value */ },
    });
    this.tokensApi.getWorkspaceExpensiveJobs(8).subscribe({
      next: (r) => this.expensiveJobs.set(r.jobs ?? []),
      error: () => { /* keep last value */ },
    });
  }

  /**
   * On-disk snapshot read for timeline + expensive-jobs. Runs once on
   * panel init so a hover triggered before the live aggregator has
   * answered still renders real numbers from the last successful run.
   * 204 responses fall through silently.
   */
  fetchDetailCached(): void {
    this.tokensApi.getWorkspaceTokensTimelineCached(24, 60).subscribe({
      next: (resp) => { if (resp.status === 200 && resp.body) this.timeline24h.set(resp.body); },
      error: () => { /* tolerated */ },
    });
    this.tokensApi.getWorkspaceTokensTimelineCached(168, 60).subscribe({
      next: (resp) => { if (resp.status === 200 && resp.body) this.timeline7d.set(resp.body); },
      error: () => { /* tolerated */ },
    });
    this.tokensApi.getWorkspaceExpensiveJobsCached().subscribe({
      next: (resp) => { if (resp.status === 200 && resp.body) this.expensiveJobs.set(resp.body.jobs ?? []); },
      error: () => { /* tolerated */ },
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
    this.fetchDetail();
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

  refreshOneFromModal(payload: { cliType: CliType; event: Event }): void {
    this.refreshOne(payload.cliType, payload.event);
  }

  // ---- Formatting ----

  toneFor(pct: number | null): 'ok' | 'warn' | 'hot' | 'unknown' {
    if (pct == null) return 'unknown';
    if (pct < 70) return 'ok';
    if (pct < 90) return 'warn';
    return 'hot';
  }

  private buildRow(s: QuotaSnapshot, ttlMs: number, now: number): QuotaRow {
    const fetchedMs = s.fetchedAt ? Date.parse(s.fetchedAt) : NaN;
    const ageMs = Number.isFinite(fetchedMs) ? Math.max(0, now - fetchedMs) : Number.POSITIVE_INFINITY;
    const stale = !s.fetchedAt || ageMs > ttlMs;
    const freshness = !s.fetchedAt ? 'never refreshed' : 'updated ' + this.formatAgo(ageMs);
    const primary = s.windows.length > 0
      ? [...s.windows].sort((a, b) => (b.usedPct ?? -1) - (a.usedPct ?? -1))[0]
      : null;
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
