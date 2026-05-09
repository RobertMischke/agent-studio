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
import { JobService } from '../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../utils/visible-interval';
import {
  AdHocUsageAggregate,
  CliType,
  QuotaReport,
  QuotaSnapshot,
  QuotaWindow,
  TokenSummaryAggregate,
} from '../models/job.model';
import { cliTypeIcon } from '../services/format.util';
import { HeaderQuotaComponent } from './header-quota';

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
  template: `
    <div class="uhp"
         data-testid="usage-hover-panel"
         (mouseenter)="onAnchorEnter()"
         (mouseleave)="onAnchorLeave()">
      <app-header-quota />
      @if (open()) {
        <div class="uhp__pop"
             data-testid="hquota-modal"
             role="dialog"
             aria-label="CLI usage and token consumption"
             (mouseenter)="onPopEnter()"
             (mouseleave)="onPopLeave()">
          <header class="uhp__head">
            <h3 class="uhp__title">CLI usage &amp; tokens</h3>
            <a class="uhp__timeline-link"
               data-testid="usage-hover-panel-open-timeline"
               href="#/workspace/tokens"
               title="Open workspace token timeline">
              📈 Timeline
            </a>
            <button type="button"
                    class="uhp__refresh"
                    data-testid="usage-hover-panel-refresh-all"
                    [disabled]="refreshingAll()"
                    (click)="refreshAll($event)"
                    title="Re-probe every CLI now (slow, several seconds)">
              {{ refreshingAll() ? '⏳ Refreshing…' : '↻ Refresh all' }}
            </button>
          </header>

          <section class="uhp__sec uhp__sec--quota" data-testid="hquota-modal-quota">
            <h4 class="uhp__sec-title">Subscription quota</h4>
            @if (quotaRows().length === 0) {
              <p class="uhp__empty">No quota data yet. The first probe takes ~30s per CLI.</p>
            } @else {
              <table class="uhp__qtab" data-testid="usage-hover-panel-quota-table">
                <thead>
                  <tr>
                    <th>CLI</th>
                    <th>Plan</th>
                    <th class="uhp__num">Used</th>
                    <th>Window</th>
                    <th>Resets</th>
                    <th>Updated</th>
                    <th></th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of quotaRows(); track row.cliType) {
                    @if (row.windows.length === 0) {
                      <tr [class.uhp__row--stale]="row.stale"
                          [class.uhp__row--err]="!!row.error"
                          [attr.data-testid]="'hquota-modal-cli-' + row.cliType">
                        <td>
                          <span class="uhp__icon">{{ row.icon }}</span>
                          <strong>{{ row.label }}</strong>
                        </td>
                        <td>{{ row.plan ?? '—' }}</td>
                        <td class="uhp__num">{{ row.error ? '⚠' : '…' }}</td>
                        <td colspan="2">{{ row.error ?? 'No window data yet.' }}</td>
                        <td [class.uhp__stale]="row.stale">{{ row.freshness }}</td>
                        <td>
                          <button type="button"
                                  class="uhp__refresh-cli"
                                  [disabled]="refreshing()[row.cliType]"
                                  (click)="refreshOne(row.cliType, $event)"
                                  [attr.data-testid]="'usage-hover-panel-refresh-' + row.cliType"
                                  [title]="'Re-probe ' + row.label">
                            {{ refreshing()[row.cliType] ? '⏳' : '↻' }}
                          </button>
                        </td>
                      </tr>
                    } @else {
                      @for (w of row.windows; track w.label; let i = $index) {
                        <tr [class.uhp__row--stale]="row.stale"
                            [class.uhp__row--err]="!!row.error"
                            [attr.data-testid]="i === 0 ? ('hquota-modal-cli-' + row.cliType) : null">
                          @if (i === 0) {
                            <td [attr.rowspan]="row.windows.length">
                              <span class="uhp__icon">{{ row.icon }}</span>
                              <strong>{{ row.label }}</strong>
                            </td>
                            <td [attr.rowspan]="row.windows.length">{{ row.plan ?? '—' }}</td>
                          }
                          <td class="uhp__num">
                            <span class="uhp__pct"
                                  [attr.data-tone]="toneFor(w.usedPct)">
                              {{ w.usedPct === null ? '?' : (w.usedPct + '%') }}
                            </span>
                            @if (w.used !== null && w.limit !== null) {
                              <span class="uhp__sub">{{ w.used }} / {{ w.limit }}{{ w.unit ? ' ' + w.unit : '' }}</span>
                            }
                          </td>
                          <td>{{ w.label }}</td>
                          <td>{{ w.resetLabel ?? '—' }}</td>
                          @if (i === 0) {
                            <td [attr.rowspan]="row.windows.length"
                                [class.uhp__stale]="row.stale">
                              {{ row.freshness }}
                              @if (row.source) { <span class="uhp__sub-block">via {{ row.source }}</span> }
                            </td>
                            <td [attr.rowspan]="row.windows.length">
                              <button type="button"
                                      class="uhp__refresh-cli"
                                      [disabled]="refreshing()[row.cliType]"
                                      (click)="refreshOne(row.cliType, $event)"
                                      [attr.data-testid]="'usage-hover-panel-refresh-' + row.cliType"
                                      [title]="'Re-probe ' + row.label">
                                {{ refreshing()[row.cliType] ? '⏳' : '↻' }}
                              </button>
                            </td>
                          }
                        </tr>
                      }
                    }
                  }
                </tbody>
              </table>
            }
          </section>

          <section class="uhp__sec uhp__sec--tokens" data-testid="hquota-modal-tokens">
            <h4 class="uhp__sec-title">
              Tokens consumed
              @if (tokens(); as t) {
                <span class="uhp__sec-sub">
                  {{ t.orchestratorLlmCalls }} orchestrator call{{ t.orchestratorLlmCalls === 1 ? '' : 's' }}
                  · {{ t.projects }} project{{ t.projects === 1 ? '' : 's' }}
                </span>
              }
            </h4>

            @if (tokens(); as t) {
              <div class="uhp__tot">
                <div class="uhp__tot-cell">
                  <span class="uhp__tot-num">↑ {{ formatTokens(t.totalInputTokens) }}</span>
                  <span class="uhp__tot-lbl">input</span>
                </div>
                <div class="uhp__tot-cell">
                  <span class="uhp__tot-num">↓ {{ formatTokens(t.totalOutputTokens) }}</span>
                  <span class="uhp__tot-lbl">output</span>
                </div>
                @if (t.totalCacheReadTokens > 0) {
                  <div class="uhp__tot-cell">
                    <span class="uhp__tot-num">⚡ {{ formatTokens(t.totalCacheReadTokens) }}</span>
                    <span class="uhp__tot-lbl">cache read</span>
                  </div>
                }
                @if (t.totalCacheCreationTokens > 0) {
                  <div class="uhp__tot-cell">
                    <span class="uhp__tot-num">+ {{ formatTokens(t.totalCacheCreationTokens) }}</span>
                    <span class="uhp__tot-lbl">cache write</span>
                  </div>
                }
                <div class="uhp__tot-cell uhp__tot-cell--cost">
                  <span class="uhp__tot-num">{{ formatUsd(t.estimatedApiCostUsd) }}</span>
                  <span class="uhp__tot-lbl">theoretical API cost</span>
                </div>
              </div>

              @if (t.byModel.length > 0) {
                <details class="uhp__det">
                  <summary>Per-model breakdown</summary>
                  <table class="uhp__btab">
                    <thead>
                      <tr>
                        <th>Model</th>
                        <th class="uhp__num">Calls</th>
                        <th class="uhp__num">Input</th>
                        <th class="uhp__num">Output</th>
                        <th class="uhp__num">API cost</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (m of t.byModel; track m.model) {
                        <tr>
                          <td><code>{{ m.model }}</code></td>
                          <td class="uhp__num">{{ m.calls }}</td>
                          <td class="uhp__num">{{ formatTokens(m.inputTokens) }}</td>
                          <td class="uhp__num">{{ formatTokens(m.outputTokens) }}</td>
                          <td class="uhp__num">
                            @if (m.modelPriced) { {{ formatUsd(m.estimatedApiCostUsd) }} }
                            @else { <span class="uhp__na">n/a</span> }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </details>
              }

              @if (t.byProject.length > 0) {
                <details class="uhp__det">
                  <summary>Per-project breakdown</summary>
                  <table class="uhp__btab">
                    <thead>
                      <tr>
                        <th>Project</th>
                        <th class="uhp__num">Calls</th>
                        <th class="uhp__num">Input</th>
                        <th class="uhp__num">Output</th>
                        <th class="uhp__num">API cost</th>
                      </tr>
                    </thead>
                    <tbody>
                      @for (p of t.byProject; track p.project) {
                        <tr>
                          <td>{{ p.project }}</td>
                          <td class="uhp__num">{{ p.orchestratorLlmCalls }}</td>
                          <td class="uhp__num">{{ formatTokens(p.inputTokens) }}</td>
                          <td class="uhp__num">{{ formatTokens(p.outputTokens) }}</td>
                          <td class="uhp__num">{{ formatUsd(p.estimatedApiCostUsd) }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </details>
              }

              <p class="uhp__disc">{{ t.disclaimer }}</p>
            } @else {
              <p class="uhp__empty">No orchestrator activity recorded yet.</p>
            }
          </section>

          <section class="uhp__sec uhp__sec--adhoc" data-testid="hquota-modal-adhoc">
            <h4 class="uhp__sec-title">
              Ad-hoc CLI usage
              @if (adhoc(); as a) {
                <span class="uhp__sec-sub">
                  {{ a.calls }} call{{ a.calls === 1 ? '' : 's' }} · title-generate, summary, enhance, commit-msg, soft-reasoning, review-decision
                </span>
              }
            </h4>

            @if (adhoc(); as a) {
              @if (a.calls === 0) {
                <p class="uhp__empty">No ad-hoc Haiku calls recorded yet.</p>
              } @else {
                <div class="uhp__tot">
                  <div class="uhp__tot-cell">
                    <span class="uhp__tot-num">↑ {{ formatTokens(a.inputTokens) }}</span>
                    <span class="uhp__tot-lbl">input</span>
                  </div>
                  <div class="uhp__tot-cell">
                    <span class="uhp__tot-num">↓ {{ formatTokens(a.outputTokens) }}</span>
                    <span class="uhp__tot-lbl">output</span>
                  </div>
                  <div class="uhp__tot-cell uhp__tot-cell--cost">
                    <span class="uhp__tot-num">{{ formatUsd(a.estimatedApiCostUsd) }}</span>
                    <span class="uhp__tot-lbl">theoretical API cost</span>
                  </div>
                </div>

                @if (a.bySource.length > 0) {
                  <details class="uhp__det" open>
                    <summary>Per-source breakdown</summary>
                    <table class="uhp__btab" data-testid="adhoc-by-source">
                      <thead>
                        <tr>
                          <th>Source</th>
                          <th class="uhp__num">Calls</th>
                          <th class="uhp__num">Input</th>
                          <th class="uhp__num">Output</th>
                          <th class="uhp__num">API cost</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (s of a.bySource; track s.source) {
                          <tr>
                            <td><code>{{ s.source }}</code></td>
                            <td class="uhp__num">{{ s.calls }}</td>
                            <td class="uhp__num">{{ formatTokens(s.inputTokens) }}</td>
                            <td class="uhp__num">{{ formatTokens(s.outputTokens) }}</td>
                            <td class="uhp__num">{{ formatUsd(s.estimatedApiCostUsd) }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </details>
                }

                @if (a.byDay.length > 0) {
                  <details class="uhp__det">
                    <summary>Per-day breakdown</summary>
                    <table class="uhp__btab" data-testid="adhoc-by-day">
                      <thead>
                        <tr>
                          <th>Date (UTC)</th>
                          <th class="uhp__num">Calls</th>
                          <th class="uhp__num">Input</th>
                          <th class="uhp__num">Output</th>
                          <th class="uhp__num">API cost</th>
                        </tr>
                      </thead>
                      <tbody>
                        @for (d of a.byDay; track d.date) {
                          <tr>
                            <td>{{ d.date }}</td>
                            <td class="uhp__num">{{ d.calls }}</td>
                            <td class="uhp__num">{{ formatTokens(d.inputTokens) }}</td>
                            <td class="uhp__num">{{ formatTokens(d.outputTokens) }}</td>
                            <td class="uhp__num">{{ formatUsd(d.estimatedApiCostUsd) }}</td>
                          </tr>
                        }
                      </tbody>
                    </table>
                  </details>
                }

                <p class="uhp__adhoc-log">
                  Log:
                  <code class="uhp__adhoc-path" [title]="a.logPath">{{ a.logPath }}</code>
                  @if (a.logSizeBytes > 0) {
                    <span class="uhp__sub-block">{{ formatBytes(a.logSizeBytes) }}, last write {{ formatLogModified(a.logModifiedAt) }}</span>
                  }
                </p>
              }
            } @else {
              <p class="uhp__empty">Ad-hoc usage data unavailable.</p>
            }
          </section>
        </div>
      }
    </div>
  `,
  styles: [`
    :host { display: inline-flex; }
    .uhp { position: relative; display: inline-flex; }

    .uhp__pop {
      position: absolute;
      bottom: calc(100% + 8px);
      left: 50%;
      transform: translateX(-50%);
      width: min(720px, 92vw);
      max-height: 72vh;
      overflow-y: auto;
      background: #1e1e2e;
      border: 1px solid rgba(196, 181, 253, 0.45);
      border-radius: 14px;
      box-shadow: 0 12px 40px rgba(0, 0, 0, 0.65);
      padding: 14px 16px 16px;
      color: #cdd6f4;
      z-index: 200;
      font-size: 13px;
      letter-spacing: 0.01em;
      animation: uhp-fade 0.12s ease-out;
    }
    @keyframes uhp-fade {
      from { opacity: 0; transform: translate(-50%, 4px); }
      to   { opacity: 1; transform: translate(-50%, 0); }
    }

    .uhp__head {
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 10px;
      padding-bottom: 8px;
      border-bottom: 1px solid rgba(255,255,255,0.08);
    }
    .uhp__title {
      margin: 0;
      font-size: 14px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: #f8fafc;
    }
    .uhp__timeline-link {
      margin-left: auto;
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.16);
      color: #cdd6f4;
      border-radius: 8px;
      padding: 4px 10px;
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
      text-decoration: none;
    }
    .uhp__timeline-link:hover {
      background: rgba(255,255,255,0.10);
      border-color: rgba(255,255,255,0.26);
      color: #f8fafc;
    }
    .uhp__refresh {
      background: rgba(124,58,237,0.30);
      border: 1px solid rgba(196,181,253,0.45);
      color: #f8fafc;
      border-radius: 8px;
      padding: 4px 12px;
      font-size: 12px;
      font-weight: 600;
      cursor: pointer;
    }
    .uhp__refresh:hover:not(:disabled) {
      background: rgba(124,58,237,0.45);
      border-color: rgba(196,181,253,0.65);
    }
    .uhp__refresh:disabled { opacity: 0.6; cursor: progress; }

    .uhp__sec {
      margin-bottom: 14px;
    }
    .uhp__sec:last-child { margin-bottom: 0; }
    .uhp__sec-title {
      margin: 0 0 6px;
      font-size: 11px;
      letter-spacing: 0.10em;
      text-transform: uppercase;
      color: rgba(255,255,255,0.55);
      font-weight: 700;
    }
    .uhp__sec-sub {
      font-weight: 500;
      letter-spacing: normal;
      text-transform: none;
      color: rgba(255,255,255,0.55);
      margin-left: 8px;
      font-size: 11px;
    }
    .uhp__empty {
      margin: 0;
      color: rgba(255,255,255,0.55);
      font-style: italic;
    }

    .uhp__qtab, .uhp__btab {
      width: 100%;
      border-collapse: collapse;
      font-size: 12px;
    }
    .uhp__qtab th, .uhp__qtab td,
    .uhp__btab th, .uhp__btab td {
      padding: 5px 8px;
      text-align: left;
      vertical-align: top;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .uhp__qtab th, .uhp__btab th {
      color: rgba(255,255,255,0.50);
      font-size: 10px;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: 0.06em;
      border-bottom: 1px solid rgba(255,255,255,0.10);
    }
    .uhp__num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
    .uhp__icon { margin-right: 4px; }
    .uhp__sub {
      display: block;
      color: rgba(255,255,255,0.50);
      font-size: 11px;
    }
    .uhp__sub-block { display: block; color: rgba(255,255,255,0.50); font-size: 11px; }
    .uhp__pct {
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }
    .uhp__pct[data-tone="ok"]   { color: #86efac; }
    .uhp__pct[data-tone="warn"] { color: #fcd34d; }
    .uhp__pct[data-tone="hot"]  { color: #fda4af; }
    .uhp__pct[data-tone="unknown"] { color: rgba(255,255,255,0.55); }

    .uhp__row--stale td { color: rgba(252, 211, 77, 0.95); }
    .uhp__row--err   td { color: rgba(253, 164, 175, 0.95); }
    .uhp__stale { color: #fcd34d; font-weight: 600; }

    .uhp__refresh-cli {
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 6px;
      padding: 2px 8px;
      cursor: pointer;
      font-size: 12px;
    }
    .uhp__refresh-cli:hover:not(:disabled) { background: rgba(255,255,255,0.14); }
    .uhp__refresh-cli:disabled { opacity: 0.6; cursor: progress; }

    .uhp__tot {
      display: flex;
      flex-wrap: wrap;
      gap: 18px;
      padding: 10px 12px;
      background: rgba(99, 102, 241, 0.10);
      border: 1px solid rgba(99, 102, 241, 0.25);
      border-radius: 10px;
      margin-bottom: 8px;
    }
    .uhp__tot-cell {
      display: flex;
      flex-direction: column;
      align-items: flex-start;
    }
    .uhp__tot-num {
      font-size: 18px;
      font-weight: 800;
      color: #f8fafc;
      font-variant-numeric: tabular-nums;
      line-height: 1.1;
    }
    .uhp__tot-cell--cost .uhp__tot-num { color: #fcd34d; }
    .uhp__tot-lbl {
      font-size: 10px;
      color: rgba(255,255,255,0.55);
      text-transform: uppercase;
      letter-spacing: 0.08em;
    }

    .uhp__det { margin-bottom: 6px; }
    .uhp__det summary {
      cursor: pointer;
      color: rgba(255,255,255,0.65);
      font-size: 12px;
      padding: 2px 0;
      user-select: none;
    }
    .uhp__det summary:hover { color: #f8fafc; }
    .uhp__det[open] summary { margin-bottom: 4px; }

    .uhp__btab td code {
      background: rgba(255,255,255,0.06);
      padding: 1px 6px;
      border-radius: 4px;
      font-size: 11px;
    }
    .uhp__na { color: rgba(255,255,255,0.40); font-style: italic; }

    .uhp__adhoc-log {
      margin: 8px 0 0;
      font-size: 11px;
      color: rgba(255,255,255,0.55);
      word-break: break-all;
    }
    .uhp__adhoc-path {
      font-family: ui-monospace, "SF Mono", Consolas, monospace;
      font-size: 10px;
      color: rgba(205, 214, 244, 0.85);
      background: rgba(255,255,255,0.06);
      padding: 1px 5px;
      border-radius: 4px;
    }

    .uhp__disc {
      margin: 6px 0 0;
      padding: 6px 10px;
      font-size: 11px;
      font-style: italic;
      color: rgba(255,255,255,0.55);
      background: rgba(249, 226, 175, 0.05);
      border-left: 2px solid rgba(249, 226, 175, 0.40);
      border-radius: 4px;
      line-height: 1.5;
    }
  `]
})
export class UsageHoverPanelComponent implements OnInit, OnDestroy {
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
    this.jobService.getQuotaReport().subscribe({
      next: (r) => this.report.set(r),
      error: () => { /* keep last value */ },
    });
  }

  fetchTokensCached(): void {
    this.jobService.getTokenSummaryAggregateCached().subscribe({
      next: (resp) => {
        if (resp.status === 200 && resp.body) this.tokens.set(resp.body);
      },
      error: () => { /* tolerated */ },
    });
  }

  fetchTokensFresh(): void {
    this.jobService.getTokenSummaryAggregate().subscribe({
      next: (a) => this.tokens.set(a),
      error: () => { /* keep last value */ },
    });
  }

  fetchAdHoc(): void {
    this.jobService.getAdHocUsage().subscribe({
      next: (a) => this.adhoc.set(a),
      error: () => { /* keep last value */ },
    });
  }

  refreshAll(ev: Event): void {
    ev.stopPropagation();
    if (this.refreshingAll()) return;
    this.refreshingAll.set(true);
    this.jobService.refreshQuotaAll().subscribe({
      next: () => { this.fetchQuota(); this.refreshingAll.set(false); },
      error: () => this.refreshingAll.set(false),
    });
    this.fetchTokensFresh();
  }

  refreshOne(cliType: CliType, ev: Event): void {
    ev.stopPropagation();
    if (this.refreshing()[cliType]) return;
    this.refreshing.update(m => ({ ...m, [cliType]: true }));
    this.jobService.refreshQuotaForCli(cliType).subscribe({
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
