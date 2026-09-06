import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { Subscription, filter, switchMap, take, timer } from 'rxjs';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import type { RemoteHost } from '../../models/remote-host.model';
import type { CodexSignInStatusResponse } from '../../models/provider-auth.model';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

type CodexSignInPhase = 'starting' | 'pending' | 'verifying' | 'failed';

@Component({
  selector: 'app-codex-sign-in-dialog',
  standalone: true,
  templateUrl: './codex-sign-in-dialog.html',
  styleUrl: './codex-sign-in-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CodexSignInDialogComponent implements OnInit, OnDestroy {
  readonly host = input.required<RemoteHost>();
  readonly cancelled = output<void>();
  readonly signedIn = output<void>();

  private readonly auth = inject(ProviderAuthStatusService);
  private polling: Subscription | null = null;
  private verification: Subscription | null = null;
  private baselineAdvertisedAt: string | null = null;

  readonly phase = signal<CodexSignInPhase>('starting');
  readonly verificationUrl = signal<string | null>(null);
  readonly userCode = signal<string | null>(null);
  readonly detail = signal('Starting a host-owned Codex device sign-in session…');
  readonly copied = signal(false);
  readonly expiresAt = signal<string | null>(null);
  readonly expiresLabel = computed(() => {
    const value = this.expiresAt();
    return value ? `This session expires at ${new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.` : '';
  });

  ngOnInit(): void {
    const host = this.host();
    const hostId = host.id.toLowerCase();
    this.baselineAdvertisedAt = this.auth.statuses().find(item =>
      item.provider === 'codex'
      && item.aliases.some(alias => alias.toLowerCase() === hostId))?.advertisedAt ?? null;
    if (!host.address) {
      this.fail('This host has no configured SSH target. Add it in Execution Hosts before signing in.');
      return;
    }
    this.auth.startCodexSignIn(host.capacityHostId ?? host.id, { sshTarget: normalizeSshTarget(host.address) })
      .subscribe({
        next: prompt => {
          this.verificationUrl.set(prompt.verificationUrl);
          this.userCode.set(prompt.userCode);
          this.expiresAt.set(prompt.expiresAt);
          this.detail.set('Open the verification page, enter the one-time code, and approve this execution host.');
          this.phase.set('pending');
          this.poll(prompt.handle);
        },
        error: error => this.fail(error?.error?.message ?? 'Could not start Codex sign-in on this host.'),
      });
  }

  ngOnDestroy(): void {
    this.polling?.unsubscribe();
    this.verification?.unsubscribe();
  }

  copyCode(): void {
    const code = this.userCode();
    if (!code) return;
    void copyTextToClipboard(code).then(ok => this.copied.set(ok));
  }

  closeFromBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.cancelled.emit();
  }

  private poll(handle: string): void {
    const host = this.host();
    this.polling = timer(0, 1_000).pipe(
      switchMap(() => this.auth.codexSignInStatus(host.capacityHostId ?? host.id, handle)),
      filter(status => status.state !== 'pending'),
      take(1),
    ).subscribe({
      next: status => this.onTerminal(status),
      error: error => this.fail(error?.error?.message ?? 'Codex sign-in status could not be read.'),
    });
  }

  private onTerminal(status: CodexSignInStatusResponse): void {
    if (status.state === 'failed') {
      this.fail(status.detail);
      return;
    }
    this.phase.set('verifying');
    this.detail.set(status.probeRefreshTriggered
      ? 'Sign-in succeeded. Waiting for the restarted runner to publish a fresh Codex probe…'
      : 'Sign-in succeeded. Waiting for the runner’s next Codex probe…');
    const host = this.host();
    this.verification = this.auth.waitForFreshProbe(
      'codex',
      [host.id, host.clientId, host.capacityHostId ?? '', host.name],
      this.baselineAdvertisedAt,
    ).subscribe({
      next: badge => {
        if (badge.state === 'ok' || badge.state === 'expiring') this.signedIn.emit();
        else this.fail(badge.detail);
      },
      error: () => this.fail('Codex signed in, but the runner did not publish a fresh provider probe in time.'),
    });
  }

  private fail(detail: string): void {
    this.phase.set('failed');
    this.detail.set(detail);
  }
}

function normalizeSshTarget(value: string): string {
  return value.trim().replace(/^ssh:\/\//i, '');
}
