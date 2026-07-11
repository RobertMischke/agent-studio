import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { RemoteHostCardComponent } from '../remote-host-card/remote-host-card';
import type { HostActionKind, RemoteHost } from '../../models/remote-host.model';
import { AddHostWizardComponent, type ProvisionedHostDraft } from '../add-host-wizard/add-host-wizard';
import { type VisibleCliTaskCreated, type VisibleCliTaskWorkspace } from '../../../visible-cli-task';
import { RunnerSetupDialogComponent } from '../runner-setup-dialog/runner-setup-dialog';

/**
 * Remote Hosts settings page (AGT-1921).
 *
 * The single visible entry point into remote-host management: every execution
 * location - the operator's local machine and each remote runner - in one list
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

  readonly hosts = this.service.hosts;
  readonly loading = this.service.loading;
  readonly error = this.service.error;
  readonly wizardOpen = signal(false);
  readonly setupHost = signal<RemoteHost | null>(null);
  readonly workspaces = input<readonly VisibleCliTaskWorkspace[]>([]);
  readonly openTask = output<VisibleCliTaskCreated>();

  /** Ticking clock so relative heartbeat labels stay fresh without per-card timers. */
  readonly now = signal<number>(Date.now());
  private tickHandle: ReturnType<typeof setInterval> | null = null;

  /** Header tallies - each reconciles to the visible cards (R3 sum invariant). */
  readonly total = computed(() => this.hosts().length);
  readonly onlineCount = computed(() => this.hosts().filter((h) => h.status === 'online').length);
  readonly remoteCount = computed(() => this.hosts().filter((h) => h.role === 'remote').length);

  ngOnInit(): void {
    this.service.ensureLoaded();
    this.tickHandle = setInterval(() => this.now.set(Date.now()), 30_000);
  }

  ngOnDestroy(): void {
    if (this.tickHandle) clearInterval(this.tickHandle);
  }

  reload(): void { this.service.reload(); }

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

  onAction(evt: { kind: HostActionKind; id: string }): void {
    switch (evt.kind) {
      case 'reprobe': this.service.reprobe(evt.id); break;
      case 'drain': this.service.drain(evt.id); break;
      case 'retire': this.service.retire(evt.id); break;
    }
  }
}
