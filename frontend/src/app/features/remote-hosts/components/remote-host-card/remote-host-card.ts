import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { CapabilityHealthComponent } from '../capability-health/capability-health';
import { GitTokenCapabilityComponent } from '../git-token-capability/git-token-capability';
import { HostWorkloadSummaryComponent } from '../host-workload-summary/host-workload-summary';
import {
  clampPct,
  diskUsedPct,
  formatDisk,
  formatMemory,
  hostRoleLabel,
  hostIsStale,
  hostStatusLabel,
  hostStatusTone,
  meterTone,
  ramUsedPct,
  relativeHeartbeat,
  type HostActionKind,
  type HostTelemetryFinding,
  type HostTelemetryPoint,
  type MeterTone,
  type RemoteHost,
} from '../../models/remote-host.model';
import { freshHostTelemetry, latestHostTelemetry } from '../../models/running-truth';

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
  imports: [
    DatePipe,
    AppTooltipDirective,
    CapabilityHealthComponent,
    GitTokenCapabilityComponent,
    HostWorkloadSummaryComponent,
  ],
  templateUrl: './remote-host-card.html',
  styleUrl: './remote-host-card.scss',
  host: { '[attr.data-tone]': 'tone()', '[attr.data-host]': 'host().id' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RemoteHostCardComponent {
  readonly host = input.required<RemoteHost>();
  /** Board-local process runs or remote leased runs attributed to this host. */
  readonly boardActiveSlots = input(0);
  /** Injected clock so the relative heartbeat label ticks without a per-card timer. */
  readonly now = input<number>(Date.now());
  readonly action = output<{ kind: HostActionKind; id: string }>();
  readonly setup = output<RemoteHost>();

  readonly liveLoading = computed(() => this.host().liveDataState === 'loading');
  readonly liveError = computed(() => this.host().liveDataState === 'error');
  readonly tone = computed(() => this.liveLoading() ? 'idle' : hostStatusTone(this.host().status));
  readonly statusLabel = computed(() => this.liveLoading() ? 'Loading live status' : hostStatusLabel(this.host().status));
  readonly roleLabel = computed(() => hostRoleLabel(this.host().role));
  readonly heartbeatLabel = computed(() => this.liveLoading()
    ? 'loading…'
    : relativeHeartbeat(this.host().lastHeartbeatAt, this.now()));
  readonly retired = computed(() => this.host().status === 'retired');
  readonly stale = computed(() => !this.liveLoading() && hostIsStale(this.host().lastHeartbeatAt, this.now()));
  readonly telemetryWindow = signal<'1h' | '6h' | '48h' | '14d'>('6h');
  readonly latestTelemetry = computed(() => latestHostTelemetry(this.host()));
  readonly liveTelemetry = computed(() => freshHostTelemetry(this.host(), this.now()));
  readonly telemetryStale = computed(() =>
    this.latestTelemetry() !== null && this.liveTelemetry() === null);
  readonly hoveredTelemetryIndex = signal<number | null>(null);
  readonly telemetryPoints = computed(() => {
    const hours = { '1h': 1, '6h': 6, '48h': 48, '14d': 336 }[this.telemetryWindow()];
    const cutoff = this.now() - hours * 60 * 60 * 1000;
    return (this.host().telemetry?.points ?? []).filter(point => Date.parse(point.timestamp) >= cutoff);
  });
  readonly hoveredTelemetry = computed(() => {
    const points = this.telemetryPoints();
    const index = this.hoveredTelemetryIndex();
    if (index === null) return null;
    const point = points[index] ?? null;
    if (!point) return null;
    return {
      index,
      point,
      position: points.length > 1 ? index * 100 / (points.length - 1) : 50,
      values: [
        { key: 'cpu', label: 'CPU', value: formatTelemetryNumber(point.cpuPercent, '%') },
        { key: 'memory', label: 'Memory', value: formatTelemetryNumber(
          point.memoryUsedBytes === null ? null : point.memoryUsedBytes / 1_000_000_000,
          ' GB',
        ) },
        { key: 'load', label: 'Load / cores', value: formatTelemetryNumber(point.load1, ' load') },
        { key: 'slots', label: 'Active slots', value: `${point.activeSlots} ${point.activeSlots === 1 ? 'slot' : 'slots'}` },
      ],
    };
  });
  readonly chartRows = computed(() => {
    const points = this.telemetryPoints();
    const hoveredPoint = this.hoveredTelemetry()?.point ?? null;
    return [
      { key: 'cpu', label: 'CPU', value: (p: HostTelemetryPoint) => p.cpuPercent, max: 100 },
      { key: 'memory', label: 'Memory', value: (p: HostTelemetryPoint) => p.memoryUsedBytes !== null && p.memoryTotalBytes ? p.memoryUsedBytes * 100 / p.memoryTotalBytes : null, max: 100 },
      { key: 'load', label: 'Load / cores', value: (p: HostTelemetryPoint) => p.load1, max: Math.max(1, ...points.map(p => p.cpuCores), ...points.map(p => p.load1 ?? 0)) },
      { key: 'slots', label: 'Active slots', value: (p: HostTelemetryPoint) => p.activeSlots, max: Math.max(1, ...points.map(p => p.activeSlots)) },
    ].map(row => ({
      ...row,
      path: sparkline(points, row.value, row.max),
      hoverY: hoveredPoint ? sparklineY(row.value(hoveredPoint), row.max) : null,
    }));
  });
  readonly latestContext = computed(() => {
    const point = this.latestTelemetry();
    if (!point) return '';
    const freshness = this.liveTelemetry() ? '' : ' at last sample · stale';
    return `${point.activeSlots} RUN active${freshness} · host load ${(point.load1 ?? 0).toFixed(1)} of ${point.cpuCores} cores`;
  });
  readonly telemetryFindings = computed(() => {
    const byPhase = new Map<string, HostTelemetryFinding>();
    for (const finding of this.host().telemetry?.findings ?? []) {
      const phase = finding.isActive === false ? 'history' : 'active';
      byPhase.set(`${finding.kind}:${phase}`, finding);
    }
    return [...byPhase.values()].sort((left, right) => {
      const activity = Number(right.isActive !== false) - Number(left.isActive !== false);
      return activity || right.until.localeCompare(left.until);
    });
  });
  readonly visibleTelemetryFindings = computed(() => this.telemetryFindings().slice(0, 3));
  readonly additionalTelemetryFindingCount = computed(() =>
    Math.max(0, this.telemetryFindings().length - this.visibleTelemetryFindings().length));

  readonly meters = computed<Meter[]>(() => {
    const h = this.host();
    const s = h.stats;
    if (!s || this.stale()) return [];
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
      ...(s.diskTotalGb > 0 ? [{
        key: 'disk',
        label: 'Disk',
        detail: `${formatDisk(s.diskTotalGb - s.diskFreeGb)} / ${formatDisk(s.diskTotalGb)}`,
        pct: disk,
        tone: meterTone(disk),
      } as Meter] : []),
    ];
  });

  readonly taskInflowLabel = computed(() => {
    const host = this.host();
    if (this.liveLoading()) return 'loading…';
    if (this.liveError()) return 'unknown';
    if (this.retired()) return 'retired';
    if (host.status === 'draining' || host.gitPushStatus === 'read-only') return 'blocked';
    return this.stale() ? 'unknown' : 'open';
  });
  readonly daemonLabel = computed(() => {
    if (this.liveLoading()) return 'loading live status…';
    if (this.liveError()) return 'status unavailable';
    return this.stale() ? 'stopped' : (this.host().daemonState ?? 'running');
  });
  readonly runSlotsLabel = computed(() => {
    if (this.liveLoading()) return 'Loading daemon telemetry…';
    if (this.liveError()) return 'Live count unavailable';
    const latest = this.latestTelemetry();
    if (!latest) return 'No slot telemetry';
    if (!this.liveTelemetry()) return `${latest.activeSlots} active · stale`;
    return `${latest.activeSlots} active`;
  });
  readonly runSlotsDiverge = computed(() => {
    const telemetry = this.liveTelemetry();
    return telemetry !== null && telemetry.activeSlots !== this.boardActiveSlots();
  });
  readonly runSlotsTooltip = computed(() => {
    const telemetry = this.liveTelemetry();
    if (!telemetry) return 'Active-slot telemetry is stale or unavailable.';
    if (this.runSlotsDiverge()) {
      const boardSource = this.host().role === 'remote' ? 'live remote leases' : 'live local executions';
      return `Sources disagree: telemetry reports ${telemetry.activeSlots} active slots; the board reports ${this.boardActiveSlots()} ${boardSource} for this host.`;
    }
    return `Telemetry and board leases agree on ${telemetry.activeSlots} active ${telemetry.activeSlots === 1 ? 'slot' : 'slots'}.`;
  });
  readonly gateWorkLabel = computed(() => {
    if (this.liveLoading()) return 'Loading gate events…';
    if (this.liveError()) return 'Live count unavailable';
    const active = this.host().activeGateCount ?? 0;
    const capacity = this.host().gateCapacity ?? 0;
    return capacity > 0 ? `${active} running · pool ${capacity}` : `${active} running`;
  });
  readonly failedProjectPreflights = computed(() =>
    (this.host().projectPreflights ?? []).filter(preflight => preflight.status === 'failed'),
  );

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

  selectTelemetryWindow(window: '1h' | '6h' | '48h' | '14d'): void {
    this.hoveredTelemetryIndex.set(null);
    this.telemetryWindow.set(window);
  }

  showTelemetryPoint(event: PointerEvent): void {
    const bounds = (event.currentTarget as HTMLElement).getBoundingClientRect();
    if (bounds.width <= 0) return;
    const ratio = Math.max(0, Math.min(1, (event.clientX - bounds.left) / bounds.width));
    const lastIndex = this.telemetryPoints().length - 1;
    if (lastIndex < 0) return;
    this.hoveredTelemetryIndex.set(Math.round(ratio * lastIndex));
  }

  hideTelemetryPoint(event: PointerEvent): void {
    if (event.pointerType !== 'touch') this.hoveredTelemetryIndex.set(null);
  }

  focusTelemetry(): void {
    const lastIndex = this.telemetryPoints().length - 1;
    if (lastIndex >= 0 && this.hoveredTelemetryIndex() === null) this.hoveredTelemetryIndex.set(lastIndex);
  }

  moveTelemetryHover(event: KeyboardEvent): void {
    const lastIndex = this.telemetryPoints().length - 1;
    if (lastIndex < 0) return;
    const current = this.hoveredTelemetryIndex() ?? lastIndex;
    let next: number;
    if (event.key === 'ArrowLeft') next = Math.max(0, current - 1);
    else if (event.key === 'ArrowRight') next = Math.min(lastIndex, current + 1);
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = lastIndex;
    else if (event.key === 'Escape') {
      this.hoveredTelemetryIndex.set(null);
      return;
    } else {
      return;
    }
    event.preventDefault();
    this.hoveredTelemetryIndex.set(next);
  }

  clearTelemetryHover(): void { this.hoveredTelemetryIndex.set(null); }

  findingTooltip(finding: HostTelemetryFinding): string {
    const range = `${finding.since} to ${finding.until}`;
    return finding.isActive === false
      ? `${finding.occurrences ?? 1} completed phase(s), ${range}`
      : range;
  }
}

function sparkline(points: readonly HostTelemetryPoint[], value: (point: HostTelemetryPoint) => number | null, max: number): string {
  if (points.length < 2) return '';
  return points.map((point, index) => ({ index, value: value(point) }))
    .filter(item => item.value !== null)
    .map(item => `${(item.index * 100 / (points.length - 1)).toFixed(1)},${sparklineY(item.value, max)}`)
    .join(' ');
}

function sparklineY(value: number | null, max: number): string | null {
  if (value === null) return null;
  return (28 - Math.max(0, Math.min(1, value / max)) * 26).toFixed(1);
}

function formatTelemetryNumber(value: number | null, unit: string): string {
  if (value === null || !Number.isFinite(value)) return '-';
  return `${Number(value.toFixed(2))}${unit}`;
}
