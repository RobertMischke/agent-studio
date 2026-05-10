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
  templateUrl: './tool-burst-chip.component.html',
  styleUrl: './tool-burst-chip.component.scss'
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
