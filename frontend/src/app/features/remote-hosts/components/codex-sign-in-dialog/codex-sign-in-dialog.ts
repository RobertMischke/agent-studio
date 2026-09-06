import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { Subscription, switchMap, takeWhile, timer } from 'rxjs';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';

type DialogPhase = 'starting' | 'pending' | 'verifying' | 'failed';

@Component({
  selector: 'app-codex-sign-in-dialog',
  standalone: true,
  templateUrl: './codex-sign-in-dialog.html',
  styleUrl: './codex-sign-in-dialog.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CodexSignInDialogComponent implements OnInit, OnDestroy {
  readonly hostId = input.required<string>();
  readonly hostName = input.required<string>();
  readonly sshTarget = input.required<string>();
  readonly baselineAdvertisedAt = input<string | null>(null);
  readonly cancelled = output<void>();
  readonly signedIn = output<void>();

  private readonly auth = inject(ProviderAuthStatusService);
  private subscription: Subscription | null = null;
  readonly phase = signal<DialogPhase>('starting');
  readonly verificationUrl = signal<string | null>(null);
  readonly userCode = signal<string | null>(null);
  readonly detail = signal('Starting a host-owned Codex device-auth session through SSH…');
  readonly copied = signal(false);
  readonly statusLabel = computed(() => {
    switch (this.phase()) {
      case 'starting': return 'Starting sign-in';
      case 'pending': return 'Waiting for browser approval';
      case 'verifying': return 'Refreshing provider state';
      case 'failed': return 'Sign-in needs attention';
    }
  });

  ngOnInit(): void { this.start(); }
  ngOnDestroy(): void { this.subscription?.unsubscribe(); }

  start(): void {
    this.subscription?.unsubscribe();
    this.phase.set('starting');
    this.detail.set('Starting a host-owned Codex device-auth session through SSH…');
    this.verificationUrl.set(null);
    this.userCode.set(null);
    this.subscription = this.auth.startCodexSignIn(this.hostId(), this.sshTarget()).subscribe({
      next: challenge => {
        this.verificationUrl.set(challenge.verificationUrl);
        this.userCode.set(challenge.userCode);
        this.phase.set('pending');
        this.detail.set('Open the verification page, enter the code, and approve this host. This dialog will detect completion automatically.');
        this.poll(challenge.handle);
      },
      error: error => this.fail(error?.error?.message ?? 'Codex sign-in could not be started on this execution host.'),
    });
  }

  async copyCode(): Promise<void> {
    this.copied.set(await copyTextToClipboard(this.userCode() ?? ''));
  }

  closeFromBackdrop(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.cancelled.emit();
  }

  private poll(handle: string): void {
    this.subscription?.unsubscribe();
    this.subscription = timer(0, 2_000).pipe(
      switchMap(() => this.auth.codexSignInStatus(this.hostId(), handle)),
      takeWhile(status => status.state === 'pending', true),
    ).subscribe({
      next: status => {
        this.detail.set(status.detail);
        if (status.state === 'completed') this.waitForProbe();
        else if (status.state === 'failed') this.phase.set('failed');
      },
      error: error => this.fail(error?.error?.message ?? 'The Codex sign-in session could not be checked.'),
    });
  }

  private waitForProbe(): void {
    this.phase.set('verifying');
    this.detail.set('Codex confirmed the host session. Waiting for a fresh runner provider probe…');
    this.subscription?.unsubscribe();
    this.subscription = this.auth.waitForFreshProbe(
      'codex',
      [this.hostId(), this.hostName()],
      this.baselineAdvertisedAt(),
    ).subscribe({
      next: status => {
        if (status.state === 'ok' || status.state === 'expiring' || status.state === 'retrying') {
          this.signedIn.emit();
          return;
        }
        this.fail(status.detail || 'The fresh runner probe still reports Codex unavailable.');
      },
      error: () => this.fail('Codex signed in, but a fresh runner provider probe did not arrive within 75 seconds.'),
    });
  }

  private fail(detail: string): void {
    this.phase.set('failed');
    this.detail.set(detail);
  }
}
