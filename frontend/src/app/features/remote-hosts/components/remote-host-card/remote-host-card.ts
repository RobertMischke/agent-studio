import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
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

  readonly tone = computed(() => hostStatusTone(this.host().status));
  readonly statusLabel = computed(() => hostStatusLabel(this.host().status));
  readonly roleLabel = computed(() => hostRoleLabel(this.host().role));
  readonly heartbeatLabel = computed(() => relativeHeartbeat(this.host().lastHeartbeatAt, this.now()));
  readonly retired = computed(() => this.host().status === 'retired');

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
}
