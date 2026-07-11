import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import {
  clampPct,
  diskUsedPct,
  formatDisk,
  formatMemory,
  hostRoleLabel,
  hostStatusLabel,
  hostStatusTone,
  meterTone,
  ramUsedPct,
  relativeHeartbeat,
  type HostActionKind,
  type HostTelemetryPoint,
  type MeterTone,
  type RemoteHost,
} from '../../models/remote-host.model';

/** One meter row (RAM / CPU / Disk) resolved for the template. */
interface Meter {
  key: string;
  label: string;
  detail: string;
  pct: number;
  tone: MeterTone;
}

/**
 * One execution location in the Remote-Hosts list (AGT-1921): heartbeat status,
 * role, capabilities, system vitals (RAM / CPU / Disk meters), per-CLI quota
 * chips, and the Re-Probe / Drain / Retire actions.
 *
 * Status is encoded with a dot + a badge, and acute states (degraded / offline)
 * additionally wash the whole card with a warn / error tint - never a left
 * accent bar (style-guide R1). History (retired / draining) renders calm (R4).
 */
@Component({
  selector: 'app-remote-host-card',
  standalone: true,
  imports: [DatePipe],
  templateUrl: './remote-host-card.html',
  styleUrl: './remote-host-card.scss',
  host: { '[attr.data-tone]': 'tone()', '[attr.data-host]': 'host().id' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RemoteHostCardComponent {
  readonly host = input.required<RemoteHost>();
  /** Injected clock so the relative heartbeat label ticks without a per-card timer. */
  readonly now = input<number>(Date.now());
  readonly action = output<{ kind: HostActionKind; id: string }>();
  readonly setup = output<RemoteHost>();

  readonly tone = computed(() => hostStatusTone(this.host().status));
  readonly statusLabel = computed(() => hostStatusLabel(this.host().status));
  readonly roleLabel = computed(() => hostRoleLabel(this.host().role));
  readonly heartbeatLabel = computed(() => relativeHeartbeat(this.host().lastHeartbeatAt, this.now()));
  readonly retired = computed(() => this.host().status === 'retired');
  readonly telemetryWindow = signal<'1h' | '6h' | '48h' | '14d'>('6h');
  readonly telemetryPoints = computed(() => {
    const hours = { '1h': 1, '6h': 6, '48h': 48, '14d': 336 }[this.telemetryWindow()];
    const cutoff = this.now() - hours * 60 * 60 * 1000;
    return (this.host().telemetry?.points ?? []).filter(point => Date.parse(point.timestamp) >= cutoff);
  });
  readonly chartRows = computed(() => {
    const points = this.telemetryPoints();
    return [
      { key: 'cpu', label: 'CPU', value: (p: HostTelemetryPoint) => p.cpuPercent, max: 100 },
      { key: 'memory', label: 'Memory', value: (p: HostTelemetryPoint) => p.memoryUsedBytes !== null && p.memoryTotalBytes ? p.memoryUsedBytes * 100 / p.memoryTotalBytes : null, max: 100 },
      { key: 'load', label: 'Load / cores', value: (p: HostTelemetryPoint) => p.load1, max: Math.max(1, ...points.map(p => p.cpuCores), ...points.map(p => p.load1 ?? 0)) },
      { key: 'slots', label: 'Active slots', value: (p: HostTelemetryPoint) => p.activeSlots, max: Math.max(1, ...points.map(p => p.activeSlots)) },
    ].map(row => ({ ...row, path: sparkline(points, row.value, row.max) }));
  });
  readonly latestContext = computed(() => {
    const point = this.telemetryPoints().at(-1);
    return point ? `${point.activeSlots} active slots · load ${(point.load1 ?? 0).toFixed(1)} of ${point.cpuCores} cores` : '';
  });

  readonly meters = computed<Meter[]>(() => {
    const h = this.host();
    const s = h.stats;
    if (!s) return [];
    const ram = ramUsedPct(s) ?? 0;
    const disk = diskUsedPct(s) ?? 0;
    const load = Math.round(clampPct(s.cpuLoadPct));
    return [
      {
        key: 'ram',
        label: 'RAM',
        detail: `${formatMemory(s.ramTotalMb - s.ramFreeMb)} / ${formatMemory(s.ramTotalMb)}`,
        pct: ram,
        tone: meterTone(ram),
      },
      {
        key: 'cpu',
        label: 'CPU',
        detail: `${s.cpuCores} cores · ${s.cpuModel}`,
        pct: load,
        tone: meterTone(load),
      },
      {
        key: 'disk',
        label: 'Disk',
        detail: `${formatDisk(s.diskTotalGb - s.diskFreeGb)} / ${formatDisk(s.diskTotalGb)}`,
        pct: disk,
        tone: meterTone(disk),
      },
    ];
  });

  cliIcon(t: CliType): string { return cliTypeIcon(t); }
  cliLabel(t: CliType): string { return cliTypeLabel(t); }
  quotaTone(pct: number | null): MeterTone { return meterTone(pct); }

  emit(kind: HostActionKind): void {
    if (this.host().busyAction) return;
    this.action.emit({ kind, id: this.host().id });
  }

  requestSetup(): void {
    const host = this.host();
    if (host.role !== 'remote' || host.status === 'retired' || host.busyAction) return;
    this.setup.emit(host);
  }

  selectTelemetryWindow(window: '1h' | '6h' | '48h' | '14d'): void { this.telemetryWindow.set(window); }
}

function sparkline(points: readonly HostTelemetryPoint[], value: (point: HostTelemetryPoint) => number | null, max: number): string {
  if (points.length < 2) return '';
  return points.map((point, index) => ({ index, value: value(point) }))
    .filter(item => item.value !== null)
    .map(item => `${(item.index * 100 / (points.length - 1)).toFixed(1)},${(28 - Math.max(0, Math.min(1, item.value! / max)) * 26).toFixed(1)}`)
    .join(' ');
}
