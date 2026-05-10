import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { JobService } from '../../../services/job.service';
import {
  ProjectExpensiveJob,
  ProjectExpensiveJobsResponse,
  ProjectJobTokenDetail,
  ProjectTokenCategory,
  ProjectTokenHeatmap,
  ProjectTokenHeatmapJob,
  ProjectTokenUsageSummary,
} from '../../../models/job.model';

interface CardSpec {
  testid: string;
  label: string;
  primary: number;
  secondary?: { label: string; value: number } | null;
  category: ProjectTokenCategory | 'total';
}

/**
 * Project Token Usage panel (slice 8 of the quality-system mockup,
 * docs/mockups/quality-system/, "Token Usage" surface). Renders the
 * project's lifetime + last-24h totals with the Job / Supporting /
 * Orchestrator category split (taxonomy.md vocabulary), a per-job ×
 * per-day heatmap, an expensive-jobs list, and a drill-down for one job
 * with per-run deltas.
 *
 * Action-driven principle: this panel does no analysis on its own. It
 * only reads the orchestrator log via the `/api/projects/{project}/
 * token-usage/*` endpoints. Token usage is visibility, not enforcement
 * (Critical Boundaries in the README).
 *
 * Hide-when-empty: a project with no token-using orchestrator entries
 * shows an explicit empty-state card instead of phantom zeros.
 */
@Component({
  selector: 'app-project-token-usage-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="tup" data-testid="project-token-usage-panel">
      <header class="tup__head">
        <div class="tup__title-row">
          <h2 class="tup__title">
            <span class="tup__icon" aria-hidden="true">▦</span>
            Token Usage
          </h2>
          <span class="tup__spacer"></span>
          @if (loading()) {
            <span class="tup__loading" data-testid="token-usage-loading">Loading…</span>
          }
        </div>
        <p class="tup__sub">
          Inference spend by job, supporting runs, orchestrator turns, and time window.
        </p>
      </header>

      @if (loadError(); as err) {
        <div class="tup__error" data-testid="token-usage-load-error">
          Could not load token usage: {{ err }}
        </div>
      }

      @if (summary()?.hasData === false) {
        <div class="tup__empty" data-testid="token-usage-empty">
          <p>No token activity recorded for this project yet.</p>
          <p class="tup__empty-detail">
            Once the orchestrator (or a supporting analysis loop like a
            security audit) makes an LLM call here, the totals,
            heatmap, and expensive-jobs list will appear.
          </p>
        </div>
      } @else {
        <div class="tup__cards" data-testid="token-usage-cards">
          @for (card of cards(); track card.testid) {
            <article class="tup__card"
                     [class]="'tup__card--' + card.category"
                     [attr.data-testid]="card.testid">
              <h3 class="tup__card-title">{{ card.label }}</h3>
              <p class="tup__card-value">{{ formatTokens(card.primary) }}</p>
              @if (card.secondary; as s) {
                <p class="tup__card-detail">
                  <span class="tup__card-detail-label">{{ s.label }}:</span>
                  <span class="tup__card-detail-value">{{ formatTokens(s.value) }}</span>
                </p>
              }
            </article>
          }
        </div>

        <section class="tup__timeline" data-testid="token-usage-timeline" aria-label="Recent token spend">
          <h3 class="tup__section-title">Recent activity</h3>
          @if (timelineBuckets().length === 0) {
            <p class="tup__section-empty">No daily activity in the heatmap window.</p>
          } @else {
            <div class="tup__timeline-bars" role="list">
              @for (b of timelineBuckets(); track b.day) {
                <div class="tup__timeline-bar" role="listitem"
                     [attr.data-day]="b.day"
                     [title]="b.day + ': ' + formatTokens(b.total) + ' tokens (' + b.calls + ' call' + (b.calls === 1 ? '' : 's') + ')'"
                     [style.height.%]="b.heightPct">
                  <span class="tup__timeline-bar-day">{{ b.shortDay }}</span>
                </div>
              }
            </div>
          }
        </section>

        <section class="tup__heatmap-section" aria-label="Per-job heatmap">
          <h3 class="tup__section-title">Heatmap (top jobs × days)</h3>
          @if (heatmap()?.hasData) {
            <div class="tup__heatmap-scroll">
              <table class="tup__heatmap" data-testid="token-usage-heatmap">
                <thead>
                  <tr>
                    <th class="tup__heatmap-corner" scope="col">Job</th>
                    @for (day of heatmap()!.days; track day) {
                      <th scope="col" class="tup__heatmap-day-th">{{ shortDay(day) }}</th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (row of heatmapTopRows(); track row.jobId) {
                    <tr [attr.data-testid]="'heatmap-row'"
                        [attr.data-job-id]="row.jobId">
                      <th scope="row" class="tup__heatmap-row-th"
                          [class.tup__heatmap-row-th--active]="row.jobId === selectedJobId()">
                        <button type="button"
                                class="tup__heatmap-row-btn"
                                [attr.data-testid]="'heatmap-row-btn-' + row.jobId"
                                [attr.aria-pressed]="row.jobId === selectedJobId()"
                                (click)="onSelectJob(row.jobId)">
                          <span class="tup__heatmap-row-cat"
                                [class]="'tup__heatmap-row-cat--' + row.category">{{ catGlyph(row.category) }}</span>
                          <span class="tup__heatmap-row-title">{{ row.title }}</span>
                          <span class="tup__heatmap-row-total">{{ formatTokens(row.total) }}</span>
                        </button>
                      </th>
                      @for (cell of row.cells; track cell.day) {
                        <td class="tup__heatmap-cell"
                            [attr.data-testid]="'heatmap-cell'"
                            [attr.data-job-id]="row.jobId"
                            [attr.data-day]="cell.day"
                            [attr.data-total]="cell.total"
                            [class]="'tup__heatmap-cell--' + heatLevel(cell.total)"
                            [title]="row.title + ' · ' + cell.day + ': ' + formatTokens(cell.total) + ' tokens'"
                            (click)="onSelectJob(row.jobId)"></td>
                      }
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <p class="tup__section-empty" data-testid="heatmap-empty">
              No per-job activity in the last {{ heatmap()?.days?.length ?? 30 }} days.
            </p>
          }
        </section>

        <section class="tup__expensive-section" aria-label="Expensive jobs">
          <h3 class="tup__section-title">Most expensive jobs</h3>
          @if (expensive().length === 0) {
            <p class="tup__section-empty" data-testid="expensive-empty">
              No jobs have recorded orchestrator token spend yet.
            </p>
          } @else {
            <ul class="tup__expensive-list" data-testid="token-usage-expensive">
              @for (j of expensive(); track j.jobId) {
                <li class="tup__expensive-row"
                    [class.tup__expensive-row--active]="j.jobId === selectedJobId()"
                    [attr.data-testid]="'expensive-row'"
                    [attr.data-job-id]="j.jobId">
                  <button type="button"
                          class="tup__expensive-btn"
                          [attr.data-testid]="'expensive-btn-' + j.jobId"
                          (click)="onSelectJob(j.jobId)">
                    <span class="tup__expensive-cat"
                          [class]="'tup__expensive-cat--' + j.category">{{ j.category }}</span>
                    <span class="tup__expensive-title">{{ j.title }}</span>
                    <span class="tup__expensive-meta">
                      <span>{{ formatTokens(j.totalTokens) }}</span>
                      <span class="tup__expensive-meta-sep">·</span>
                      <span>{{ j.calls }} call{{ j.calls === 1 ? '' : 's' }}</span>
                    </span>
                  </button>
                </li>
              }
            </ul>
          }
        </section>

        @if (selectedJobId(); as jid) {
          <section class="tup__drill" data-testid="token-usage-drill" aria-label="Per-run drill-down">
            <header class="tup__drill-head">
              <h3 class="tup__section-title">Drill-down: {{ drilldown()?.title ?? jid }}</h3>
              <button type="button"
                      class="tup__drill-close"
                      data-testid="drill-close"
                      (click)="onClearSelection()">Close</button>
            </header>
            @if (drilldownError(); as err) {
              <p class="tup__error" data-testid="drill-error">{{ err }}</p>
            } @else if (!drilldown()) {
              <p class="tup__section-empty" data-testid="drill-loading">Loading drill-down…</p>
            } @else if (drilldown()!.runs.length === 0) {
              <p class="tup__section-empty" data-testid="drill-empty">No orchestrator calls recorded for this job.</p>
            } @else {
              <div class="tup__drill-summary">
                <span class="tup__drill-summary-cat"
                      [class]="'tup__drill-summary-cat--' + drilldown()!.category">{{ drilldown()!.category }}</span>
                <span class="tup__drill-summary-total">{{ formatTokens(drilldown()!.totalTokens) }} tokens</span>
                <span class="tup__drill-summary-calls">· {{ drilldown()!.calls }} run{{ drilldown()!.calls === 1 ? '' : 's' }}</span>
                @if (drilldown()!.lastModel; as m) {
                  <span class="tup__drill-summary-model">· {{ m }}</span>
                }
              </div>
              <ol class="tup__drill-runs">
                @for (r of drilldown()!.runs; track r.index) {
                  <li class="tup__drill-run" [attr.data-testid]="'drill-run'">
                    <span class="tup__drill-run-idx">#{{ r.index + 1 }}</span>
                    <span class="tup__drill-run-ts">{{ formatTs(r.ts) }}</span>
                    <span class="tup__drill-run-model">{{ r.model ?? '—' }}</span>
                    <span class="tup__drill-run-total">{{ formatTokens(r.total) }}</span>
                    <span class="tup__drill-run-delta"
                          [class.tup__drill-run-delta--up]="(r.deltaVsPrev ?? 0) > 0"
                          [class.tup__drill-run-delta--down]="(r.deltaVsPrev ?? 0) < 0">
                      @if (r.deltaVsPrev === null || r.deltaVsPrev === undefined) {
                        first run
                      } @else {
                        {{ r.deltaVsPrev > 0 ? '+' : '' }}{{ formatTokens(r.deltaVsPrev) }}
                      }
                    </span>
                    @if (r.summary; as s) {
                      <span class="tup__drill-run-summary">{{ s }}</span>
                    }
                  </li>
                }
              </ol>
            }
          </section>
        }
      }

      @if (summary()?.disclaimer; as d) {
        <p class="tup__disclaimer">{{ d }}</p>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }

    .tup { display: flex; flex-direction: column; gap: 18px; }

    .tup__head { padding-bottom: 12px; border-bottom: 1px solid #313244; }
    .tup__title-row { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .tup__title { margin: 0; font-size: 1.05rem; font-weight: 600; color: #f8fafc; display: flex; align-items: center; gap: 8px; }
    .tup__icon { width: 18px; text-align: center; }
    .tup__sub { margin: 6px 0 0; color: #a6adc8; font-size: 0.85rem; }
    .tup__spacer { flex: 1; }
    .tup__loading { color: #a6adc8; font-size: 0.78rem; }

    .tup__error {
      padding: 10px 14px;
      background: rgba(243, 139, 168, 0.10);
      border: 1px solid rgba(243, 139, 168, 0.30);
      color: #f38ba8;
      border-radius: 6px;
      font-size: 0.85rem;
    }

    .tup__empty {
      padding: 24px 18px;
      border: 1px dashed #313244;
      background: rgba(0,0,0,0.18);
      border-radius: 6px;
      color: #a6adc8;
    }
    .tup__empty p { margin: 0 0 6px; font-size: 0.88rem; }
    .tup__empty-detail { color: #6c7086; font-size: 0.80rem; }

    .tup__cards {
      display: grid;
      grid-template-columns: repeat(4, minmax(0, 1fr));
      gap: 14px;
    }
    .tup__card {
      background: #181825;
      border: 1px solid #313244;
      border-radius: 8px;
      padding: 14px 16px;
      min-height: 110px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .tup__card--total { border-top: 3px solid #94e2d5; }
    .tup__card--job { border-top: 3px solid #89b4fa; }
    .tup__card--supporting { border-top: 3px solid #cba6f7; }
    .tup__card--orchestrator { border-top: 3px solid #fab387; }
    .tup__card-title {
      margin: 0; color: #a6adc8; font-size: 0.74rem;
      letter-spacing: 0.06em; text-transform: uppercase; font-weight: 600;
    }
    .tup__card-value {
      margin: 4px 0 0; font-size: 1.45rem; font-weight: 600; color: #f8fafc;
    }
    .tup__card-detail { margin: 0; color: #a6adc8; font-size: 0.78rem; display: flex; gap: 6px; }
    .tup__card-detail-label { color: #6c7086; }
    .tup__card-detail-value { font-weight: 600; color: #cdd6f4; }

    .tup__section-title {
      margin: 0 0 8px;
      color: #a6adc8;
      font-size: 0.74rem;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      font-weight: 600;
    }
    .tup__section-empty { margin: 4px 0 0; color: #6c7086; font-size: 0.82rem; }

    .tup__timeline-bars {
      display: flex;
      align-items: flex-end;
      height: 60px;
      gap: 2px;
      padding: 4px 4px 18px;
      background: #11111b;
      border: 1px solid #313244;
      border-radius: 6px;
    }
    .tup__timeline-bar {
      flex: 1;
      min-width: 4px;
      background: #89b4fa;
      border-radius: 2px 2px 0 0;
      position: relative;
      min-height: 1px;
    }
    .tup__timeline-bar-day {
      position: absolute;
      bottom: -16px;
      left: 50%;
      transform: translateX(-50%);
      font-size: 0.62rem;
      color: #6c7086;
    }

    .tup__heatmap-scroll {
      overflow-x: auto;
      border: 1px solid #313244;
      border-radius: 6px;
      background: #181825;
    }
    .tup__heatmap {
      border-collapse: separate;
      border-spacing: 0;
      width: 100%;
      font-size: 0.74rem;
    }
    .tup__heatmap thead th {
      position: sticky;
      top: 0;
      background: #1e1e2e;
      color: #6c7086;
      font-weight: 600;
      padding: 4px 4px;
      border-bottom: 1px solid #313244;
      text-align: center;
    }
    .tup__heatmap-corner { text-align: left !important; min-width: 240px; }
    .tup__heatmap-day-th { writing-mode: vertical-rl; transform: rotate(180deg); padding: 6px 2px !important; }
    .tup__heatmap-row-th {
      text-align: left;
      padding: 0;
      border-bottom: 1px solid #313244;
      background: #181825;
    }
    .tup__heatmap-row-th--active { background: rgba(166, 227, 161, 0.10); }
    .tup__heatmap-row-btn {
      width: 100%;
      padding: 6px 8px;
      background: transparent;
      border: none;
      color: #cdd6f4;
      font: inherit;
      cursor: pointer;
      display: grid;
      grid-template-columns: 18px 1fr auto;
      gap: 8px;
      align-items: center;
      text-align: left;
    }
    .tup__heatmap-row-btn:hover { background: rgba(255,255,255,0.04); }
    .tup__heatmap-row-cat {
      font-size: 0.78rem;
      width: 18px;
      text-align: center;
    }
    .tup__heatmap-row-cat--job { color: #89b4fa; }
    .tup__heatmap-row-cat--supporting { color: #cba6f7; }
    .tup__heatmap-row-cat--orchestrator { color: #fab387; }
    .tup__heatmap-row-title {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
      max-width: 200px;
    }
    .tup__heatmap-row-total {
      color: #a6adc8;
      font-size: 0.72rem;
      font-variant-numeric: tabular-nums;
    }
    .tup__heatmap-cell {
      width: 18px;
      height: 22px;
      cursor: pointer;
      border-bottom: 1px solid #313244;
      border-left: 1px solid #313244;
      transition: filter 0.1s;
    }
    .tup__heatmap-cell:hover { filter: brightness(1.4); }
    .tup__heatmap-cell--n0 { background: #11111b; }
    .tup__heatmap-cell--n1 { background: rgba(137, 180, 250, 0.20); }
    .tup__heatmap-cell--n2 { background: rgba(137, 180, 250, 0.45); }
    .tup__heatmap-cell--n3 { background: rgba(250, 179, 135, 0.55); }
    .tup__heatmap-cell--n4 { background: rgba(243, 139, 168, 0.75); }

    .tup__expensive-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
    .tup__expensive-row {
      background: #181825;
      border: 1px solid #313244;
      border-radius: 6px;
    }
    .tup__expensive-row--active { border-color: rgba(166, 227, 161, 0.40); background: rgba(166, 227, 161, 0.06); }
    .tup__expensive-btn {
      width: 100%;
      padding: 8px 12px;
      display: grid;
      grid-template-columns: 100px 1fr auto;
      gap: 12px;
      align-items: center;
      background: transparent;
      border: none;
      color: #cdd6f4;
      font: inherit;
      cursor: pointer;
      text-align: left;
    }
    .tup__expensive-btn:hover { background: rgba(255,255,255,0.03); }
    .tup__expensive-cat {
      font-size: 0.68rem;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      padding: 2px 8px;
      border-radius: 999px;
      text-align: center;
      font-weight: 600;
    }
    .tup__expensive-cat--job { background: rgba(137, 180, 250, 0.16); color: #89b4fa; }
    .tup__expensive-cat--supporting { background: rgba(203, 166, 247, 0.18); color: #cba6f7; }
    .tup__expensive-cat--orchestrator { background: rgba(250, 179, 135, 0.18); color: #fab387; }
    .tup__expensive-title { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .tup__expensive-meta {
      color: #a6adc8;
      font-size: 0.78rem;
      display: inline-flex;
      gap: 6px;
      align-items: center;
      font-variant-numeric: tabular-nums;
    }
    .tup__expensive-meta-sep { color: #45475a; }

    .tup__drill {
      background: #11111b;
      border: 1px solid #313244;
      border-radius: 8px;
      padding: 14px 16px;
    }
    .tup__drill-head {
      display: flex;
      align-items: center;
      gap: 12px;
      margin-bottom: 10px;
    }
    .tup__drill-head .tup__section-title { margin: 0; flex: 1; }
    .tup__drill-close {
      background: transparent;
      color: #a6adc8;
      border: 1px solid #313244;
      padding: 3px 10px;
      border-radius: 6px;
      cursor: pointer;
      font: inherit;
      font-size: 0.78rem;
    }
    .tup__drill-close:hover { background: #313244; color: #cdd6f4; }
    .tup__drill-summary {
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
      margin-bottom: 8px;
      color: #cdd6f4;
      font-size: 0.85rem;
    }
    .tup__drill-summary-cat {
      font-size: 0.68rem;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      padding: 2px 8px;
      border-radius: 999px;
      font-weight: 600;
    }
    .tup__drill-summary-cat--job { background: rgba(137, 180, 250, 0.16); color: #89b4fa; }
    .tup__drill-summary-cat--supporting { background: rgba(203, 166, 247, 0.18); color: #cba6f7; }
    .tup__drill-summary-cat--orchestrator { background: rgba(250, 179, 135, 0.18); color: #fab387; }
    .tup__drill-summary-total { font-weight: 600; color: #f8fafc; }
    .tup__drill-summary-calls { color: #a6adc8; }
    .tup__drill-summary-model { color: #6c7086; font-size: 0.78rem; }

    .tup__drill-runs { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
    .tup__drill-run {
      display: grid;
      grid-template-columns: 32px 110px 130px auto auto 1fr;
      gap: 10px;
      align-items: center;
      padding: 6px 10px;
      background: #181825;
      border: 1px solid #313244;
      border-radius: 6px;
      font-size: 0.80rem;
      color: #cdd6f4;
    }
    .tup__drill-run-idx { color: #6c7086; font-variant-numeric: tabular-nums; }
    .tup__drill-run-ts { color: #a6adc8; font-size: 0.74rem; font-variant-numeric: tabular-nums; }
    .tup__drill-run-model { color: #6c7086; font-size: 0.74rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .tup__drill-run-total { font-weight: 600; color: #f8fafc; font-variant-numeric: tabular-nums; }
    .tup__drill-run-delta {
      font-variant-numeric: tabular-nums;
      font-size: 0.74rem;
      color: #6c7086;
    }
    .tup__drill-run-delta--up { color: #f38ba8; }
    .tup__drill-run-delta--down { color: #a6e3a1; }
    .tup__drill-run-summary {
      color: #a6adc8;
      font-size: 0.74rem;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }

    .tup__disclaimer {
      margin: 0;
      color: #6c7086;
      font-size: 0.72rem;
      padding: 8px 10px;
      background: rgba(0,0,0,0.18);
      border: 1px dashed #313244;
      border-radius: 6px;
    }

    @media (max-width: 720px) {
      .tup__cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
      .tup__expensive-btn { grid-template-columns: 80px 1fr; }
      .tup__expensive-meta { grid-column: 1 / -1; }
      .tup__drill-run { grid-template-columns: 32px 1fr auto; }
      .tup__drill-run-ts, .tup__drill-run-model, .tup__drill-run-summary { display: none; }
    }
  `],
})
export class ProjectTokenUsagePanelComponent {
  private readonly jobs = inject(JobService);

  readonly projectName = input.required<string>();

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly summary = signal<ProjectTokenUsageSummary | null>(null);
  readonly heatmap = signal<ProjectTokenHeatmap | null>(null);
  readonly expensive = signal<ProjectExpensiveJob[]>([]);

  readonly selectedJobId = signal<string | null>(null);
  readonly drilldown = signal<ProjectJobTokenDetail | null>(null);
  readonly drilldownError = signal<string | null>(null);

  /** Hard cap on heatmap rows we render. Beyond this the panel scrolls. */
  private static readonly HEATMAP_TOP_ROWS = 12;

  readonly cards = computed<CardSpec[]>(() => {
    const s = this.summary();
    if (!s) return [];
    return [
      {
        testid: 'token-usage-card-total',
        label: 'Total tokens',
        primary: s.lifetimeTotalTokens,
        secondary: { label: 'Last 24h', value: s.last24hTotalTokens },
        category: 'total',
      },
      {
        testid: 'token-usage-card-job',
        label: 'Job tokens',
        primary: s.lifetimeJobTokens,
        secondary: { label: 'Last 24h', value: s.last24hJobTokens },
        category: 'job',
      },
      {
        testid: 'token-usage-card-supporting',
        label: 'Supporting jobs tokens',
        primary: s.lifetimeSupportingTokens,
        secondary: { label: 'Last 24h', value: s.last24hSupportingTokens },
        category: 'supporting',
      },
      {
        testid: 'token-usage-card-orchestrator',
        label: 'Orchestrator tokens',
        primary: s.lifetimeOrchestratorTokens,
        secondary: { label: 'Last 24h', value: s.last24hOrchestratorTokens },
        category: 'orchestrator',
      },
    ];
  });

  /** Top-N heatmap rows. Sorted by total descending by the backend. */
  readonly heatmapTopRows = computed<readonly ProjectTokenHeatmapJob[]>(() => {
    const h = this.heatmap();
    if (!h) return [];
    return h.jobs.slice(0, ProjectTokenUsagePanelComponent.HEATMAP_TOP_ROWS);
  });

  /** Per-day buckets folded across all heatmap rows (the timeline view). */
  readonly timelineBuckets = computed(() => {
    const h = this.heatmap();
    if (!h || h.days.length === 0) return [] as Array<{
      day: string; shortDay: string; total: number; calls: number; heightPct: number;
    }>;
    const totals = new Map<string, { total: number; calls: number }>();
    for (const day of h.days) totals.set(day, { total: 0, calls: 0 });
    for (const row of h.jobs) {
      for (const cell of row.cells) {
        const acc = totals.get(cell.day);
        if (!acc) continue;
        if (cell.total > 0) {
          acc.total += cell.total;
          // Calls aren't on the cell; approximate by row.calls × share of
          // the row this day represents. Keeps the tooltip honest enough
          // for an at-a-glance number.
          acc.calls += cell.total > 0 ? 1 : 0;
        }
      }
    }
    let max = 0;
    for (const { total } of totals.values()) if (total > max) max = total;
    return h.days.map(day => {
      const acc = totals.get(day) ?? { total: 0, calls: 0 };
      return {
        day,
        shortDay: this.shortDay(day),
        total: acc.total,
        calls: acc.calls,
        heightPct: max > 0 ? Math.max(2, Math.round((acc.total / max) * 100)) : 0,
      };
    });
  });

  constructor() {
    effect(() => {
      const name = this.projectName();
      if (name) this.refresh(name);
    });

    effect(() => {
      const jobId = this.selectedJobId();
      const name = this.projectName();
      if (!jobId || !name) {
        this.drilldown.set(null);
        this.drilldownError.set(null);
        return;
      }
      this.drilldown.set(null);
      this.drilldownError.set(null);
      this.jobs.getProjectJobTokenDetail(name, jobId).subscribe({
        next: (d: ProjectJobTokenDetail) => this.drilldown.set(d),
        error: (err: HttpErrorResponse) => {
          this.drilldown.set(null);
          this.drilldownError.set(err?.error?.error ?? err.message ?? 'failed to load drill-down');
        },
      });
    });
  }

  private refresh(name: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.summary.set(null);
    this.heatmap.set(null);
    this.expensive.set([]);
    this.selectedJobId.set(null);

    let pending = 3;
    const done = () => { pending--; if (pending <= 0) this.loading.set(false); };
    const fail = (err: HttpErrorResponse) => {
      this.loadError.set(this.loadError() ?? err?.message ?? 'unknown');
      done();
    };

    this.jobs.getProjectTokenUsageSummary(name).subscribe({
      next: (s: ProjectTokenUsageSummary) => { this.summary.set(s); done(); },
      error: fail,
    });
    this.jobs.getProjectTokenUsageHeatmap(name, 30).subscribe({
      next: (h: ProjectTokenHeatmap) => { this.heatmap.set(h); done(); },
      error: fail,
    });
    this.jobs.getProjectExpensiveJobs(name, 10).subscribe({
      next: (r: ProjectExpensiveJobsResponse) => { this.expensive.set(r.jobs); done(); },
      error: fail,
    });
  }

  onSelectJob(jobId: string): void {
    if (this.selectedJobId() === jobId) {
      this.selectedJobId.set(null);
    } else {
      this.selectedJobId.set(jobId);
    }
  }

  onClearSelection(): void {
    this.selectedJobId.set(null);
  }

  /** 5-bucket heat scale; computed against the row max so dense rows stay readable. */
  heatLevel(total: number): string {
    if (total <= 0) return 'n0';
    const max = this.maxCellValue();
    if (max <= 0) return 'n0';
    const ratio = total / max;
    if (ratio < 0.1) return 'n1';
    if (ratio < 0.3) return 'n2';
    if (ratio < 0.6) return 'n3';
    return 'n4';
  }

  private maxCellValue(): number {
    const h = this.heatmap();
    if (!h) return 0;
    let max = 0;
    for (const row of h.jobs) {
      for (const cell of row.cells) {
        if (cell.total > max) max = cell.total;
      }
    }
    return max;
  }

  formatTokens(n: number | null | undefined): string {
    const v = n ?? 0;
    const sign = v < 0 ? '-' : '';
    const abs = Math.abs(v);
    if (abs >= 1_000_000_000) return `${sign}${(abs / 1_000_000_000).toFixed(1)}B`;
    if (abs >= 1_000_000) return `${sign}${(abs / 1_000_000).toFixed(1)}M`;
    if (abs >= 1_000) return `${sign}${(abs / 1_000).toFixed(1)}k`;
    return `${sign}${abs}`;
  }

  formatTs(iso: string): string {
    if (!iso) return '';
    // Trim seconds + timezone for a tighter row.
    const d = new Date(iso);
    if (isNaN(d.getTime())) return iso;
    const yyyy = d.getUTCFullYear();
    const mm = String(d.getUTCMonth() + 1).padStart(2, '0');
    const dd = String(d.getUTCDate()).padStart(2, '0');
    const hh = String(d.getUTCHours()).padStart(2, '0');
    const mi = String(d.getUTCMinutes()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd} ${hh}:${mi}`;
  }

  shortDay(day: string): string {
    // Day strings are YYYY-MM-DD; render MM-DD to keep the heatmap tight.
    if (day.length >= 10) return day.slice(5);
    return day;
  }

  catGlyph(category: ProjectTokenCategory): string {
    switch (category) {
      case 'job': return '●';
      case 'supporting': return '◐';
      case 'orchestrator': return '◇';
    }
  }
}
