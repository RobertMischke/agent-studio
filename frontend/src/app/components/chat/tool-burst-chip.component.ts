import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import type { ToolBurstEvent, ToolFamily } from './conversation-event';

/**
 * Dense, collapsed-by-default renderer for `ToolBurst` events in the
 * next-gen chat (`Frontend:NextGenChat`). One ToolBurst maps to one row;
 * the row expands into a per-tool details list with file, test, and
 * artifact rollups.
 *
 * The component is intentionally presentational: it takes a `ToolBurstEvent`
 * and emits no events back to the host. Hosts that need "open in Trace"
 * read `event().rawRange` themselves; the chip surfaces the raw range
 * inside the expanded details so the user can see what the row maps to.
 *
 * Visibility is gated upstream by `Frontend:NextGenChat`; the chip itself
 * does not read the flag.
 */
@Component({
  selector: 'app-tool-burst-chip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <article class="burst"
             [class.burst--error]="failed()"
             [class.burst--open]="open()"
             [attr.data-density]="density()"
             [attr.data-failed]="failed() ? 'true' : 'false'"
             data-testid="tool-burst-chip">
      <button type="button"
              class="burst__row"
              [attr.aria-expanded]="open()"
              data-testid="tool-burst-row"
              (click)="toggle()">
        <span class="burst__icon" aria-hidden="true">{{ leadingIcon() }}</span>
        <span class="burst__count">
          <strong data-testid="tool-burst-total">Tools {{ event().count }}</strong>
          @if (failed()) {
            <em class="burst__fail" data-testid="tool-burst-failures">{{ event().failures }} failed</em>
          }
        </span>
        <span class="burst__families" data-testid="tool-burst-families">
          @for (chip of familyChips(); track chip.family) {
            <span class="burst__chip" [attr.data-family]="chip.family">
              {{ chip.label }} {{ chip.count }}
            </span>
          }
          @if (event().durationMs && event().durationMs! > 0) {
            <span class="burst__chip burst__chip--time" data-testid="tool-burst-duration">
              {{ formattedDuration() }}
            </span>
          }
          @if (artifactCount() > 0) {
            <span class="burst__chip burst__chip--art" data-testid="tool-burst-artifacts">
              {{ artifactCount() }} artifact{{ artifactCount() === 1 ? '' : 's' }}
            </span>
          }
          @if (fileCount() > 0) {
            <span class="burst__chip burst__chip--file" data-testid="tool-burst-files">
              {{ fileCount() }} file{{ fileCount() === 1 ? '' : 's' }}
            </span>
          }
        </span>
        <span class="burst__caret" aria-hidden="true">{{ open() ? 'v' : '>' }}</span>
      </button>

      @if (open()) {
        <section class="burst__details" data-testid="tool-burst-details">
          <header class="burst__details-head">
            <span>Tool details</span>
            <code data-testid="tool-burst-range">
              {{ event().rawRange.source }}:{{ event().rawRange.start }}-{{ event().rawRange.end }}
            </code>
          </header>

          <ul class="burst__rows" data-testid="tool-burst-table">
            @for (row of detailRows(); track row.family + row.target) {
              <li class="burst__detail-row" [attr.data-family]="row.family" [attr.data-status]="row.status">
                <span class="burst__detail-time">{{ formatTime(event().timestamp) }}</span>
                <code class="burst__detail-tool">{{ familyLabel(row.family) }}</code>
                <span class="burst__detail-target">{{ row.target }}</span>
                <strong class="burst__detail-status">{{ row.statusLabel }}</strong>
                <span class="burst__detail-meta">{{ row.meta }}</span>
              </li>
            }
          </ul>

          @if ((event().tests?.length ?? 0) > 0) {
            <section class="burst__sub" data-testid="tool-burst-tests">
              <header>Tests</header>
              <ul>
                @for (test of event().tests ?? []; track test.command) {
                  <li [attr.data-status]="test.status">
                    <strong>{{ test.status }}</strong>
                    <code>{{ test.command }}</code>
                  </li>
                }
              </ul>
            </section>
          }

          @if ((event().artifacts?.length ?? 0) > 0) {
            <section class="burst__sub" data-testid="tool-burst-artifacts-list">
              <header>Artifacts</header>
              <ul>
                @for (artifact of event().artifacts ?? []; track artifact) {
                  <li><code>{{ artifact }}</code></li>
                }
              </ul>
            </section>
          }
        </section>
      }
    </article>
  `,
  styles: [`
    :host { display: block; }

    .burst {
      border: 1px solid color-mix(in srgb, currentColor 18%, transparent);
      border-radius: 8px;
      background: color-mix(in srgb, currentColor 4%, transparent);
      font-family: 'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif;
      font-size: 12.5px;
      line-height: 1.45;
      transition: border-color 120ms ease, background 120ms ease;
    }
    .burst--open {
      border-color: color-mix(in srgb, currentColor 32%, transparent);
      background: color-mix(in srgb, currentColor 6%, transparent);
    }
    .burst--error {
      border-color: rgba(239, 68, 68, 0.55);
      background: rgba(239, 68, 68, 0.08);
    }
    .burst--error.burst--open {
      background: rgba(239, 68, 68, 0.12);
    }

    .burst__row {
      width: 100%;
      display: grid;
      grid-template-columns: auto auto 1fr auto;
      align-items: center;
      gap: 10px;
      padding: 6px 10px;
      background: transparent;
      border: 0;
      color: inherit;
      cursor: pointer;
      text-align: left;
    }
    .burst__row:hover { background: color-mix(in srgb, currentColor 8%, transparent); border-radius: 8px; }
    .burst__row:focus-visible {
      outline: 2px solid #6366f1;
      outline-offset: -2px;
      border-radius: 8px;
    }

    .burst__icon { font-size: 14px; line-height: 1; opacity: 0.9; }
    .burst__count { display: flex; gap: 8px; align-items: baseline; }
    .burst__count strong { font-weight: 700; letter-spacing: 0.01em; }
    .burst__fail {
      font-style: normal;
      font-weight: 700;
      color: #f87171;
      font-size: 11.5px;
    }
    .burst__families {
      display: flex;
      flex-wrap: wrap;
      gap: 5px;
      align-items: center;
      min-width: 0;
    }
    .burst__chip {
      display: inline-flex;
      align-items: center;
      gap: 3px;
      padding: 1px 8px;
      border-radius: 999px;
      background: color-mix(in srgb, currentColor 10%, transparent);
      border: 1px solid color-mix(in srgb, currentColor 18%, transparent);
      font-size: 11px;
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }
    .burst__chip--time { background: color-mix(in srgb, currentColor 4%, transparent); }
    .burst__chip--art { background: rgba(56,189,248,0.14); border-color: rgba(125,211,252,0.45); }
    .burst__chip--file { background: rgba(167,139,250,0.14); border-color: rgba(196,181,253,0.45); }
    .burst__chip[data-family='read'] { background: rgba(94,234,212,0.14); border-color: rgba(45,212,191,0.45); }
    .burst__chip[data-family='search'] { background: rgba(250,204,21,0.14); border-color: rgba(234,179,8,0.45); }
    .burst__chip[data-family='edit'] { background: rgba(196,181,253,0.16); border-color: rgba(167,139,250,0.45); }
    .burst__chip[data-family='command'] { background: rgba(248,113,113,0.14); border-color: rgba(239,68,68,0.45); }
    .burst__chip[data-family='task'] { background: rgba(125,211,252,0.16); border-color: rgba(56,189,248,0.45); }
    .burst__chip[data-family='todo'] { background: rgba(186,230,253,0.16); border-color: rgba(56,189,248,0.45); }

    .burst__caret {
      font-size: 11px;
      opacity: 0.65;
      font-variant-numeric: tabular-nums;
    }

    .burst__details {
      padding: 6px 10px 10px;
      border-top: 1px solid color-mix(in srgb, currentColor 12%, transparent);
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .burst__details-head {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      gap: 8px;
      font-size: 11px;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      opacity: 0.65;
    }
    .burst__details-head code {
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11px;
      opacity: 0.7;
    }

    .burst__rows {
      list-style: none;
      margin: 0;
      padding: 0;
      display: grid;
      gap: 2px;
    }
    .burst__detail-row {
      display: grid;
      grid-template-columns: 64px 80px 1fr auto auto;
      gap: 8px;
      align-items: center;
      padding: 3px 6px;
      border-radius: 6px;
      font-size: 12px;
    }
    .burst__detail-row:nth-child(odd) {
      background: color-mix(in srgb, currentColor 4%, transparent);
    }
    .burst__detail-row[data-status='fail'] {
      background: rgba(239,68,68,0.10);
    }
    .burst__detail-time { font-variant-numeric: tabular-nums; opacity: 0.6; font-size: 11px; }
    .burst__detail-tool {
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11px;
      padding: 1px 6px;
      border-radius: 4px;
      background: color-mix(in srgb, currentColor 10%, transparent);
    }
    .burst__detail-target {
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11.5px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .burst__detail-status { font-size: 11px; letter-spacing: 0.05em; }
    .burst__detail-row[data-status='ok'] .burst__detail-status { color: #10b981; }
    .burst__detail-row[data-status='fail'] .burst__detail-status { color: #f87171; }
    .burst__detail-meta { font-size: 11px; opacity: 0.6; }

    .burst__sub {
      display: flex;
      flex-direction: column;
      gap: 3px;
      padding: 4px 6px;
      border: 1px dashed color-mix(in srgb, currentColor 18%, transparent);
      border-radius: 6px;
    }
    .burst__sub header {
      font-size: 11px;
      letter-spacing: 0.05em;
      text-transform: uppercase;
      opacity: 0.6;
    }
    .burst__sub ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
    .burst__sub li {
      display: flex;
      gap: 8px;
      align-items: baseline;
      font-size: 11.5px;
    }
    .burst__sub li code {
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 11px;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .burst__sub li[data-status='pass'] strong { color: #10b981; }
    .burst__sub li[data-status='fail'] strong { color: #f87171; }
    .burst__sub li[data-status='unknown'] strong { color: #94a3b8; }

    /* Compact density: hide secondary chips first so the row stays one line. */
    .burst[data-density='compact'] .burst__chip--time,
    .burst[data-density='compact'] .burst__chip--file {
      display: none;
    }
    .burst[data-density='compact'] .burst__row {
      grid-template-columns: auto auto 1fr auto;
      padding: 4px 8px;
      gap: 6px;
      font-size: 11.5px;
    }

    /* Mobile collapse: drop the family chips strip; the header still
       shows total + failure count, and the details panel still owns the
       per-tool table. The summary strip carries family rollups elsewhere. */
    @media (max-width: 520px) {
      .burst__row {
        grid-template-columns: auto auto 1fr auto;
      }
      .burst__families .burst__chip:not(.burst__chip--art):not([data-family='__leading__']) {
        display: none;
      }
      .burst__detail-row {
        grid-template-columns: 1fr auto;
        gap: 4px;
      }
      .burst__detail-time,
      .burst__detail-tool,
      .burst__detail-meta {
        display: none;
      }
    }
  `]
})
export class ToolBurstChipComponent {
  readonly event = input.required<ToolBurstEvent>();
  readonly density = input<'comfortable' | 'compact'>('comfortable');
  readonly initialOpen = input<boolean>(false);

  readonly open = signal<boolean>(false);

  constructor() {
    queueMicrotask(() => {
      if (this.initialOpen()) this.open.set(true);
      else if (this.event().collapsedByDefault === false) this.open.set(true);
    });
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  readonly failed = computed(() => (this.event().failures ?? 0) > 0);

  readonly familyChips = computed<{ family: ToolFamily; label: string; count: number }[]>(() => {
    const families = this.event().families ?? {};
    const order: ToolFamily[] = ['read', 'search', 'command', 'edit', 'task', 'todo', 'other'];
    const out: { family: ToolFamily; label: string; count: number }[] = [];
    for (const f of order) {
      const count = families[f];
      if (count && count > 0) out.push({ family: f, label: this.familyLabel(f), count });
    }
    return out;
  });

  readonly leadingIcon = computed(() => {
    if (this.failed()) return '!';
    const top = this.familyChips()[0];
    if (!top) return 'T';
    return iconFor(top.family);
  });

  readonly fileCount = computed(() => this.event().files?.length ?? 0);
  readonly artifactCount = computed(() => this.event().artifacts?.length ?? 0);

  readonly formattedDuration = computed(() => formatBurstDuration(this.event().durationMs ?? 0));

  readonly detailRows = computed<DetailRow[]>(() => {
    const event = this.event();
    const families = event.families ?? {};
    const samples = event.samples ?? {};
    const failures = event.failures ?? 0;
    const rows: DetailRow[] = [];
    const familyOrder: ToolFamily[] = ['read', 'search', 'command', 'edit', 'task', 'todo', 'other'];
    let failuresLeft = failures;
    for (const family of familyOrder) {
      const count = families[family] ?? 0;
      if (count <= 0) continue;
      const sample = samples[family] ?? this.familyLabel(family);
      const familyFailures = Math.min(count, failuresLeft);
      failuresLeft -= familyFailures;
      const status: 'ok' | 'fail' = familyFailures > 0 ? 'fail' : 'ok';
      rows.push({
        family,
        target: sample,
        status,
        statusLabel: status === 'fail' ? 'fail' : 'ok',
        meta: count > 1 ? `x${count}${familyFailures > 0 ? ` - ${familyFailures} fail` : ''}` : (familyFailures > 0 ? '1 fail' : 'ok')
      });
    }
    return rows;
  });

  familyLabel(family: ToolFamily): string {
    switch (family) {
      case 'read': return 'read';
      case 'search': return 'search';
      case 'command': return 'shell';
      case 'edit': return 'edit';
      case 'task': return 'task';
      case 'todo': return 'todo';
      default: return 'tool';
    }
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return '';
      return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
    } catch {
      return '';
    }
  }
}

interface DetailRow {
  family: ToolFamily;
  target: string;
  status: 'ok' | 'fail';
  statusLabel: string;
  meta: string;
}

function iconFor(family: ToolFamily): string {
  switch (family) {
    case 'read': return 'R';
    case 'search': return 'S';
    case 'command': return '$';
    case 'edit': return 'E';
    case 'task': return 'A';
    case 'todo': return 'D';
    default: return 'T';
  }
}

function formatBurstDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) return '';
  if (ms < 1000) return '<1s';
  const totalSec = Math.round(ms / 1000);
  if (totalSec < 60) return `${totalSec}s`;
  const totalMin = Math.floor(totalSec / 60);
  const sec = totalSec % 60;
  if (totalMin < 60) return sec === 0 ? `${totalMin}m` : `${totalMin}m ${sec}s`;
  const hr = Math.floor(totalMin / 60);
  const min = totalMin % 60;
  return min === 0 ? `${hr}h` : `${hr}h ${min}m`;
}
