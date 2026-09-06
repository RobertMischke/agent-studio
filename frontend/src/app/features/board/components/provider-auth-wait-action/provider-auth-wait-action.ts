import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { TooltipDirective } from 'coding-agent-chat/shared';
import {
  CodexSignInDialogComponent,
  ProviderAuthStatusService,
  RemoteHostsService,
  providerAuthWaitReason,
} from '../../../remote-hosts';

@Component({
  selector: 'app-provider-auth-wait-action',
  standalone: true,
  imports: [TooltipDirective, CodexSignInDialogComponent],
  templateUrl: './provider-auth-wait-action.html',
  styleUrl: './provider-auth-wait-action.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProviderAuthWaitActionComponent {
  readonly task = input.required<TaskInfo>();
  private readonly authStatus = inject(ProviderAuthStatusService);
  private readonly remoteHosts = inject(RemoteHostsService);
  readonly dialogOpen = signal(false);
  readonly wait = computed(() => this.authStatus.loaded()
    ? providerAuthWaitReason(this.task(), this.authStatus.statuses())
    : null);
  readonly target = computed(() => {
    const auth = this.wait()?.signInTarget;
    if (!auth) return null;
    const host = this.remoteHosts.configuredHostForAliases(auth.aliases);
    return host ? { host, auth } : null;
  });

  open(event: MouseEvent): void {
    event.stopPropagation();
    if (this.target()) this.dialogOpen.set(true);
  }

  close(): void { this.dialogOpen.set(false); }
  complete(): void {
    this.dialogOpen.set(false);
    this.authStatus.refresh();
  }
}
