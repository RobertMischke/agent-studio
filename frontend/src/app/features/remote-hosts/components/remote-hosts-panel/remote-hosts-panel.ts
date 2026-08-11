import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { RemoteHostCardComponent } from '../remote-host-card/remote-host-card';
import type {
  HostActionKind,
  HostHeartbeatStatus,
  HostProjectSlots,
  RemoteHost,
} from '../../models/remote-host.model';
import type {
  HostProjectPolicyChange,
  RuntimeCapacityChange,
} from '../runtime-capacity-editor/runtime-capacity-editor';
import {
  boardProjectSlotsForHost,
  boardRemoteSlotsForHost,
  deriveBoardRunningTruth,
} from '../../models/running-truth';
import { AddHostWizardComponent, type ProvisionedHostDraft } from '../add-host-wizard/add-host-wizard';
import { type VisibleCliTaskCreated, type VisibleCliTaskWorkspace } from '../../../visible-cli-task';
import { RunnerSetupDialogComponent } from '../runner-setup-dialog/runner-setup-dialog';

type HostSortKey = 'name' | 'status' | 'slots' | 'load' | 'activity' | 'release';
type SortDirection = 'asc' | 'desc';

interface HostTableState {
  expandedHostIds: readonly string[];
  sortKey: HostSortKey;
  direction: SortDirection;
}

const HOST_TABLE_STORAGE_KEY = 'atp.executionHosts.table.v1';
const DEFAULT_TABLE_STATE: HostTableState = {
  expandedHostIds: [],
  sortKey: 'name',
  direction: 'asc',
};

/**
 * Execution Hosts settings page (AGT-1921).
 *
 * The single visible entry point into execution-host management: the local
 * machine and each remote runner in one list
 * so the whole fleet reads as one picture. Each row carries heartbeat status,
 * capabilities, live system vitals (RAM / CPU / Disk), per-CLI quota, and the
 * Re-Probe / Drain / Retire actions ({@link RemoteHostCardComponent}).
 *
 * Host definitions come from {@link RemoteHostsService}; real Task Server
 * client LastSeen values hydrate liveness on every reload.
 */
@Component({
  selector: 'app-remote-hosts-panel',
  standalone: true,
  imports: [RemoteHostCardComponent, AddHostWizardComponent, RunnerSetupDialogComponent],
  templateUrl: './remote-hosts-panel.html',
  styleUrl: './remote-hosts-panel.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RemoteHostsPanelComponent implements OnInit, OnDestroy {
  private readonly service = inject(RemoteHostsService);
  private readonly tasks = inject(TaskService);
  private readonly initialTableState = readTableState();
  private readonly nameCollator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

  readonly hosts = this.service.hosts;
  readonly loading = this.service.loading;
  readonly error = this.service.error;
  readonly identityDiagnostics = this.service.identityDiagnostics;
  readonly wizardOpen = signal(false);
  readonly setupHost = signal<RemoteHost | null>(null);
  readonly pendingConfirmation = signal<{ kind: 'retire' | 'delete'; host: RemoteHost } | null>(null);
  readonly expandedHostIds = signal<ReadonlySet<string>>(
    new Set(this.initialTableState.expandedHostIds),
  );
  readonly sortKey = signal<HostSortKey>(this.initialTableState.sortKey);
  readonly sortDirection = signal<SortDirection>(this.initialTableState.direction);
  readonly confirmationTitle = computed(() => {
    const pending = this.pendingConfirmation();
    if (!pending) return '';
    return pending.kind === 'retire' ? `Retire ${pending.host.name}?` : `Delete ${pending.host.name} permanently?`;
  });
  readonly confirmationText = computed(() => {
    const pending = this.pendingConfirmation();
    if (!pending) return '';
    if (pending.kind === 'delete') return 'This removes the already-retired identity file and cannot be undone. Historical task attribution may retain the old client id as plain text.';
    const active = pending.host.activeTaskCount ?? 0;
    const work = active > 0 ? `The ${active} running task(s) will finish first; then the client retires.` : 'With no running work, the client retires immediately.';
    return `No new leases will be granted. ${work} It remains visible and can be revived.`;
  });
  readonly workspaces = input<readonly VisibleCliTaskWorkspace[]>([]);
  readonly openTask = output<VisibleCliTaskCreated>();

  /** Ticking clock so relative heartbeat labels stay fresh without per-row timers. */
  readonly now = signal<number>(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;

  readonly total = computed(() => this.hosts().length);
  readonly sortedHosts = computed(() => {
    const key = this.sortKey();
    const direction = this.sortDirection() === 'asc' ? 1 : -1;
    return [...this.hosts()].sort((a, b) => {
      const compared = this.compareHosts(a, b, key);
      return compared === 0 ? this.nameCollator.compare(a.id, b.id) : compared * direction;
    });
  });
  readonly boardRunningTruth = computed(() =>
    deriveBoardRunningTruth(this.tasks.grouped().progress));

  ngOnInit(): void {
    this.service.ensureLoaded();
    this.tickHandle = setInterval(() => this.now.set(Date.now()), 30_000);
  }

  ngOnDestroy(): void {
    if (this.tickHandle) clearInterval(this.tickHandle);
  }

  reload(): void { this.service.reload(); }

  isExpanded(hostId: string): boolean {
    return this.expandedHostIds().has(hostId);
  }

  toggleHost(hostId: string): void {
    const next = new Set(this.expandedHostIds());
    if (next.has(hostId)) next.delete(hostId);
    else next.add(hostId);
    this.expandedHostIds.set(next);
    this.persistTableState();
  }

  sortBy(key: HostSortKey): void {
    if (this.sortKey() === key) {
      this.sortDirection.update(direction => direction === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortKey.set(key);
      this.sortDirection.set(key === 'activity' || key === 'load' ? 'desc' : 'asc');
    }
    this.persistTableState();
  }

  sortAria(key: HostSortKey): 'ascending' | 'descending' | 'none' {
    if (this.sortKey() !== key) return 'none';
    return this.sortDirection() === 'asc' ? 'ascending' : 'descending';
  }

  sortIndicator(key: HostSortKey): string {
    if (this.sortKey() !== key) return '';
    return this.sortDirection() === 'asc' ? '↑' : '↓';
  }

  boardSlots(host: RemoteHost): number {
    const truth = this.boardRunningTruth();
    return host.role === 'local' ? truth.local : boardRemoteSlotsForHost(truth, host);
  }

  /** Per-project consumption of one host's shared slot ceiling. */
  projectSlots(host: RemoteHost): readonly HostProjectSlots[] {
    return host.role === 'local'
      ? []
      : boardProjectSlotsForHost(this.boardRunningTruth(), host);
  }

  openWizard(): void { this.wizardOpen.set(true); }
  closeWizard(): void { this.wizardOpen.set(false); }
  openSetup(host: RemoteHost): void { this.setupHost.set(host); }
  closeSetup(): void { this.setupHost.set(null); }

  onSetupTaskCreated(task: VisibleCliTaskCreated): void {
    this.setupHost.set(null);
    this.openTask.emit(task);
  }

  completeWizard(host: ProvisionedHostDraft): void {
    this.service.addProvisionedHost(host.name, host.address);
    this.wizardOpen.set(false);
  }

  onCapacityChange(change: RuntimeCapacityChange): void {
    this.service.setCapacity(
      change.id,
      change.maxParallelism,
      change.targetLoadPercent,
      change.rampStrategy,
    );
  }

  onProjectPolicyChange(change: HostProjectPolicyChange): void {
    this.service.setProjectPolicy(
      change.id,
      change.allowAllProjects,
      change.allowedProjectIds,
      change.expectedVersion,
    );
  }

  onAction(evt: { kind: HostActionKind; id: string }): void {
    const host = this.hosts().find(item => item.id === evt.id);
    if (!host) return;
    if (evt.kind === 'retire' || evt.kind === 'delete') {
      this.pendingConfirmation.set({ kind: evt.kind, host });
      return;
    }
    switch (evt.kind) {
      case 'reprobe': this.service.reprobe(evt.id); break;
      case 'drain': this.service.drain(evt.id); break;
      case 'revive': this.service.revive(evt.id); break;
    }
  }

  cancelConfirmation(): void { this.pendingConfirmation.set(null); }

  confirmLifecycleAction(): void {
    const pending = this.pendingConfirmation();
    if (!pending) return;
    this.pendingConfirmation.set(null);
    if (pending.kind === 'retire') this.service.retire(pending.host.id);
    else this.service.permanentlyDelete(pending.host.id);
  }

  private compareHosts(a: RemoteHost, b: RemoteHost, key: HostSortKey): number {
    switch (key) {
      case 'name': return this.nameCollator.compare(a.name, b.name);
      case 'status': return statusRank(a.status) - statusRank(b.status);
      case 'slots': return slotOccupancy(a) - slotOccupancy(b);
      case 'load': return (a.stats?.cpuLoadPct ?? -1) - (b.stats?.cpuLoadPct ?? -1);
      case 'activity': return activityAt(a) - activityAt(b);
      case 'release': return this.nameCollator.compare(a.runnerVersion ?? '', b.runnerVersion ?? '');
    }
  }

  private persistTableState(): void {
    writeTableState({
      expandedHostIds: [...this.expandedHostIds()],
      sortKey: this.sortKey(),
      direction: this.sortDirection(),
    });
  }
}

function slotOccupancy(host: RemoteHost): number {
  return host.telemetry?.points.at(-1)?.activeSlots ?? host.activeTaskCount ?? 0;
}

function activityAt(host: RemoteHost): number {
  return Math.max(parseTime(host.lastHeartbeatAt), parseTime(host.lastClaimAt));
}

function parseTime(value: string | null | undefined): number {
  const parsed = value ? Date.parse(value) : Number.NaN;
  return Number.isFinite(parsed) ? parsed : 0;
}

function statusRank(status: HostHeartbeatStatus): number {
  return ({
    online: 0,
    idle: 1,
    draining: 2,
    degraded: 3,
    offline: 4,
    retired: 5,
  } satisfies Record<HostHeartbeatStatus, number>)[status];
}

function readTableState(): HostTableState {
  if (typeof localStorage === 'undefined') return DEFAULT_TABLE_STATE;
  try {
    const parsed = JSON.parse(localStorage.getItem(HOST_TABLE_STORAGE_KEY) ?? '{}') as Partial<HostTableState>;
    const sortKeys: readonly HostSortKey[] = ['name', 'status', 'slots', 'load', 'activity', 'release'];
    return {
      expandedHostIds: Array.isArray(parsed.expandedHostIds)
        ? parsed.expandedHostIds.filter((id): id is string => typeof id === 'string')
        : [],
      sortKey: sortKeys.includes(parsed.sortKey as HostSortKey)
        ? parsed.sortKey as HostSortKey
        : DEFAULT_TABLE_STATE.sortKey,
      direction: parsed.direction === 'desc' ? 'desc' : 'asc',
    };
  } catch {
    return DEFAULT_TABLE_STATE;
  }
}

function writeTableState(state: HostTableState): void {
  if (typeof localStorage === 'undefined') return;
  try {
    localStorage.setItem(HOST_TABLE_STORAGE_KEY, JSON.stringify(state));
  } catch {
    // Persistence is best-effort; the table remains fully usable without it.
  }
}
