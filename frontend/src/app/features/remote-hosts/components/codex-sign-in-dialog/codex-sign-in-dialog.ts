import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

type SignInPhase = 'idle' | 'starting' | 'pending' | 'verifying' | 'failed';

@Component({
  selector: 'app-codex-sign-in-dialog',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './codex-sign-in-dialog.html',
  styleUrls: ['../runner-setup-dialog/runner-setup-dialog.scss', './codex-sign-in-dialog.scss'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CodexSignInDialogComponent implements OnInit, OnDestroy {
  readonly runnerId = input.required<string>();
  readonly hostName = input.required<string>();
  readonly initialSshTarget = input('');
  readonly aliases = input<readonly string[]>([]);
  readonly cancelled = output<void>();
  readonly completed = output<void>();

  readonly sshTarget = signal('');
  readonly phase = signal<SignInPhase>('idle');
  readonly detail = signal('Start a host-owned Codex device sign-in.');
  readonly verificationUrl = signal<string | null>(null);
  readonly userCode = signal<string | null>(null);
  readonly copied = signal(false);
  readonly canStart = computed(() =>
    this.phase() !== 'starting'
    && this.phase() !== 'pending'
    && this.phase() !== 'verifying'
    && this.sshTarget().trim().length > 0);

  private readonly providerAuth = inject(ProviderAuthStatusService);
  private statusSubscription: Subscription | null = null;
  private probeSubscription: Subscription | null = null;

  ngOnInit(): void {
    this.sshTarget.set(this.initialSshTarget().trim() || this.hostName());
  }

  ngOnDestroy(): void {
    this.statusSubscription?.unsubscribe();
    this.probeSubscription?.unsubscribe();
  }

  start(): void {
    if (!this.canStart()) return;
    const baseline = this.providerAuth.statuses().find(status =>
      status.provider === 'codex'
      && status.aliases.some(alias => this.aliases().includes(alias)))?.advertisedAt ?? null;
    this.phase.set('starting');
    this.detail.set('Starting `codex login --device-auth` as the runner user.');
    this.verificationUrl.set(null);
    this.userCode.set(null);
    this.providerAuth.startCodexSignIn(this.runnerId(), this.sshTarget().trim()).subscribe({
      next: session => {
        this.phase.set('pending');
        this.detail.set(session.detail);
        this.verificationUrl.set(session.verificationUrl);
        this.userCode.set(session.userCode);
        this.statusSubscription = this.providerAuth.watchCodexSignIn(
          this.runnerId(), session.handle,
        ).subscribe({
          next: update => {
            this.detail.set(update.detail);
            if (update.state === 'failed') {
              this.phase.set('failed');
              return;
            }
            if (update.state === 'completed') this.waitForProbe(baseline);
          },
          error: error => this.fail(error?.error?.message ?? 'Studio could not read the Codex sign-in status.'),
        });
      },
      error: error => this.fail(error?.error?.message ?? 'Studio could not start Codex sign-in on the host.'),
    });
  }

  async copyCode(): Promise<void> {
    this.copied.set(await copyTextToClipboard(this.userCode() ?? ''));
  }

  closeFromBackdrop(event: MouseEvent): void {
    event.stopPropagation();
    if (event.target === event.currentTarget) this.cancelled.emit();
  }

  private waitForProbe(baseline: string | null): void {
    if (this.phase() === 'verifying') return;
    this.phase.set('verifying');
    this.detail.set('Codex reports signed in. Waiting for the refreshed runner capability.');
    this.probeSubscription = this.providerAuth.waitForFreshProbe(
      'codex',
      [this.runnerId(), this.hostName(), ...this.aliases()],
      baseline,
    ).subscribe({
      next: status => {
        if (status.state === 'ok' || status.state === 'expiring' || status.state === 'retrying') {
          this.completed.emit();
          return;
        }
        this.fail(status.detail);
      },
      error: () => this.fail('Codex signed in, but no refreshed provider capability arrived within 75 seconds. Re-probe the host before retrying.'),
    });
  }

  private fail(detail: string): void {
    this.phase.set('failed');
    this.detail.set(detail);
  }
}
