import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import type { ProviderAuthDisplayState } from '../../models/provider-auth.model';
import { providerAuthWaitReason } from '../../models/provider-auth.model';
import type { RemoteHost } from '../../models/remote-host.model';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';
import { RemoteHostsService } from '../../services/remote-hosts.service';
import { CodexSignInDialogComponent } from '../codex-sign-in-dialog/codex-sign-in-dialog';

@Component({
  selector: 'app-codex-sign-in-action',
  standalone: true,
  imports: [CodexSignInDialogComponent],
  templateUrl: './codex-sign-in-action.html',
  styleUrl: './codex-sign-in-action.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CodexSignInActionComponent {
  readonly host = input<RemoteHost | null>(null);
  readonly task = input<TaskInfo | null>(null);
  readonly provider = input<string | null>(null);
  readonly state = input<ProviderAuthDisplayState | null>(null);
  readonly mode = input<'badge' | 'wait'>('badge');
  readonly open = signal(false);

  private readonly auth = inject(ProviderAuthStatusService);
  private readonly hosts = inject(RemoteHostsService);
  readonly wait = computed(() => {
    const task = this.task();
    return task && this.auth.loaded() ? providerAuthWaitReason(task, this.auth.statuses()) : null;
  });
  readonly target = computed(() => {
    if (this.host()) return this.host();
    const wait = this.wait();
    if (wait?.provider !== 'codex') return null;
    const aliases = new Set(wait.hostNames.map(value => value.toLowerCase()));
    return this.hosts.hosts().find(host => !!host.address && [
      host.id, host.clientId, host.capacityHostId ?? '', host.name,
    ].some(alias => aliases.has(alias.toLowerCase()))) ?? null;
  });
  readonly actionableWait = computed(() => this.wait()?.provider === 'codex' && !!this.target()?.address);
  readonly visible = computed(() => this.mode() === 'badge'
    ? this.provider() === 'codex'
      && (this.state() === 'unavailable' || this.state() === 'expiring')
      && !!this.target()?.address
    : !!this.wait());

  constructor() {
    effect(() => {
      if (this.mode() === 'wait'
        && this.wait()?.provider === 'codex'
        && this.hosts.hosts().length === 0
        && !this.hosts.loading()) this.hosts.refresh();
    });
  }

  show(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.open.set(true);
  }

  complete(): void {
    this.open.set(false);
    this.auth.refresh();
    this.hosts.refresh();
  }
}
