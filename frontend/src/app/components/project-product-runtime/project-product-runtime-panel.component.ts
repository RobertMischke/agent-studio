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
import {
  ProductRuntimeFixture,
  ProductRuntimeService,
} from '../../services/product-runtime.service';
import {
  PRODUCT_RUNTIME_LEVELS,
  ProductRuntimeEvent,
  ProductRuntimeLevel,
  RuntimeEventParseWarning,
} from '../../models/product-runtime.model';

interface FilterState {
  level: ProductRuntimeLevel | '';
  event: string;
  subsystem: string;
  jobId: string;
  runId: string;
  correlationId: string;
  rangeHours: number;
}

const DEFAULT_FILTER: FilterState = {
  level: '',
  event: '',
  subsystem: '',
  jobId: '',
  runId: '',
  correlationId: '',
  rangeHours: 24,
};

const RANGE_OPTIONS: ReadonlyArray<{ id: number; label: string }> = [
  { id: 1, label: 'Last 1h' },
  { id: 6, label: 'Last 6h' },
  { id: 24, label: 'Last 24h' },
  { id: 24 * 7, label: 'Last 7d' },
  { id: 0, label: 'All time' },
];

const LEVEL_RANK: Record<ProductRuntimeLevel, number> = {
  Trace: 0, Debug: 1, Info: 2, Warn: 3, Error: 4, Fatal: 5,
};

interface CountChip {
  testid: string;
  label: string;
  value: number | string;
  tone: 'neutral' | 'warn' | 'high';
}

interface ErrorGroup {
  key: string;
  event: string;
  errorType: string;
  count: number;
  lastSeen: string;
  retryable: boolean | null;
  sampleMessage: string;
  jobId: string | null;
  runId: string | null;
}

interface LatencyRow {
  key: string;
  subsystem: string;
  operation: string;
  count: number;
  p50: number;
  p95: number;
  max: number;
}

interface CounterRow {
  key: string;
  subsystem: string;
  event: string;
  total: number;
  warns: number;
  errors: number;
}

interface DomainTimelineEntry {
  id: string;
  timestamp: string;
  event: string;
  subsystem: string;
  level: ProductRuntimeLevel;
  status: string | null;
  correlationId: string | null;
  durationMs: number | null;
  groupKey: string;
}

const DOMAIN_TIMELINE_ALLOW_PREFIXES = ['order.', 'payment.', 'render.', 'job.', 'auth.', 'task.', 'http.request.'];

/**
 * Project Product Runtime Observability panel: renders the structured runtime
 * stream emitted by the software the agents are building, distinct from the
 * Agent Message Bus surface. Reads {@code /api/runtime/{project}/events} via
 * {@link ProductRuntimeService}. When the live stream is empty, the panel
 * offers a fixture toggle ({@link ProductRuntimeFixture}) so reviewers can
 * exercise every surface and Playwright can capture screenshots.
 *
 * Surfaces, top-down:
 *  - Counter chips: total events, errors, warnings, p95 latency, top
 *    subsystem, correlation count, malformed-line count.
 *  - Recent events feed (newest first, 50 rows) with drill-down to JSON.
 *  - Error groups: events with level Error/Fatal grouped by event +
 *    error.type, with last-seen + retryable hint.
 *  - Latency summary: p50 / p95 / max per (subsystem, operation) over
 *    Ok-status events.
 *  - Counters per (subsystem, event) with warn/error split.
 *  - Domain timeline: filtered to product-shaped events (order.*, payment.*,
 *    render.*, job.*, http.request.*) ordered chronologically.
 *  - Malformed-line warnings, kept compact and only when present.
 *
 * Read-only by contract: nothing on this panel mutates state, runs an
 * agent, or moves a job; runtime events are pure output.
 */
@Component({
  selector: 'app-project-product-runtime-panel',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="prt" data-testid="project-product-runtime-panel">
      <header class="prt__head">
        <div class="prt__title-row">
          <h2 class="prt__title">
            <span class="prt__icon" aria-hidden="true">📈</span>
            Product Runtime
          </h2>
          <span class="prt__spacer"></span>
          @if (loading()) {
            <span class="prt__chip prt__chip--info" data-testid="product-runtime-loading">Loading…</span>
          }
          @if (sourceLabel(); as src) {
            <span class="prt__chip"
                  [class.prt__chip--fixture]="usingFixture()"
                  data-testid="product-runtime-source">{{ src }}</span>
          }
          <button type="button"
                  class="prt__refresh"
                  data-testid="product-runtime-refresh"
                  (click)="refresh()">⟳ Refresh</button>
        </div>
        <p class="prt__sub">
          Structured events from the built software during local runs and tests.
          Read-only stream; distinct from the Agent Message Bus.
        </p>
      </header>

      @if (loadError(); as err) {
        <div class="prt__error" data-testid="product-runtime-load-error">
          Could not load runtime events: {{ err }}
        </div>
      }

      @if (!loading() && events().length === 0 && warnings().length === 0) {
        <div class="prt__empty" data-testid="product-runtime-empty">
          <p>No runtime events captured for this project yet.</p>
          <p class="prt__empty-detail">
            Capture lands here when the built software emits events to
            <code>logs/runtime/{{ projectName() }}/</code> or a watched job's
            <code>logs/runtime/</code> folder. Schema:
            <code>docs/schemas/product-runtime-event.schema.json</code>.
          </p>
          <button type="button"
                  class="prt__fixture-btn"
                  data-testid="product-runtime-load-fixture"
                  (click)="loadFixture()">Load sample dataset</button>
        </div>
      } @else {
        <section class="prt__filters" aria-label="Filters" data-testid="product-runtime-filters">
          <label class="prt__field">
            <span>Range</span>
            <select [ngModel]="filter.rangeHours"
                    (ngModelChange)="onRangeChanged($event)"
                    data-testid="product-runtime-filter-range">
              @for (r of rangeOptions; track r.id) {
                <option [value]="r.id">{{ r.label }}</option>
              }
            </select>
          </label>
          <label class="prt__field">
            <span>Level</span>
            <select [(ngModel)]="filter.level" (ngModelChange)="onFilterChanged()"
                    data-testid="product-runtime-filter-level">
              <option value="">All</option>
              @for (l of levels; track l) {
                <option [value]="l">{{ l }}+</option>
              }
            </select>
          </label>
          <label class="prt__field">
            <span>Event</span>
            <select [(ngModel)]="filter.event" (ngModelChange)="onFilterChanged()"
                    data-testid="product-runtime-filter-event">
              <option value="">All events</option>
              @for (e of eventOptions(); track e) {
                <option [value]="e">{{ e }}</option>
              }
            </select>
          </label>
          <label class="prt__field">
            <span>Subsystem</span>
            <select [(ngModel)]="filter.subsystem" (ngModelChange)="onFilterChanged()"
                    data-testid="product-runtime-filter-subsystem">
              <option value="">All</option>
              @for (s of subsystemOptions(); track s) {
                <option [value]="s">{{ s }}</option>
              }
            </select>
          </label>
          <label class="prt__field">
            <span>Job</span>
            <select [(ngModel)]="filter.jobId" (ngModelChange)="onFilterChanged()"
                    data-testid="product-runtime-filter-job">
              <option value="">All jobs</option>
              @for (j of jobOptions(); track j) {
                <option [value]="j">{{ j }}</option>
              }
            </select>
          </label>
          <label class="prt__field">
            <span>Run</span>
            <input type="text" [(ngModel)]="filter.runId" (ngModelChange)="onFilterChanged()"
                   placeholder="run-id" data-testid="product-runtime-filter-run">
          </label>
          <label class="prt__field">
            <span>Correlation</span>
            <input type="text" [(ngModel)]="filter.correlationId" (ngModelChange)="onFilterChanged()"
                   placeholder="correlation-id" data-testid="product-runtime-filter-correlation">
          </label>
          <button type="button"
                  class="prt__field-reset"
                  data-testid="product-runtime-filter-reset"
                  (click)="resetFilters()">Reset</button>
        </section>

        <section class="prt__counters" aria-label="Counters" data-testid="product-runtime-counters">
          @for (c of countChips(); track c.testid) {
            <div class="prt__counter"
                 [class.prt__counter--warn]="c.tone === 'warn'"
                 [class.prt__counter--high]="c.tone === 'high'"
                 [attr.data-testid]="c.testid">
              <span class="prt__counter-num">{{ c.value }}</span>
              <span class="prt__counter-label">{{ c.label }}</span>
            </div>
          }
        </section>

        @if (warnings().length > 0) {
          <section class="prt__warnings" data-testid="product-runtime-warnings">
            <header class="prt__section-head">
              <h3>Malformed lines</h3>
              <span class="prt__section-meta">
                {{ warnings().length }} parse warning{{ warnings().length === 1 ? '' : 's' }} surfaced from
                <code>.warnings.jsonl</code> sidecars
              </span>
            </header>
            <ul class="prt__warning-list">
              @for (w of warnings().slice(0, 8); track $index) {
                <li class="prt__warning-row" [attr.data-testid]="'product-runtime-warning-row'">
                  <code class="prt__warning-where">{{ w.sourcePath }}:{{ w.lineNumber }}</code>
                  <span class="prt__warning-reason">{{ w.reason }}</span>
                  <code class="prt__warning-raw">{{ w.rawLine }}</code>
                </li>
              }
            </ul>
          </section>
        }

        <section class="prt__events-section" aria-label="Recent events" data-testid="product-runtime-events">
          <header class="prt__section-head">
            <h3>Recent events</h3>
            <span class="prt__section-meta">
              {{ filtered().length }} match{{ filtered().length === 1 ? '' : 'es' }} · showing {{ Math.min(filtered().length, 50) }}
            </span>
          </header>
          @if (filtered().length === 0) {
            <p class="prt__section-empty" data-testid="product-runtime-events-empty">
              No events match the current filter.
            </p>
          } @else {
            <div class="prt__events-scroll">
              <table class="prt__events-table">
                <thead>
                  <tr>
                    <th scope="col">Time</th>
                    <th scope="col">Level</th>
                    <th scope="col">Event</th>
                    <th scope="col">Subsystem</th>
                    <th scope="col">Operation</th>
                    <th scope="col">Status</th>
                    <th scope="col">Latency</th>
                    <th scope="col">Job · Run</th>
                  </tr>
                </thead>
                <tbody>
                  @for (e of filteredHead(); track $index) {
                    <tr [class.prt__events-row--active]="rowKey(e) === selectedKey()"
                        [attr.data-testid]="'product-runtime-event-row'"
                        [attr.data-event]="e.event"
                        [attr.data-level]="e.level"
                        (click)="select(e)">
                      <td class="prt__events-cell prt__events-cell--time">{{ formatTime(e.timestamp) }}</td>
                      <td class="prt__events-cell">
                        <span class="prt__level"
                              [class.prt__level--warn]="e.level === 'Warn'"
                              [class.prt__level--error]="e.level === 'Error' || e.level === 'Fatal'">
                          {{ e.level }}
                        </span>
                      </td>
                      <td class="prt__events-cell"><code>{{ e.event }}</code></td>
                      <td class="prt__events-cell">{{ e.subsystem }}</td>
                      <td class="prt__events-cell">{{ e.operation || '' }}</td>
                      <td class="prt__events-cell">
                        @if (e.status) {
                          <span class="prt__status"
                                [class.prt__status--ok]="e.status === 'Ok'"
                                [class.prt__status--bad]="e.status === 'Failed' || e.status === 'Timeout'">
                            {{ e.status }}
                          </span>
                        }
                      </td>
                      <td class="prt__events-cell prt__events-cell--num">
                        @if (e.duration) { {{ formatMs(e.duration.ms) }} }
                      </td>
                      <td class="prt__events-cell prt__events-cell--ids">
                        @if (e.jobId) { <code>{{ e.jobId }}</code> }
                        @if (e.runId) { <span class="prt__events-sep">·</span><code>{{ e.runId }}</code> }
                      </td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          }
        </section>

        <section class="prt__error-groups" aria-label="Error groups" data-testid="product-runtime-error-groups">
          <header class="prt__section-head">
            <h3>Error groups</h3>
            <span class="prt__section-meta">Errors and Fatals grouped by event + error.type</span>
          </header>
          @if (errorGroups().length === 0) {
            <p class="prt__section-empty" data-testid="product-runtime-error-groups-empty">
              No errors in the selected range.
            </p>
          } @else {
            <table class="prt__group-table">
              <thead>
                <tr>
                  <th scope="col">Event</th>
                  <th scope="col">Error type</th>
                  <th scope="col">Count</th>
                  <th scope="col">Last seen</th>
                  <th scope="col">Retryable</th>
                  <th scope="col">Sample</th>
                </tr>
              </thead>
              <tbody>
                @for (g of errorGroups(); track g.key) {
                  <tr [attr.data-testid]="'product-runtime-error-group-row'"
                      [attr.data-key]="g.key">
                    <td><code>{{ g.event }}</code></td>
                    <td>{{ g.errorType || '(none)' }}</td>
                    <td class="prt__num">{{ g.count }}</td>
                    <td class="prt__events-cell--time">{{ formatTime(g.lastSeen) }}</td>
                    <td>
                      @if (g.retryable === true) { <span class="prt__chip prt__chip--retry">retryable</span> }
                      @else if (g.retryable === false) { <span class="prt__chip">terminal</span> }
                    </td>
                    <td class="prt__group-sample">{{ g.sampleMessage }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </section>

        <section class="prt__latency" aria-label="Latency summary" data-testid="product-runtime-latency">
          <header class="prt__section-head">
            <h3>Latency summary</h3>
            <span class="prt__section-meta">p50 / p95 / max per (subsystem, operation) on Ok status</span>
          </header>
          @if (latencyRows().length === 0) {
            <p class="prt__section-empty" data-testid="product-runtime-latency-empty">
              No timed Ok events in the selected range.
            </p>
          } @else {
            <table class="prt__group-table">
              <thead>
                <tr>
                  <th scope="col">Subsystem</th>
                  <th scope="col">Operation</th>
                  <th scope="col">Count</th>
                  <th scope="col">p50</th>
                  <th scope="col">p95</th>
                  <th scope="col">max</th>
                </tr>
              </thead>
              <tbody>
                @for (r of latencyRows(); track r.key) {
                  <tr [attr.data-testid]="'product-runtime-latency-row'"
                      [attr.data-subsystem]="r.subsystem"
                      [attr.data-operation]="r.operation">
                    <td>{{ r.subsystem }}</td>
                    <td>{{ r.operation }}</td>
                    <td class="prt__num">{{ r.count }}</td>
                    <td class="prt__num">{{ formatMs(r.p50) }}</td>
                    <td class="prt__num">{{ formatMs(r.p95) }}</td>
                    <td class="prt__num">{{ formatMs(r.max) }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </section>

        <section class="prt__counters-table" aria-label="Counters by event" data-testid="product-runtime-event-counters">
          <header class="prt__section-head">
            <h3>Counters</h3>
            <span class="prt__section-meta">By (subsystem, event) with warn / error split</span>
          </header>
          @if (counterRows().length === 0) {
            <p class="prt__section-empty" data-testid="product-runtime-counters-empty">
              No events to count.
            </p>
          } @else {
            <table class="prt__group-table">
              <thead>
                <tr>
                  <th scope="col">Subsystem</th>
                  <th scope="col">Event</th>
                  <th scope="col">Total</th>
                  <th scope="col">Warn</th>
                  <th scope="col">Err</th>
                </tr>
              </thead>
              <tbody>
                @for (r of counterRows(); track r.key) {
                  <tr [attr.data-testid]="'product-runtime-counter-row'"
                      [attr.data-event]="r.event">
                    <td>{{ r.subsystem }}</td>
                    <td><code>{{ r.event }}</code></td>
                    <td class="prt__num">{{ r.total }}</td>
                    <td class="prt__num prt__num--warn">{{ r.warns || '' }}</td>
                    <td class="prt__num prt__num--bad">{{ r.errors || '' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          }
        </section>

        <section class="prt__domain-timeline" aria-label="Domain timeline" data-testid="product-runtime-domain-timeline">
          <header class="prt__section-head">
            <h3>Domain event timeline</h3>
            <span class="prt__section-meta">{{ domainTimeline().length }} entries · grouped by correlationId</span>
          </header>
          @if (domainTimeline().length === 0) {
            <p class="prt__section-empty" data-testid="product-runtime-domain-empty">
              No product-shaped events (order.*, payment.*, render.*, job.*, auth.*, http.request.*) match the filter.
            </p>
          } @else {
            <ol class="prt__domain-list">
              @for (e of domainTimeline(); track e.id) {
                <li class="prt__domain-row"
                    [class.prt__domain-row--bad]="e.level === 'Error' || e.level === 'Fatal' || e.status === 'Failed' || e.status === 'Timeout'"
                    [attr.data-testid]="'product-runtime-domain-row'"
                    [attr.data-event]="e.event"
                    [attr.data-correlation]="e.correlationId">
                  <span class="prt__domain-time">{{ formatTime(e.timestamp) }}</span>
                  <code class="prt__domain-event">{{ e.event }}</code>
                  <span class="prt__domain-meta">{{ e.subsystem }}</span>
                  @if (e.correlationId) {
                    <span class="prt__domain-corr">corr {{ e.correlationId }}</span>
                  }
                  @if (e.durationMs !== null) {
                    <span class="prt__domain-dur">{{ formatMs(e.durationMs) }}</span>
                  }
                </li>
              }
            </ol>
          }
        </section>

        @if (selectedEvent(); as sel) {
          <section class="prt__detail" aria-label="Selected event detail" data-testid="product-runtime-detail">
            <header class="prt__section-head">
              <h3>Event · <code>{{ sel.event }}</code></h3>
              <span class="prt__spacer"></span>
              <button type="button"
                      class="prt__refresh"
                      data-testid="product-runtime-detail-close"
                      (click)="clearSelection()">Close</button>
            </header>
            <dl class="prt__detail-meta">
              <div><dt>Time</dt><dd>{{ formatTime(sel.timestamp) }}</dd></div>
              <div><dt>Level</dt><dd>{{ sel.level }}</dd></div>
              <div><dt>Subsystem</dt><dd>{{ sel.subsystem }}</dd></div>
              @if (sel.operation) { <div><dt>Operation</dt><dd>{{ sel.operation }}</dd></div> }
              @if (sel.status) { <div><dt>Status</dt><dd>{{ sel.status }}</dd></div> }
              @if (sel.duration) { <div><dt>Duration</dt><dd>{{ formatMs(sel.duration.ms) }}</dd></div> }
              @if (sel.correlationId) { <div><dt>Correlation</dt><dd><code>{{ sel.correlationId }}</code></dd></div> }
              @if (sel.jobId) { <div><dt>Job</dt><dd><code>{{ sel.jobId }}</code></dd></div> }
              @if (sel.runId) { <div><dt>Run</dt><dd><code>{{ sel.runId }}</code></dd></div> }
              @if (sel.taskId) { <div><dt>Task</dt><dd><code>{{ sel.taskId }}</code></dd></div> }
              @if (sel.tags && sel.tags.length) {
                <div><dt>Tags</dt><dd>
                  @for (t of sel.tags; track t) { <span class="prt__tag">{{ t }}</span> }
                </dd></div>
              }
            </dl>
            @if (sel.error) {
              <div class="prt__detail-error" data-testid="product-runtime-detail-error">
                <strong>{{ sel.error.type || 'error' }}</strong>: {{ sel.error.message }}
                @if (sel.error.code) { <code class="prt__tag">{{ sel.error.code }}</code> }
              </div>
            }
            <details class="prt__detail-json" open>
              <summary>Raw JSON</summary>
              <pre class="prt__detail-pre" data-testid="product-runtime-detail-json">{{ formatJson(sel) }}</pre>
            </details>
          </section>
        }
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .prt { color: #cdd6f4; font-size: 0.85rem; }
    .prt__head { margin-bottom: 14px; }
    .prt__title-row {
      display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap;
    }
    .prt__title {
      margin: 0; font-size: 1.05rem; font-weight: 600; color: #f8fafc;
      display: flex; align-items: center; gap: 8px;
    }
    .prt__icon { font-size: 1rem; }
    .prt__sub { margin: 4px 0 0; color: #a6adc8; font-size: 0.82rem; }
    .prt__spacer { flex: 1; }
    .prt__chip {
      padding: 2px 8px; border-radius: 999px;
      background: rgba(255,255,255,0.06); color: #bac2de;
      font-size: 0.72rem; letter-spacing: 0.02em;
    }
    .prt__chip--info { color: #94e2d5; background: rgba(148,226,213,0.12); }
    .prt__chip--fixture { color: #fab387; background: rgba(250,179,135,0.14); }
    .prt__chip--retry { color: #94e2d5; background: rgba(148,226,213,0.12); }
    .prt__refresh {
      background: rgba(255,255,255,0.04); color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 6px; padding: 4px 10px; font: inherit; font-size: 0.78rem; cursor: pointer;
    }
    .prt__refresh:hover { background: rgba(255,255,255,0.10); }

    .prt__error {
      margin: 0 0 12px; padding: 8px 12px; border-radius: 6px;
      border: 1px solid rgba(248,113,113,0.40); background: rgba(248,113,113,0.10);
      color: #fda4af; font-size: 0.82rem;
    }
    .prt__empty {
      padding: 28px 20px; text-align: center; color: #a6adc8;
      border: 1px dashed #313244; border-radius: 6px; background: rgba(0,0,0,0.18);
    }
    .prt__empty p { margin: 0 0 8px; }
    .prt__empty-detail { color: #6c7086; font-size: 0.80rem; }
    .prt__empty-detail code {
      background: rgba(255,255,255,0.05); padding: 1px 4px; border-radius: 3px; color: #c4b5fd;
    }
    .prt__fixture-btn {
      margin-top: 10px;
      background: rgba(148,226,213,0.10); color: #94e2d5;
      border: 1px solid rgba(148,226,213,0.40); border-radius: 6px;
      padding: 6px 14px; font: inherit; font-size: 0.82rem; cursor: pointer;
    }
    .prt__fixture-btn:hover { background: rgba(148,226,213,0.20); }

    .prt__filters {
      display: flex; flex-wrap: wrap; gap: 8px 12px; align-items: end;
      padding: 10px 12px; border: 1px solid #313244; border-radius: 6px;
      background: #1a1a26; margin-bottom: 14px;
    }
    .prt__field { display: flex; flex-direction: column; gap: 2px; font-size: 0.74rem; }
    .prt__field span { color: #6c7086; letter-spacing: 0.04em; text-transform: uppercase; }
    .prt__field select, .prt__field input {
      background: rgba(0,0,0,0.30); color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12); border-radius: 5px;
      padding: 4px 8px; font: inherit; font-size: 0.82rem; min-width: 130px;
    }
    .prt__field-reset {
      background: transparent; color: #a6adc8; border: 1px solid transparent;
      border-radius: 6px; padding: 4px 10px; font: inherit; font-size: 0.78rem; cursor: pointer;
    }
    .prt__field-reset:hover { background: rgba(255,255,255,0.06); color: #cdd6f4; }

    .prt__counters {
      display: grid; grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
      gap: 8px; margin-bottom: 14px;
    }
    .prt__counter {
      padding: 8px 10px; border: 1px solid #313244; background: #1a1a26;
      border-radius: 6px; display: flex; flex-direction: column; gap: 2px;
    }
    .prt__counter--warn { border-color: rgba(249,226,175,0.45); }
    .prt__counter--high { border-color: rgba(248,113,113,0.50); }
    .prt__counter-num {
      font-size: 1.20rem; font-weight: 700; color: #f8fafc; font-variant-numeric: tabular-nums;
    }
    .prt__counter--warn .prt__counter-num { color: #f9e2af; }
    .prt__counter--high .prt__counter-num { color: #f87171; }
    .prt__counter-label {
      color: #6c7086; font-size: 0.70rem; text-transform: uppercase; letter-spacing: 0.04em;
    }

    .prt__section-head {
      display: flex; align-items: baseline; gap: 10px; margin: 0 0 8px;
    }
    .prt__section-head h3 {
      margin: 0; font-size: 0.92rem; font-weight: 600; color: #cbd5e1;
    }
    .prt__section-meta { color: #6c7086; font-size: 0.74rem; }
    .prt__section-meta code {
      background: rgba(255,255,255,0.05); padding: 1px 4px; border-radius: 3px; color: #c4b5fd;
    }
    .prt__section-empty {
      margin: 0; padding: 10px 12px; color: #6c7086; font-size: 0.80rem; font-style: italic;
      border: 1px dashed #313244; border-radius: 6px; background: rgba(0,0,0,0.18);
    }

    .prt__warnings { margin-bottom: 14px; }
    .prt__warning-list {
      list-style: none; margin: 0; padding: 0;
      border: 1px solid rgba(249,226,175,0.30); background: rgba(249,226,175,0.05);
      border-radius: 6px; overflow: hidden;
    }
    .prt__warning-row {
      display: grid; grid-template-columns: minmax(160px,1fr) minmax(120px,1fr) 2fr;
      gap: 8px; padding: 6px 10px; border-bottom: 1px solid rgba(255,255,255,0.04);
      font-size: 0.78rem;
    }
    .prt__warning-row:last-child { border-bottom: none; }
    .prt__warning-where { color: #fab387; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .prt__warning-reason { color: #f9e2af; }
    .prt__warning-raw {
      color: #6c7086; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
      font-family: ui-monospace, monospace;
    }

    .prt__events-section, .prt__error-groups, .prt__latency,
    .prt__counters-table, .prt__domain-timeline { margin-bottom: 14px; }
    .prt__events-scroll {
      overflow-x: auto; border: 1px solid #313244; border-radius: 6px; background: #1a1a26;
    }
    table.prt__events-table, table.prt__group-table {
      width: 100%; border-collapse: collapse; font-size: 0.80rem;
    }
    .prt__events-table th, .prt__events-table td,
    .prt__group-table th, .prt__group-table td {
      padding: 6px 8px; text-align: left;
      border-bottom: 1px solid rgba(255,255,255,0.04);
    }
    .prt__group-table {
      border: 1px solid #313244; border-radius: 6px; background: #1a1a26;
      overflow: hidden;
    }
    .prt__events-table th, .prt__group-table th {
      color: #6c7086; font-size: 0.70rem; text-transform: uppercase;
      letter-spacing: 0.04em; font-weight: 500;
    }
    .prt__events-table tbody tr { cursor: pointer; }
    .prt__events-table tbody tr:hover { background: rgba(255,255,255,0.04); }
    .prt__events-row--active { background: rgba(196,181,253,0.10) !important; }
    .prt__events-cell--time { font-variant-numeric: tabular-nums; color: #a6adc8; white-space: nowrap; }
    .prt__events-cell--num { font-variant-numeric: tabular-nums; }
    .prt__events-cell--ids code {
      background: rgba(255,255,255,0.05); padding: 1px 4px; border-radius: 3px; font-size: 0.74rem;
    }
    .prt__events-sep { color: #45475a; padding: 0 4px; }
    .prt__num { font-variant-numeric: tabular-nums; }
    .prt__num--warn { color: #f9e2af; }
    .prt__num--bad { color: #fca5a5; }
    .prt__group-sample {
      max-width: 360px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
      color: #a6adc8;
    }

    .prt__level {
      padding: 1px 6px; border-radius: 3px; font-size: 0.72rem;
      background: rgba(255,255,255,0.05); color: #bac2de;
    }
    .prt__level--warn { background: rgba(249,226,175,0.16); color: #f9e2af; }
    .prt__level--error { background: rgba(248,113,113,0.20); color: #fca5a5; font-weight: 600; }
    .prt__status {
      padding: 1px 6px; border-radius: 3px; font-size: 0.72rem;
      background: rgba(255,255,255,0.05); color: #bac2de;
    }
    .prt__status--ok { color: #6ee7b7; }
    .prt__status--bad { color: #fca5a5; font-weight: 600; }

    .prt__domain-list {
      list-style: none; margin: 0; padding: 6px 8px;
      border: 1px solid #313244; border-radius: 6px; background: #1a1a26;
      display: flex; flex-direction: column; gap: 4px;
    }
    .prt__domain-row {
      display: flex; flex-wrap: wrap; gap: 8px; align-items: center;
      padding: 4px 6px; border-radius: 4px; font-size: 0.80rem;
      border-left: 2px solid transparent;
    }
    .prt__domain-row--bad { border-left-color: rgba(248,113,113,0.65); background: rgba(248,113,113,0.05); }
    .prt__domain-time {
      color: #a6adc8; font-variant-numeric: tabular-nums; white-space: nowrap;
      min-width: 132px;
    }
    .prt__domain-event { color: #c4b5fd; }
    .prt__domain-meta { color: #6c7086; font-size: 0.74rem; }
    .prt__domain-corr {
      color: #94e2d5; font-size: 0.74rem;
      background: rgba(148,226,213,0.10); padding: 1px 6px; border-radius: 3px;
    }
    .prt__domain-dur { color: #a6adc8; font-variant-numeric: tabular-nums; font-size: 0.74rem; }

    .prt__detail {
      margin-top: 6px; padding: 12px 14px;
      border: 1px solid #313244; border-radius: 6px; background: #181825;
    }
    .prt__detail-meta {
      display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px;
      margin: 0 0 10px; font-size: 0.80rem;
    }
    .prt__detail-meta > div { display: contents; }
    .prt__detail-meta dt { color: #6c7086; }
    .prt__detail-meta dd { margin: 0; color: #cdd6f4; }
    .prt__detail-meta code {
      font-size: 0.74rem; color: #c4b5fd;
      background: rgba(255,255,255,0.05); padding: 1px 4px; border-radius: 3px;
    }
    .prt__detail-error {
      margin: 0 0 10px; padding: 8px 10px;
      border: 1px solid rgba(248,113,113,0.45); background: rgba(248,113,113,0.10);
      color: #fda4af; border-radius: 4px; font-size: 0.82rem;
    }
    .prt__detail-json summary { cursor: pointer; color: #6c7086; font-size: 0.78rem; }
    .prt__detail-pre {
      max-height: 320px; overflow: auto; padding: 8px 10px;
      background: rgba(0,0,0,0.30); border: 1px solid rgba(255,255,255,0.06);
      border-radius: 4px; font-size: 0.74rem; color: #cdd6f4; white-space: pre-wrap;
    }
    .prt__tag {
      display: inline-block; margin-right: 4px; padding: 1px 6px; border-radius: 3px;
      background: rgba(255,255,255,0.05); color: #a6adc8; font-size: 0.72rem;
    }
  `],
})
export class ProjectProductRuntimePanelComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly runtime = inject(ProductRuntimeService);

  readonly Math = Math;
  readonly levels: ReadonlyArray<ProductRuntimeLevel> = PRODUCT_RUNTIME_LEVELS;
  readonly rangeOptions = RANGE_OPTIONS;

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly events = signal<ProductRuntimeEvent[]>([]);
  readonly warnings = signal<RuntimeEventParseWarning[]>([]);
  readonly usingFixture = signal<boolean>(false);
  readonly selectedKey = signal<string | null>(null);

  filter: FilterState = { ...DEFAULT_FILTER };
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
    this.runtime.getEvents(name).subscribe({
      next: (resp) => {
        if (this.usingFixture()) {
          this.loading.set(false);
          return;
        }
        this.events.set(resp.events ?? []);
        this.warnings.set(resp.warnings ?? []);
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
    const sample = ProductRuntimeFixture.sample(name);
    this.events.set(sample.events);
    this.warnings.set(sample.warnings);
    this.usingFixture.set(true);
    this.loading.set(false);
    this.loadError.set(null);
  }

  resetFilters(): void {
    this.filter = { ...DEFAULT_FILTER };
    this.filterTick.update(v => v + 1);
    this.selectedKey.set(null);
  }

  onFilterChanged(): void {
    this.filterTick.update(v => v + 1);
  }

  onRangeChanged(value: string | number): void {
    const n = typeof value === 'number' ? value : Number(value);
    this.filter.rangeHours = Number.isFinite(n) ? n : 0;
    this.filterTick.update(v => v + 1);
  }

  select(e: ProductRuntimeEvent): void {
    this.selectedKey.set(this.rowKey(e));
  }
  clearSelection(): void { this.selectedKey.set(null); }

  rowKey(e: ProductRuntimeEvent): string {
    return `${e.timestamp}|${e.event}|${e.subsystem}|${e.operation ?? ''}|${e.correlationId ?? ''}`;
  }

  readonly sourceLabel = computed(() => {
    if (this.usingFixture()) return 'Fixture';
    const total = this.events().length;
    return total > 0 ? 'Live' : 'Empty';
  });

  readonly rangeStart = computed(() => {
    void this.filterTick();
    if (this.filter.rangeHours <= 0) {
      const evs = this.events();
      if (!evs.length) return new Date().toISOString();
      let min = evs[0].timestamp;
      for (const e of evs) if (e.timestamp < min) min = e.timestamp;
      return min;
    }
    return new Date(Date.now() - this.filter.rangeHours * 3600 * 1000).toISOString();
  });
  readonly rangeEnd = computed(() => new Date().toISOString());

  readonly filtered = computed<ProductRuntimeEvent[]>(() => {
    void this.filterTick();
    const f = this.filter;
    const start = this.rangeStart();
    const end = this.rangeEnd();
    const minRank = f.level ? LEVEL_RANK[f.level] : -1;
    return this.events()
      .filter(e => {
        if (e.timestamp < start || e.timestamp > end) return false;
        if (minRank >= 0 && LEVEL_RANK[e.level] < minRank) return false;
        if (f.event && e.event !== f.event) return false;
        if (f.subsystem && e.subsystem !== f.subsystem) return false;
        if (f.jobId && e.jobId !== f.jobId) return false;
        if (f.runId && e.runId !== f.runId.trim()) return false;
        if (f.correlationId && e.correlationId !== f.correlationId.trim()) return false;
        return true;
      })
      .sort((a, b) => (a.timestamp < b.timestamp ? 1 : -1));
  });

  readonly filteredHead = computed(() => this.filtered().slice(0, 50));

  readonly eventOptions = computed(() => {
    const set = new Set<string>();
    for (const e of this.events()) set.add(e.event);
    return Array.from(set).sort();
  });
  readonly subsystemOptions = computed(() => {
    const set = new Set<string>();
    for (const e of this.events()) set.add(e.subsystem);
    return Array.from(set).sort();
  });
  readonly jobOptions = computed(() => {
    const set = new Set<string>();
    for (const e of this.events()) if (e.jobId) set.add(e.jobId);
    return Array.from(set).sort();
  });

  readonly countChips = computed<CountChip[]>(() => {
    const evs = this.filtered();
    let errors = 0, warns = 0;
    const subBuckets = new Map<string, number>();
    const corrSet = new Set<string>();
    const okMs: number[] = [];
    for (const e of evs) {
      if (e.level === 'Error' || e.level === 'Fatal') errors++;
      else if (e.level === 'Warn') warns++;
      subBuckets.set(e.subsystem, (subBuckets.get(e.subsystem) ?? 0) + 1);
      if (e.correlationId) corrSet.add(e.correlationId);
      if (e.status === 'Ok' && e.duration && Number.isFinite(e.duration.ms)) {
        okMs.push(e.duration.ms);
      }
    }
    let topSub = '–';
    let topCount = 0;
    for (const [k, v] of subBuckets) {
      if (v > topCount) { topSub = k; topCount = v; }
    }
    const p95 = percentile(okMs, 0.95);
    const p95Label = p95 === null ? '–' : formatMsStatic(p95);
    const malformed = this.warnings().length;
    return [
      { testid: 'product-runtime-counter-total', label: 'Events', value: evs.length, tone: 'neutral' },
      { testid: 'product-runtime-counter-errors', label: 'Errors', value: errors, tone: errors > 0 ? 'high' : 'neutral' },
      { testid: 'product-runtime-counter-warns', label: 'Warnings', value: warns, tone: warns > 0 ? 'warn' : 'neutral' },
      { testid: 'product-runtime-counter-p95', label: 'p95 latency', value: p95Label, tone: 'neutral' },
      { testid: 'product-runtime-counter-top-sub', label: 'Top subsystem', value: topCount > 0 ? `${topSub} · ${topCount}` : '–', tone: 'neutral' },
      { testid: 'product-runtime-counter-correlations', label: 'Correlations', value: corrSet.size, tone: 'neutral' },
      { testid: 'product-runtime-counter-malformed', label: 'Malformed lines', value: malformed, tone: malformed > 0 ? 'warn' : 'neutral' },
    ];
  });

  readonly errorGroups = computed<ErrorGroup[]>(() => {
    const map = new Map<string, ErrorGroup>();
    for (const e of this.filtered()) {
      if (e.level !== 'Error' && e.level !== 'Fatal') continue;
      const errType = e.error?.type ?? '';
      const key = `${e.event}|${errType}`;
      const existing = map.get(key);
      if (existing) {
        existing.count++;
        if (e.timestamp > existing.lastSeen) existing.lastSeen = e.timestamp;
      } else {
        map.set(key, {
          key,
          event: e.event,
          errorType: errType,
          count: 1,
          lastSeen: e.timestamp,
          retryable: e.error?.retryable ?? null,
          sampleMessage: e.error?.message ?? '',
          jobId: e.jobId ?? null,
          runId: e.runId ?? null,
        });
      }
    }
    return Array.from(map.values()).sort((a, b) => b.count - a.count || (a.lastSeen < b.lastSeen ? 1 : -1));
  });

  readonly latencyRows = computed<LatencyRow[]>(() => {
    const map = new Map<string, { sub: string; op: string; samples: number[] }>();
    for (const e of this.filtered()) {
      if (e.status !== 'Ok' || !e.duration || !Number.isFinite(e.duration.ms)) continue;
      const op = e.operation ?? '(none)';
      const key = `${e.subsystem}|${op}`;
      let bucket = map.get(key);
      if (!bucket) { bucket = { sub: e.subsystem, op, samples: [] }; map.set(key, bucket); }
      bucket.samples.push(e.duration.ms);
    }
    const rows: LatencyRow[] = [];
    for (const [key, b] of map) {
      const sorted = [...b.samples].sort((a, c) => a - c);
      rows.push({
        key,
        subsystem: b.sub,
        operation: b.op,
        count: sorted.length,
        p50: percentile(sorted, 0.50) ?? 0,
        p95: percentile(sorted, 0.95) ?? 0,
        max: sorted[sorted.length - 1] ?? 0,
      });
    }
    return rows.sort((a, b) => b.p95 - a.p95);
  });

  readonly counterRows = computed<CounterRow[]>(() => {
    const map = new Map<string, CounterRow>();
    for (const e of this.filtered()) {
      const key = `${e.subsystem}|${e.event}`;
      const existing = map.get(key);
      if (existing) {
        existing.total++;
        if (e.level === 'Warn') existing.warns++;
        if (e.level === 'Error' || e.level === 'Fatal') existing.errors++;
      } else {
        map.set(key, {
          key,
          subsystem: e.subsystem,
          event: e.event,
          total: 1,
          warns: e.level === 'Warn' ? 1 : 0,
          errors: e.level === 'Error' || e.level === 'Fatal' ? 1 : 0,
        });
      }
    }
    return Array.from(map.values()).sort((a, b) => b.total - a.total);
  });

  readonly domainTimeline = computed<DomainTimelineEntry[]>(() => {
    const out: DomainTimelineEntry[] = [];
    for (const e of this.filtered()) {
      if (!DOMAIN_TIMELINE_ALLOW_PREFIXES.some(p => e.event.startsWith(p))) continue;
      out.push({
        id: this.rowKey(e),
        timestamp: e.timestamp,
        event: e.event,
        subsystem: e.subsystem,
        level: e.level,
        status: e.status ?? null,
        correlationId: e.correlationId ?? null,
        durationMs: e.duration?.ms ?? null,
        groupKey: e.correlationId ?? e.event,
      });
    }
    // Sort by correlation group for readability, then by time within group.
    return out.sort((a, b) => {
      if (a.groupKey !== b.groupKey) return a.groupKey < b.groupKey ? -1 : 1;
      return a.timestamp < b.timestamp ? -1 : 1;
    });
  });

  readonly selectedEvent = computed<ProductRuntimeEvent | null>(() => {
    const key = this.selectedKey();
    if (!key) return null;
    return this.events().find(e => this.rowKey(e) === key) ?? null;
  });

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch { return iso; }
  }
  formatMs(ms: number): string { return formatMsStatic(ms); }
  formatJson(e: ProductRuntimeEvent): string {
    try { return JSON.stringify(e, null, 2); } catch { return String(e); }
  }
}

function percentile(sorted: number[], p: number): number | null {
  if (sorted.length === 0) return null;
  const a = [...sorted].sort((x, y) => x - y);
  const idx = Math.min(a.length - 1, Math.max(0, Math.floor(p * (a.length - 1))));
  return a[idx];
}

function formatMsStatic(ms: number): string {
  if (!Number.isFinite(ms)) return '–';
  if (ms < 1) return ms.toFixed(2) + 'ms';
  if (ms < 1000) return Math.round(ms) + 'ms';
  return (ms / 1000).toFixed(2) + 's';
}
