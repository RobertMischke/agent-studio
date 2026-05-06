import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AgentBusService, AgentBusFixture } from '../../services/agent-bus.service';
import {
  AGENT_MESSAGE_KINDS,
  AGENT_MESSAGE_SEVERITIES,
  AgentMessage,
  AgentMessageQuery,
  AgentMessageSummary,
} from '../../models/agent-bus.model';

interface FilterState {
  participantId: string;
  kind: string;
  severity: string;
  jobId: string;
  runId: string;
  cli: string;
  skill: string;
  rangeHours: number;
}

interface CountChip {
  testid: string;
  label: string;
  value: number;
  tone: 'neutral' | 'warn' | 'high';
}

interface MatrixRow {
  participantId: string;
  total: number;
  kindCounts: Record<string, number>;
  jobs: ReadonlySet<string>;
}

interface TimelineLane {
  participantId: string;
  marks: ReadonlyArray<{ id: string; offsetPct: number; kind: string; severity: string | null; title: string }>;
}

interface HeatmapCell {
  participantId: string;
  bucket: number;
  tokens: number;
  msgIds: string[];
}

const HEATMAP_BUCKETS = 12;
const SILENT_GAP_THRESHOLD_MS = 1000 * 60 * 30; // a 30-minute gap counts as a silent period

const DEFAULT_FILTER: FilterState = {
  participantId: '',
  kind: '',
  severity: '',
  jobId: '',
  runId: '',
  cli: '',
  skill: '',
  rangeHours: 24,
};

const RANGE_OPTIONS: ReadonlyArray<{ id: number; label: string }> = [
  { id: 1, label: 'Last 1h' },
  { id: 6, label: 'Last 6h' },
  { id: 24, label: 'Last 24h' },
  { id: 24 * 7, label: 'Last 7d' },
  { id: 0, label: 'All time' },
];

/**
 * Project Observability panel: renders the Agent Message Bus as a dense,
 * scannable operations surface. Reads `/api/bus/{project}/...` via
 * {@link AgentBusService}; when the backend has not yet projected any
 * messages for a project, a fixture dataset (toggled per-panel) keeps the
 * surfaces useful in development and Playwright runs.
 *
 * Surfaces shipped here, all addressable through the filter strip at the
 * top:
 *  - Counter chips for total, intervention, error, severity-Warn,
 *    severity-High, token messages and silent-period count.
 *  - A per-participant timeline (rows = participants, columns = wall
 *    clock) with kind/severity glyphs.
 *  - A participant × kind matrix with reference counts.
 *  - A token-usage heatmap (participant × time bucket).
 *  - The filtered message table with raw-JSON drilldown.
 */
@Component({
  selector: 'app-project-observability-panel',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="obs" data-testid="project-observability-panel">
      <header class="obs__head">
        <div class="obs__title-row">
          <h2 class="obs__title">
            <span class="obs__icon" aria-hidden="true">📡</span>
            Observability
          </h2>
          <span class="obs__spacer"></span>
          @if (loading()) {
            <span class="obs__chip obs__chip--info" data-testid="observability-loading">Loading…</span>
          }
          @if (sourceLabel(); as src) {
            <span class="obs__chip" [class.obs__chip--fixture]="usingFixture()" data-testid="observability-source">{{ src }}</span>
          }
          <button type="button"
                  class="obs__refresh"
                  data-testid="observability-refresh"
                  (click)="refresh()">⟳ Refresh</button>
        </div>
        <p class="obs__sub">
          Communication on the Agent Message Bus for this project. Read-only; the bus does not move state.
        </p>
      </header>

      @if (loadError(); as err) {
        <div class="obs__error" data-testid="observability-load-error">
          Could not load bus messages: {{ err }}
        </div>
      }

      @if (!loading() && messages().length === 0) {
        <div class="obs__empty" data-testid="observability-empty">
          <p>No bus messages for this project yet.</p>
          <p class="obs__empty-detail">
            The Agent Message Bus captures orchestrator decisions, agent
            observations, supervisor advisories, token usage, and lifecycle
            events. Once any of those land on this project, the timeline,
            matrix, counters, and heatmap appear here.
          </p>
          <button type="button"
                  class="obs__fixture-btn"
                  data-testid="observability-load-fixture"
                  (click)="loadFixture()">Load sample dataset</button>
        </div>
      } @else {
        <section class="obs__filters" aria-label="Filters" data-testid="observability-filters">
          <label class="obs__field">
            <span>Range</span>
            <select [ngModel]="filter.rangeHours"
                    (ngModelChange)="onRangeChanged($event)"
                    data-testid="observability-filter-range">
              @for (r of rangeOptions; track r.id) {
                <option [value]="r.id">{{ r.label }}</option>
              }
            </select>
          </label>
          <label class="obs__field">
            <span>Participant</span>
            <select [(ngModel)]="filter.participantId" (ngModelChange)="onFilterChanged()" data-testid="observability-filter-participant">
              <option value="">All participants</option>
              @for (p of participantIds(); track p) {
                <option [value]="p">{{ p }}</option>
              }
            </select>
          </label>
          <label class="obs__field">
            <span>Kind</span>
            <select [(ngModel)]="filter.kind" (ngModelChange)="onFilterChanged()" data-testid="observability-filter-kind">
              <option value="">All kinds</option>
              @for (k of kinds; track k) {
                <option [value]="k">{{ k }}</option>
              }
            </select>
          </label>
          <label class="obs__field">
            <span>Severity</span>
            <select [(ngModel)]="filter.severity" (ngModelChange)="onFilterChanged()" data-testid="observability-filter-severity">
              <option value="">All severities</option>
              @for (s of severities; track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </label>
          <label class="obs__field">
            <span>Job</span>
            <select [(ngModel)]="filter.jobId" (ngModelChange)="onFilterChanged()" data-testid="observability-filter-job">
              <option value="">All jobs</option>
              @for (j of jobIds(); track j) {
                <option [value]="j">{{ j }}</option>
              }
            </select>
          </label>
          <label class="obs__field">
            <span>Run</span>
            <input type="text" [(ngModel)]="filter.runId" (ngModelChange)="onFilterChanged()"
                   placeholder="run-id" data-testid="observability-filter-run">
          </label>
          <label class="obs__field">
            <span>CLI</span>
            <select [(ngModel)]="filter.cli" (ngModelChange)="onFilterChanged()" data-testid="observability-filter-cli">
              <option value="">All</option>
              @for (c of cliOptions(); track c) {
                <option [value]="c">{{ c }}</option>
              }
            </select>
          </label>
          <label class="obs__field">
            <span>Skill</span>
            <select [(ngModel)]="filter.skill" (ngModelChange)="onFilterChanged()" data-testid="observability-filter-skill">
              <option value="">All</option>
              @for (s of skillOptions(); track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </label>
          <button type="button"
                  class="obs__field-reset"
                  data-testid="observability-filter-reset"
                  (click)="resetFilters()">Reset</button>
        </section>

        <section class="obs__counters" aria-label="Counters" data-testid="observability-counters">
          @for (c of countChips(); track c.testid) {
            <div class="obs__counter"
                 [class.obs__counter--warn]="c.tone === 'warn'"
                 [class.obs__counter--high]="c.tone === 'high'"
                 [attr.data-testid]="c.testid">
              <span class="obs__counter-num">{{ c.value }}</span>
              <span class="obs__counter-label">{{ c.label }}</span>
            </div>
          }
        </section>

        <section class="obs__timeline" aria-label="Timeline by participant" data-testid="observability-timeline">
          <header class="obs__section-head">
            <h3>Timeline by participant</h3>
            <span class="obs__section-meta">{{ rangeLabel() }} · {{ filtered().length }} message{{ filtered().length === 1 ? '' : 's' }}</span>
          </header>
          @if (timelineLanes().length === 0) {
            <p class="obs__section-empty">No messages in the selected range.</p>
          } @else {
            <div class="obs__timeline-grid" role="grid">
              @for (lane of timelineLanes(); track lane.participantId) {
                <div class="obs__timeline-row"
                     role="row"
                     [attr.data-testid]="'observability-timeline-row'"
                     [attr.data-participant]="lane.participantId">
                  <div class="obs__timeline-label">{{ lane.participantId }}</div>
                  <div class="obs__timeline-track" role="gridcell">
                    @for (m of lane.marks; track m.id) {
                      <button type="button"
                              class="obs__timeline-mark"
                              [class]="'obs__timeline-mark--' + m.kind"
                              [class.obs__timeline-mark--high]="m.severity === 'High'"
                              [class.obs__timeline-mark--warn]="m.severity === 'Warn'"
                              [style.left.%]="m.offsetPct"
                              [title]="m.title"
                              [attr.data-testid]="'observability-timeline-mark'"
                              [attr.data-msg-id]="m.id"
                              (click)="selectMessage(m.id)"></button>
                    }
                  </div>
                </div>
              }
            </div>
            <div class="obs__timeline-axis">
              <span>{{ formatTime(rangeStart()) }}</span>
              <span>{{ formatTime(rangeEnd()) }}</span>
            </div>
          }
        </section>

        <section class="obs__matrix" aria-label="Participant matrix" data-testid="observability-matrix">
          <header class="obs__section-head">
            <h3>Participant × kind matrix</h3>
            <span class="obs__section-meta">Refs = unique jobs each participant emitted to</span>
          </header>
          @if (matrixRows().length === 0) {
            <p class="obs__section-empty">No participants in the selected range.</p>
          } @else {
            <div class="obs__matrix-scroll">
              <table class="obs__matrix-table">
                <thead>
                  <tr>
                    <th scope="col">Participant</th>
                    @for (k of matrixKinds(); track k) {
                      <th scope="col" class="obs__matrix-th">{{ k }}</th>
                    }
                    <th scope="col" class="obs__matrix-th">Total</th>
                    <th scope="col" class="obs__matrix-th">Refs</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of matrixRows(); track row.participantId) {
                    <tr [attr.data-testid]="'observability-matrix-row'"
                        [attr.data-participant]="row.participantId">
                      <th scope="row" class="obs__matrix-row-th">{{ row.participantId }}</th>
                      @for (k of matrixKinds(); track k) {
                        <td class="obs__matrix-cell"
                            [class.obs__matrix-cell--zero]="(row.kindCounts[k] ?? 0) === 0">
                          {{ row.kindCounts[k] ?? 0 }}
                        </td>
                      }
                      <td class="obs__matrix-cell obs__matrix-cell--total">{{ row.total }}</td>
                      <td class="obs__matrix-cell">{{ row.jobs.size }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>

        <section class="obs__heatmap-section" aria-label="Token usage heatmap" data-testid="observability-heatmap">
          <header class="obs__section-head">
            <h3>Token usage heatmap</h3>
            <span class="obs__section-meta">{{ heatmapTotalLabel() }}</span>
          </header>
          @if (heatmapHasData()) {
            <div class="obs__heatmap-scroll">
              <table class="obs__heatmap-table">
                <thead>
                  <tr>
                    <th scope="col">Participant</th>
                    @for (b of heatmapBuckets(); track b) {
                      <th scope="col" class="obs__heatmap-th">{{ b }}</th>
                    }
                    <th scope="col" class="obs__heatmap-th">Total</th>
                  </tr>
                </thead>
                <tbody>
                  @for (row of heatmapRows(); track row.participantId) {
                    <tr [attr.data-testid]="'observability-heatmap-row'"
                        [attr.data-participant]="row.participantId">
                      <th scope="row" class="obs__heatmap-row-th">{{ row.participantId }}</th>
                      @for (cell of row.cells; track cell.bucket) {
                        <td class="obs__heatmap-cell"
                            [class]="'obs__heatmap-cell--' + heatLevel(cell.tokens)"
                            [attr.data-testid]="'observability-heatmap-cell'"
                            [attr.data-participant]="cell.participantId"
                            [attr.data-bucket]="cell.bucket"
                            [attr.data-tokens]="cell.tokens"
                            [title]="cell.tokens + ' tokens · ' + cell.msgIds.length + ' message(s)'"
                            (click)="selectFirst(cell.msgIds)"></td>
                      }
                      <td class="obs__heatmap-cell obs__heatmap-cell--total">{{ formatTokens(row.total) }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          } @else {
            <p class="obs__section-empty" data-testid="observability-heatmap-empty">
              No token-usage data in the selected range.
            </p>
          }
        </section>

        <section class="obs__messages-section" aria-label="Filtered messages">
          <header class="obs__section-head">
            <h3>Messages</h3>
            <span class="obs__section-meta">{{ filtered().length }} match{{ filtered().length === 1 ? '' : 'es' }}</span>
          </header>
          @if (filtered().length === 0) {
            <p class="obs__section-empty" data-testid="observability-messages-empty">
              No messages match the current filter.
            </p>
          } @else {
            <div class="obs__messages-scroll">
              <table class="obs__messages-table" data-testid="observability-messages">
                <thead>
                  <tr>
                    <th scope="col">Time</th>
                    <th scope="col">Participant</th>
                    <th scope="col">Kind</th>
                    <th scope="col">Severity</th>
                    <th scope="col">Job · Run</th>
                    <th scope="col">Summary</th>
                  </tr>
                </thead>
                <tbody>
                  @for (m of filtered(); track m.id) {
                    <tr [class.obs__messages-row--active]="m.id === selectedMessageId()"
                        [attr.data-testid]="'observability-message-row'"
                        [attr.data-msg-id]="m.id"
                        (click)="selectMessage(m.id)">
                      <td class="obs__messages-cell obs__messages-cell--time">{{ formatTime(m.createdAt) }}</td>
                      <td class="obs__messages-cell">{{ m.participantId }}</td>
                      <td class="obs__messages-cell">
                        <span class="obs__kind-badge" [class]="'obs__kind-badge--' + m.kind">{{ m.kind }}</span>
                      </td>
                      <td class="obs__messages-cell">
                        @if (m.severity) {
                          <span class="obs__sev"
                                [class.obs__sev--warn]="m.severity === 'Warn'"
                                [class.obs__sev--high]="m.severity === 'High'">{{ m.severity }}</span>
                        }
                      </td>
                      <td class="obs__messages-cell obs__messages-cell--ids">
                        @if (m.jobId) { <code>{{ m.jobId }}</code> }
                        @if (m.runId) { <span class="obs__messages-sep">·</span><code>{{ m.runId }}</code> }
                      </td>
                      <td class="obs__messages-cell obs__messages-cell--summary">{{ m.summary || '(no summary)' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>

        @if (selectedMessage(); as sel) {
          <section class="obs__detail" aria-label="Selected message detail" data-testid="observability-detail">
            <header class="obs__section-head">
              <h3>Message · <code>{{ sel.id }}</code></h3>
              <span class="obs__spacer"></span>
              <button type="button"
                      class="obs__refresh"
                      data-testid="observability-detail-close"
                      (click)="clearSelection()">Close</button>
            </header>
            <dl class="obs__detail-meta">
              <div><dt>Created</dt><dd>{{ formatTime(sel.createdAt) }}</dd></div>
              <div><dt>Participant</dt><dd>{{ sel.participantId }} ({{ sel.role }})</dd></div>
              <div><dt>Kind</dt><dd>{{ sel.kind }}</dd></div>
              @if (sel.severity) { <div><dt>Severity</dt><dd>{{ sel.severity }}</dd></div> }
              @if (sel.jobId) { <div><dt>Job</dt><dd><code>{{ sel.jobId }}</code></dd></div> }
              @if (sel.runId) { <div><dt>Run</dt><dd><code>{{ sel.runId }}</code></dd></div> }
              @if (sel.cliSessionId) { <div><dt>CLI session</dt><dd><code>{{ sel.cliSessionId }}</code></dd></div> }
              @if (sel.replyToId) { <div><dt>Reply to</dt><dd><code>{{ sel.replyToId }}</code></dd></div> }
              @if (sel.correlationId) { <div><dt>Correlation</dt><dd><code>{{ sel.correlationId }}</code></dd></div> }
              @if (sel.tokens) {
                <div><dt>Tokens</dt><dd>↑{{ sel.tokens.input }} / ↓{{ sel.tokens.output }}@if (sel.tokens.cacheRead) { <span> (cache: {{ sel.tokens.cacheRead }})</span> }@if (sel.tokens.model) { <span> · {{ sel.tokens.model }}</span> }</dd></div>
              }
              @if (sel.tags && sel.tags.length) {
                <div><dt>Tags</dt><dd>
                  @for (t of sel.tags; track t) {
                    <span class="obs__tag">{{ t }}</span>
                  }
                </dd></div>
              }
            </dl>
            @if (sel.summary) {
              <p class="obs__detail-summary">{{ sel.summary }}</p>
            }
            @if (sel.body) {
              <pre class="obs__detail-body">{{ sel.body }}</pre>
            }
            <details class="obs__detail-json" open>
              <summary>Raw JSON</summary>
              <pre class="obs__detail-pre" data-testid="observability-detail-json">{{ formatJson(sel) }}</pre>
            </details>
          </section>
        }
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .obs { color: #cdd6f4; font-size: 0.85rem; }
    .obs__head { margin-bottom: 14px; }
    .obs__title-row {
      display: flex;
      align-items: baseline;
      gap: 10px;
      flex-wrap: wrap;
    }
    .obs__title {
      margin: 0;
      font-size: 1.05rem;
      font-weight: 600;
      color: #f8fafc;
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .obs__icon { font-size: 1rem; }
    .obs__sub { margin: 4px 0 0; color: #a6adc8; font-size: 0.82rem; }
    .obs__spacer { flex: 1; }
    .obs__chip {
      padding: 2px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: #bac2de;
      font-size: 0.72rem;
      letter-spacing: 0.02em;
    }
    .obs__chip--info { color: #94e2d5; background: rgba(148,226,213,0.12); }
    .obs__chip--fixture { color: #fab387; background: rgba(250,179,135,0.14); }
    .obs__refresh {
      background: rgba(255,255,255,0.04);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 6px;
      padding: 4px 10px;
      font: inherit;
      font-size: 0.78rem;
      cursor: pointer;
    }
    .obs__refresh:hover { background: rgba(255,255,255,0.10); }

    .obs__error {
      margin: 0 0 12px;
      padding: 8px 12px;
      border-radius: 6px;
      border: 1px solid rgba(248,113,113,0.40);
      background: rgba(248,113,113,0.10);
      color: #fda4af;
      font-size: 0.82rem;
    }

    .obs__empty {
      padding: 28px 20px;
      text-align: center;
      color: #a6adc8;
      border: 1px dashed #313244;
      border-radius: 6px;
      background: rgba(0,0,0,0.18);
    }
    .obs__empty p { margin: 0 0 8px; }
    .obs__empty-detail { color: #6c7086; font-size: 0.80rem; }
    .obs__fixture-btn {
      margin-top: 10px;
      background: rgba(148,226,213,0.10);
      color: #94e2d5;
      border: 1px solid rgba(148,226,213,0.40);
      border-radius: 6px;
      padding: 6px 14px;
      font: inherit;
      font-size: 0.82rem;
      cursor: pointer;
    }
    .obs__fixture-btn:hover { background: rgba(148,226,213,0.20); }

    .obs__filters {
      display: flex;
      flex-wrap: wrap;
      gap: 8px 12px;
      align-items: end;
      padding: 10px 12px;
      border: 1px solid #313244;
      border-radius: 6px;
      background: #1a1a26;
      margin-bottom: 14px;
    }
    .obs__field {
      display: flex;
      flex-direction: column;
      gap: 2px;
      font-size: 0.74rem;
    }
    .obs__field span {
      color: #6c7086;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    .obs__field select,
    .obs__field input {
      background: rgba(0,0,0,0.30);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12);
      border-radius: 5px;
      padding: 4px 8px;
      font: inherit;
      font-size: 0.82rem;
      min-width: 130px;
    }
    .obs__field-reset {
      background: transparent;
      color: #a6adc8;
      border: 1px solid transparent;
      border-radius: 6px;
      padding: 4px 10px;
      font: inherit;
      font-size: 0.78rem;
      cursor: pointer;
    }
    .obs__field-reset:hover { background: rgba(255,255,255,0.06); color: #cdd6f4; }

    .obs__counters {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
      gap: 8px;
      margin-bottom: 14px;
    }
    .obs__counter {
      padding: 8px 10px;
      border: 1px solid #313244;
      background: #1a1a26;
      border-radius: 6px;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .obs__counter--warn { border-color: rgba(249,226,175,0.45); }
    .obs__counter--high { border-color: rgba(248,113,113,0.50); }
    .obs__counter-num {
      font-size: 1.20rem;
      font-weight: 700;
      color: #f8fafc;
      font-variant-numeric: tabular-nums;
    }
    .obs__counter--warn .obs__counter-num { color: #f9e2af; }
    .obs__counter--high .obs__counter-num { color: #f87171; }
    .obs__counter-label {
      color: #6c7086;
      font-size: 0.70rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .obs__section-head {
      display: flex;
      align-items: baseline;
      gap: 10px;
      margin: 0 0 8px;
    }
    .obs__section-head h3 {
      margin: 0;
      font-size: 0.92rem;
      font-weight: 600;
      color: #cbd5e1;
    }
    .obs__section-meta { color: #6c7086; font-size: 0.74rem; }
    .obs__section-empty {
      margin: 0;
      padding: 10px 12px;
      color: #6c7086;
      font-size: 0.80rem;
      font-style: italic;
      border: 1px dashed #313244;
      border-radius: 6px;
      background: rgba(0,0,0,0.18);
    }

    .obs__timeline { margin-bottom: 14px; }
    .obs__timeline-grid {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 8px 8px 4px;
      border: 1px solid #313244;
      border-radius: 6px;
      background: #1a1a26;
    }
    .obs__timeline-row {
      display: grid;
      grid-template-columns: 140px 1fr;
      gap: 8px;
      align-items: center;
      min-height: 22px;
    }
    .obs__timeline-label {
      color: #bac2de;
      font-size: 0.78rem;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .obs__timeline-track {
      position: relative;
      height: 16px;
      border-radius: 6px;
      background: rgba(255,255,255,0.04);
    }
    .obs__timeline-mark {
      position: absolute;
      top: 50%;
      transform: translate(-50%, -50%);
      width: 9px;
      height: 9px;
      border-radius: 50%;
      border: 1px solid rgba(255,255,255,0.18);
      background: #94a3b8;
      cursor: pointer;
      padding: 0;
    }
    .obs__timeline-mark--observation { background: #94a3b8; }
    .obs__timeline-mark--question { background: #fcd34d; }
    .obs__timeline-mark--decision { background: #c4b5fd; }
    .obs__timeline-mark--advisory { background: #fdba74; }
    .obs__timeline-mark--intervention { background: #f87171; width: 11px; height: 11px; }
    .obs__timeline-mark--artifact { background: #7dd3fc; }
    .obs__timeline-mark--token-usage { background: #6ee7b7; }
    .obs__timeline-mark--lifecycle { background: #cbd5e1; }
    .obs__timeline-mark--error { background: #f87171; border-color: #fca5a5; width: 11px; height: 11px; }
    .obs__timeline-mark--heartbeat { background: rgba(148,163,184,0.45); width: 6px; height: 6px; }
    .obs__timeline-mark--warn { box-shadow: 0 0 0 2px rgba(249,226,175,0.45); }
    .obs__timeline-mark--high { box-shadow: 0 0 0 2px rgba(248,113,113,0.55); }
    .obs__timeline-axis {
      display: flex;
      justify-content: space-between;
      padding: 4px 8px 0;
      font-size: 0.70rem;
      color: #6c7086;
      font-variant-numeric: tabular-nums;
    }

    .obs__matrix { margin-bottom: 14px; }
    .obs__matrix-scroll, .obs__heatmap-scroll, .obs__messages-scroll {
      overflow-x: auto;
      border: 1px solid #313244;
      border-radius: 6px;
      background: #1a1a26;
    }
    table.obs__matrix-table, table.obs__heatmap-table, table.obs__messages-table {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.80rem;
    }
    .obs__matrix-table th, .obs__matrix-table td,
    .obs__heatmap-table th, .obs__heatmap-table td,
    .obs__messages-table th, .obs__messages-table td {
      padding: 6px 8px;
      text-align: left;
      border-bottom: 1px solid rgba(255,255,255,0.04);
    }
    .obs__matrix-th, .obs__heatmap-th {
      color: #6c7086;
      font-size: 0.70rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      font-weight: 500;
    }
    .obs__matrix-row-th, .obs__heatmap-row-th {
      color: #cdd6f4;
      font-weight: 500;
      white-space: nowrap;
    }
    .obs__matrix-cell, .obs__heatmap-cell {
      font-variant-numeric: tabular-nums;
    }
    .obs__matrix-cell--zero { color: #45475a; }
    .obs__matrix-cell--total { font-weight: 600; color: #cbd5e1; }
    .obs__heatmap-cell { width: 26px; min-width: 26px; height: 22px; padding: 0; cursor: pointer; }
    .obs__heatmap-cell--total { width: auto; padding: 6px 8px; cursor: default; }
    .obs__heatmap-cell--l0 { background: rgba(255,255,255,0.02); }
    .obs__heatmap-cell--l1 { background: rgba(110,231,183,0.14); }
    .obs__heatmap-cell--l2 { background: rgba(110,231,183,0.30); }
    .obs__heatmap-cell--l3 { background: rgba(110,231,183,0.50); }
    .obs__heatmap-cell--l4 { background: rgba(110,231,183,0.75); }

    .obs__messages-section { margin-bottom: 14px; }
    .obs__messages-table tbody tr { cursor: pointer; }
    .obs__messages-table tbody tr:hover { background: rgba(255,255,255,0.04); }
    .obs__messages-row--active { background: rgba(196,181,253,0.10) !important; }
    .obs__messages-cell--time { font-variant-numeric: tabular-nums; color: #a6adc8; white-space: nowrap; }
    .obs__messages-cell--ids code {
      background: rgba(255,255,255,0.05);
      padding: 1px 4px;
      border-radius: 3px;
      font-size: 0.74rem;
    }
    .obs__messages-sep { color: #45475a; padding: 0 4px; }
    .obs__messages-cell--summary {
      max-width: 420px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .obs__kind-badge {
      padding: 1px 6px;
      border-radius: 3px;
      font-size: 0.72rem;
      background: rgba(255,255,255,0.06);
      color: #bac2de;
      letter-spacing: 0.02em;
    }
    .obs__kind-badge--decision { background: rgba(196,181,253,0.18); color: #c4b5fd; }
    .obs__kind-badge--intervention { background: rgba(248,113,113,0.20); color: #fca5a5; }
    .obs__kind-badge--error { background: rgba(248,113,113,0.20); color: #fca5a5; }
    .obs__kind-badge--token-usage { background: rgba(110,231,183,0.18); color: #6ee7b7; }
    .obs__kind-badge--advisory { background: rgba(253,186,116,0.18); color: #fdba74; }
    .obs__kind-badge--lifecycle { background: rgba(203,213,225,0.14); color: #cbd5e1; }
    .obs__kind-badge--heartbeat { background: rgba(148,163,184,0.14); color: #94a3b8; }
    .obs__sev { color: #94a3b8; font-size: 0.74rem; }
    .obs__sev--warn { color: #f9e2af; }
    .obs__sev--high { color: #fca5a5; font-weight: 600; }

    .obs__detail {
      margin-top: 6px;
      padding: 12px 14px;
      border: 1px solid #313244;
      border-radius: 6px;
      background: #181825;
    }
    .obs__detail-meta {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 4px 12px;
      margin: 0 0 10px;
      font-size: 0.80rem;
    }
    .obs__detail-meta > div { display: contents; }
    .obs__detail-meta dt { color: #6c7086; }
    .obs__detail-meta dd { margin: 0; color: #cdd6f4; }
    .obs__detail-meta code { font-size: 0.74rem; color: #c4b5fd; background: rgba(255,255,255,0.05); padding: 1px 4px; border-radius: 3px; }
    .obs__detail-summary {
      margin: 0 0 8px;
      color: #f1f5f9;
      font-size: 0.86rem;
    }
    .obs__detail-body {
      margin: 0 0 8px;
      padding: 8px 10px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 4px;
      font-size: 0.78rem;
      color: #cdd6f4;
      white-space: pre-wrap;
      max-height: 220px;
      overflow: auto;
    }
    .obs__detail-json summary {
      cursor: pointer;
      color: #6c7086;
      font-size: 0.78rem;
    }
    .obs__detail-pre {
      max-height: 320px;
      overflow: auto;
      padding: 8px 10px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.06);
      border-radius: 4px;
      font-size: 0.74rem;
      color: #cdd6f4;
      white-space: pre-wrap;
    }
    .obs__tag {
      display: inline-block;
      margin-right: 4px;
      padding: 1px 6px;
      border-radius: 3px;
      background: rgba(255,255,255,0.05);
      color: #a6adc8;
      font-size: 0.72rem;
    }
  `],
})
export class ProjectObservabilityPanelComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly bus = inject(AgentBusService);

  readonly kinds = AGENT_MESSAGE_KINDS;
  readonly severities = AGENT_MESSAGE_SEVERITIES;
  readonly rangeOptions = RANGE_OPTIONS;

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly summary = signal<AgentMessageSummary | null>(null);
  readonly messages = signal<AgentMessage[]>([]);
  readonly usingFixture = signal<boolean>(false);
  readonly selectedMessageId = signal<string | null>(null);

  filter: FilterState = { ...DEFAULT_FILTER };
  // ngModel needs a stable container; bumping a tick signal lets computeds rerun.
  readonly filterTick = signal(0);

  private pollTimer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      const name = this.projectName();
      if (!name) return;
      this.refresh();
    });
  }

  ngOnInit(): void {
    this.pollTimer = setInterval(() => this.refresh(true), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(silent = false): void {
    const name = this.projectName();
    if (!name) return;
    if (!silent) this.loading.set(true);
    this.loadError.set(null);
    this.bus.getSummary(name).subscribe({
      next: (s) => {
        this.summary.set(s);
      },
      error: () => {},
    });
    this.bus.getRecent(name, 500).subscribe({
      next: (msgs) => {
        if (this.usingFixture()) {
          // Don't clobber a user-loaded fixture with an empty live response.
          this.loading.set(false);
          return;
        }
        this.messages.set(msgs);
        this.loading.set(false);
      },
      error: (err) => {
        this.loading.set(false);
        this.loadError.set(err?.message ?? 'request failed');
      },
    });
  }

  loadFixture(): void {
    const name = this.projectName();
    const sample = AgentBusFixture.sample(name);
    this.messages.set(sample);
    this.summary.set(AgentBusFixture.summary(name, sample));
    this.usingFixture.set(true);
    this.loading.set(false);
    this.loadError.set(null);
  }

  selectMessage(id: string): void {
    this.selectedMessageId.set(id);
  }
  selectFirst(ids: ReadonlyArray<string>): void {
    if (ids.length > 0) this.selectedMessageId.set(ids[0]);
  }
  clearSelection(): void {
    this.selectedMessageId.set(null);
  }

  resetFilters(): void {
    this.filter = { ...DEFAULT_FILTER };
    this.filterTick.update(v => v + 1);
    this.selectedMessageId.set(null);
  }

  onFilterChanged(): void {
    this.filterTick.update(v => v + 1);
  }

  onRangeChanged(value: string | number): void {
    const n = typeof value === 'number' ? value : Number(value);
    this.filter.rangeHours = Number.isFinite(n) ? n : 0;
    this.filterTick.update(v => v + 1);
  }

  readonly sourceLabel = computed(() => {
    if (this.usingFixture()) return 'Fixture';
    const total = this.summary()?.totalMessages ?? this.messages().length;
    return total > 0 ? 'Live' : 'Empty';
  });

  readonly rangeStart = computed(() => {
    void this.filterTick();
    if (this.filter.rangeHours <= 0) {
      const msgs = this.messages();
      if (!msgs.length) return new Date().toISOString();
      let min = msgs[0].createdAt;
      for (const m of msgs) if (m.createdAt < min) min = m.createdAt;
      return min;
    }
    return new Date(Date.now() - this.filter.rangeHours * 3600 * 1000).toISOString();
  });
  readonly rangeEnd = computed(() => new Date().toISOString());

  readonly rangeLabel = computed(() => {
    void this.filterTick();
    return RANGE_OPTIONS.find(o => o.id === this.filter.rangeHours)?.label ?? 'Custom';
  });

  readonly filtered = computed<AgentMessage[]>(() => {
    void this.filterTick();
    const f = this.filter;
    const start = this.rangeStart();
    const end = this.rangeEnd();
    return this.messages().filter(m => {
      if (m.createdAt < start || m.createdAt > end) return false;
      if (f.participantId && m.participantId !== f.participantId) return false;
      if (f.kind && m.kind !== f.kind) return false;
      if (f.severity && m.severity !== f.severity) return false;
      if (f.jobId && m.jobId !== f.jobId) return false;
      if (f.runId && m.runId !== f.runId.trim()) return false;
      if (f.cli && !(m.tags ?? []).includes(`cli:${f.cli}`)) return false;
      if (f.skill && !(m.tags ?? []).includes(`skill:${f.skill}`)) return false;
      return true;
    }).sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1));
  });

  readonly participantIds = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) set.add(m.participantId);
    return Array.from(set).sort();
  });

  readonly jobIds = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) if (m.jobId) set.add(m.jobId);
    return Array.from(set).sort();
  });

  readonly cliOptions = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) {
      for (const t of m.tags ?? []) if (t.startsWith('cli:')) set.add(t.slice(4));
    }
    return Array.from(set).sort();
  });
  readonly skillOptions = computed(() => {
    const set = new Set<string>();
    for (const m of this.messages()) {
      for (const t of m.tags ?? []) if (t.startsWith('skill:')) set.add(t.slice(6));
    }
    return Array.from(set).sort();
  });

  readonly countChips = computed<CountChip[]>(() => {
    const msgs = this.filtered();
    let interventions = 0, errors = 0, warns = 0, highs = 0, tokens = 0;
    for (const m of msgs) {
      if (m.kind === 'intervention') interventions++;
      if (m.kind === 'error') errors++;
      if (m.severity === 'Warn') warns++;
      if (m.severity === 'High') highs++;
      if (m.tokens) tokens += (m.tokens.input ?? 0) + (m.tokens.output ?? 0);
    }
    const sorted = [...msgs].sort((a, b) => (a.createdAt < b.createdAt ? -1 : 1));
    let silent = 0;
    for (let i = 1; i < sorted.length; i++) {
      const dt = new Date(sorted[i].createdAt).getTime() - new Date(sorted[i - 1].createdAt).getTime();
      if (dt >= SILENT_GAP_THRESHOLD_MS) silent++;
    }
    return [
      { testid: 'observability-counter-total', label: 'Messages', value: msgs.length, tone: 'neutral' },
      { testid: 'observability-counter-interventions', label: 'Interventions', value: interventions, tone: interventions > 0 ? 'high' : 'neutral' },
      { testid: 'observability-counter-errors', label: 'Errors', value: errors, tone: errors > 0 ? 'high' : 'neutral' },
      { testid: 'observability-counter-warn', label: 'Severity Warn', value: warns, tone: warns > 0 ? 'warn' : 'neutral' },
      { testid: 'observability-counter-high', label: 'Severity High', value: highs, tone: highs > 0 ? 'high' : 'neutral' },
      { testid: 'observability-counter-tokens', label: 'Tokens (sum)', value: tokens, tone: 'neutral' },
      { testid: 'observability-counter-silent', label: `Silent gaps (>${Math.round(SILENT_GAP_THRESHOLD_MS / 60000)}m)`, value: silent, tone: silent > 0 ? 'warn' : 'neutral' },
    ];
  });

  readonly timelineLanes = computed<TimelineLane[]>(() => {
    const start = new Date(this.rangeStart()).getTime();
    const end = new Date(this.rangeEnd()).getTime();
    const span = Math.max(end - start, 1);
    const byParticipant = new Map<string, TimelineLane['marks'][number][]>();
    for (const m of this.filtered()) {
      const t = new Date(m.createdAt).getTime();
      const offsetPct = Math.max(0, Math.min(100, ((t - start) / span) * 100));
      const arr = byParticipant.get(m.participantId) ?? [];
      arr.push({
        id: m.id,
        offsetPct,
        kind: m.kind,
        severity: m.severity ?? null,
        title: `${m.participantId} · ${m.kind}${m.severity ? ' (' + m.severity + ')' : ''} · ${m.summary ?? ''}`,
      });
      byParticipant.set(m.participantId, arr);
    }
    return Array.from(byParticipant.entries())
      .sort((a, b) => (a[0] < b[0] ? -1 : 1))
      .map(([participantId, marks]) => ({ participantId, marks }));
  });

  readonly matrixKinds = computed(() => {
    const set = new Set<string>();
    for (const m of this.filtered()) set.add(m.kind);
    return Array.from(set).sort();
  });

  readonly matrixRows = computed<MatrixRow[]>(() => {
    const rows = new Map<string, { kindCounts: Record<string, number>; total: number; jobs: Set<string> }>();
    for (const m of this.filtered()) {
      const row = rows.get(m.participantId) ?? { kindCounts: {}, total: 0, jobs: new Set<string>() };
      row.kindCounts[m.kind] = (row.kindCounts[m.kind] ?? 0) + 1;
      row.total++;
      if (m.jobId) row.jobs.add(m.jobId);
      rows.set(m.participantId, row);
    }
    return Array.from(rows.entries())
      .sort((a, b) => b[1].total - a[1].total)
      .map(([participantId, r]) => ({ participantId, kindCounts: r.kindCounts, total: r.total, jobs: r.jobs }));
  });

  readonly heatmapBuckets = computed<string[]>(() => {
    const start = new Date(this.rangeStart()).getTime();
    const end = new Date(this.rangeEnd()).getTime();
    const span = Math.max(end - start, 1);
    const out: string[] = [];
    for (let i = 0; i < HEATMAP_BUCKETS; i++) {
      const t = start + (span * (i + 0.5)) / HEATMAP_BUCKETS;
      out.push(this.shortBucket(new Date(t)));
    }
    return out;
  });

  readonly heatmapRows = computed(() => {
    const start = new Date(this.rangeStart()).getTime();
    const end = new Date(this.rangeEnd()).getTime();
    const span = Math.max(end - start, 1);
    const map = new Map<string, HeatmapCell[]>();
    for (const m of this.filtered()) {
      if (!m.tokens) continue;
      const t = new Date(m.createdAt).getTime();
      const bucket = Math.min(HEATMAP_BUCKETS - 1, Math.max(0, Math.floor(((t - start) / span) * HEATMAP_BUCKETS)));
      let arr = map.get(m.participantId);
      if (!arr) {
        arr = Array.from({ length: HEATMAP_BUCKETS }, (_, i) => ({
          participantId: m.participantId,
          bucket: i,
          tokens: 0,
          msgIds: [] as string[],
        }));
        map.set(m.participantId, arr);
      }
      const tokens = (m.tokens.input ?? 0) + (m.tokens.output ?? 0);
      arr[bucket].tokens += tokens;
      arr[bucket].msgIds.push(m.id);
    }
    const rows = Array.from(map.entries()).map(([participantId, cells]) => ({
      participantId,
      cells,
      total: cells.reduce((a, c) => a + c.tokens, 0),
    }));
    rows.sort((a, b) => b.total - a.total);
    return rows;
  });

  readonly heatmapHasData = computed(() => this.heatmapRows().length > 0);
  readonly heatmapTotalLabel = computed(() => {
    const rows = this.heatmapRows();
    const total = rows.reduce((a, r) => a + r.total, 0);
    return rows.length === 0
      ? 'No token messages in range.'
      : `${rows.length} participant${rows.length === 1 ? '' : 's'} · ${this.formatTokens(total)} tokens`;
  });

  readonly selectedMessage = computed<AgentMessage | null>(() => {
    const id = this.selectedMessageId();
    if (!id) return null;
    return this.messages().find(m => m.id === id) ?? null;
  });

  heatLevel(tokens: number): string {
    if (tokens <= 0) return 'l0';
    if (tokens < 1500) return 'l1';
    if (tokens < 5000) return 'l2';
    if (tokens < 15000) return 'l3';
    return 'l4';
  }

  formatTokens(n: number): string {
    if (!Number.isFinite(n)) return '0';
    if (n >= 1_000_000) return (n / 1_000_000).toFixed(1).replace(/\.0$/, '') + 'M';
    if (n >= 1_000) return (n / 1_000).toFixed(1).replace(/\.0$/, '') + 'k';
    return String(Math.round(n));
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  formatJson(msg: AgentMessage): string {
    try {
      return JSON.stringify(msg, null, 2);
    } catch {
      return String(msg);
    }
  }

  private shortBucket(d: Date): string {
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
  }
}
