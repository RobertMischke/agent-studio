import { ChangeDetectionStrategy, Component, OnDestroy, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription, filter, switchMap, take, tap, timer } from 'rxjs';
import { DialogComponent } from '../../../../components/dialog/dialog.component';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';
import { RemoteHostsService } from '../../services/remote-hosts.service';

type SignInPhase = 'idle' | 'starting' | 'pending' | 'awaiting-probe' | 'failed';

@Component({
  selector: 'app-codex-sign-in-dialog',
  standalone: true,
  imports: [FormsModule, DialogComponent],
  templateUrl: './codex-sign-in-dialog.html',
  styleUrl: './codex-sign-in-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CodexSignInDialogComponent implements OnDestroy {
  private readonly auth = inject(ProviderAuthStatusService);
  private readonly remoteHosts = inject(RemoteHostsService);
  readonly dialog = inject(CodexSignInDialogService);
  readonly phase = signal<SignInPhase>('idle');
  readonly sshTarget = signal('');
  readonly verificationUrl = signal<string | null>(null);
  readonly userCode = signal<string | null>(null);
  readonly detail = signal('Start a host-owned Codex device sign-in. No token leaves the execution host.');
  readonly copied = signal(false);
  private request: Subscription | null = null;
  private activeTargetId: string | null = null;

  readonly target = this.dialog.active;

  constructor() {
    effect(() => {
      const target = this.dialog.active();
      if (target?.hostId === this.activeTargetId) return;
      this.activeTargetId = target?.hostId ?? null;
      untracked(() => {
        this.sshTarget.set(target?.sshTarget || target?.hostName || '');
        this.resetState();
      });
    });
  }

  ngOnDestroy(): void {
    this.request?.unsubscribe();
  }

  start(): void {
    const target = this.target();
    const sshTarget = this.sshTarget().trim();
    if (!target || !sshTarget || this.phase() === 'starting' || this.phase() === 'pending') return;
    const baseline = this.auth.statuses().find(status =>
      status.provider === 'codex' && status.aliases.some(alias => target.aliases.includes(alias)))?.advertisedAt ?? null;
    this.request?.unsubscribe();
    this.phase.set('starting');
    this.detail.set('Starting codex login --device-auth through the protected SSH channel…');
    this.request = this.auth.startCodexSignIn(target.hostId, { sshTarget }).subscribe({
      next: started => {
        this.verificationUrl.set(started.verificationUrl);
        this.userCode.set(started.userCode);
        this.phase.set('pending');
        this.detail.set('Open the verification page, enter the code, and finish sign-in in your browser.');
        this.request = timer(0, 1_500).pipe(
          switchMap(() => this.auth.pollCodexSignIn(target.hostId, started.handle)),
          tap(status => this.detail.set(status.detail)),
          filter(status => status.state !== 'pending'),
          take(1),
          switchMap(status => {
            if (status.state === 'failed') throw new Error(status.detail);
            this.phase.set('awaiting-probe');
            this.detail.set('Sign-in succeeded. Waiting for the runner to advertise a fresh Codex authentication probe…');
            return this.auth.waitForReadyProbe('codex', target.aliases, baseline);
          }),
        ).subscribe({
          next: () => {
            this.auth.refresh();
            this.remoteHosts.reload();
            this.close();
          },
          error: error => {
            this.phase.set('failed');
            this.detail.set(error?.message ?? 'Codex sign-in could not be confirmed.');
          },
        });
      },
      error: error => {
        this.phase.set('failed');
        this.detail.set(error?.error?.message ?? 'Codex sign-in could not be started.');
      },
    });
  }

  async copyCode(): Promise<void> {
    this.copied.set(await copyTextToClipboard(this.userCode() ?? ''));
  }

  close(): void {
    this.request?.unsubscribe();
    this.request = null;
    this.dialog.close();
  }

  private resetState(): void {
    this.request?.unsubscribe();
    this.request = null;
    this.phase.set('idle');
    this.verificationUrl.set(null);
    this.userCode.set(null);
    this.copied.set(false);
    this.detail.set('Start a host-owned Codex device sign-in. No token leaves the execution host.');
  }
}
