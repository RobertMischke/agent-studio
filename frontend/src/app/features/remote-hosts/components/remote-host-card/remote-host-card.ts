import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { cliTypeIcon, cliTypeLabel } from '../../../../services/format.util';
import type { CliType } from '../../../../models/task.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { CapabilityHealthComponent } from '../capability-health/capability-health';
import { GitTokenCapabilityComponent } from '../git-token-capability/git-token-capability';
import { HostWorkloadSummaryComponent } from '../host-workload-summary/host-workload-summary';
import { HostTelemetryHistoryComponent } from '../host-telemetry-history/host-telemetry-history';
import {
  RuntimeCapacityEditorComponent,
  type HostProjectPolicyChange,
  type RuntimeCapacityChange,
} from '../runtime-capacity-editor/runtime-capacity-editor';
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
  taskServerRouteStatus,
  type HostActionKind,
  type HostProjectSlots,
  type MeterTone,
  type RemoteHost,
} from '../../models/remote-host.model';
import { freshHostTelemetry, latestHostTelemetry } from '../../models/running-truth';
import { providerAuthBadgesForHost, type ProviderAuthBadge } from '../../models/provider-auth.model';

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
    HostTelemetryHistoryComponent,
    RuntimeCapacityEditorComponent,
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
  /**
   * Which projects hold this host's slots. Supplied by the panel from the same
   * board lease truth as {@link boardActiveSlots}, so the per-project rows and
   * the active-slot total always reconcile.
   */
  readonly projectSlots = input<readonly HostProjectSlots[]>([]);
  /** Injected clock so the relative heartbeat label ticks without a per-card timer. */
  readonly now = input<number>(Date.now());
  readonly action = output<{ kind: HostActionKind; id: string }>();
  readonly capacityChange = output<RuntimeCapacityChange>();
  readonly projectPolicyChange = output<HostProjectPolicyChange>();
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
  readonly latestTelemetry = computed(() => latestHostTelemetry(this.host()));
  readonly liveTelemetry = computed(() => freshHostTelemetry(this.host(), this.now()));
  readonly telemetryStale = computed(() =>
    this.latestTelemetry() !== null && this.liveTelemetry() === null);
  readonly taskServerRouteLabel = computed(() => {
    switch (taskServerRouteStatus(this.host())) {
      case 'reachable': return 'reachable';
      case 'degraded': return 'degraded';
      case 'unreachable': return 'unreachable';
      case 'unknown': return 'not reported';
    }
  });

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
    if (host.status === 'draining') return 'blocked';
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
  readonly providerAuthBadges = computed(() => providerAuthBadgesForHost(this.host(), this.now()));

  latestAuthTransition(badge: ProviderAuthBadge) {
    return badge.history.at(-1) ?? null;
  }

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

}
