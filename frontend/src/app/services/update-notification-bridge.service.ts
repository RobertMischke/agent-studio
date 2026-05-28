import { effect, inject, Injectable } from '@angular/core';
import { UpdateClientService } from './update.service';
import { NotificationService } from './notification.service';

/**
 * F56: bridges UpdateClientService status changes to toast notifications.
 * Replaces the old <app-update-banner> component. Watches status() and
 * pushes toasts for done / done-no-change / failed states.
 *
 * Instantiated once at app bootstrap (provided in root, injected in AppComponent).
 */
@Injectable({ providedIn: 'root' })
export class UpdateNotificationBridge {
  private readonly client = inject(UpdateClientService);
  private readonly notify = inject(NotificationService);

  private activeToastId: number | null = null;
  private lastHandledRunId: string | null = null;
  private lastHandledPhase: string | null = null;
  private dismissed = new Set<string>();

  constructor() {
    effect(() => {
      const s = this.client.status();
      if (!s) return;
      if (s.isRunning) return;

      const runId = s.currentRunId ?? 'null-run';

      if (this.dismissed.has(runId)) return;
      // Allow upgrading a previously shown failed toast to the done toast
      // for the same runId (defense-in-depth for the cold-start retry
      // window: if the backend eventually answers, the operator should see
      // the success state, not stay parked on "Update failed").
      const sameRunIdAlreadyHandled = this.lastHandledRunId === runId;
      const isFailedToDoneUpgrade =
        sameRunIdAlreadyHandled && this.lastHandledPhase === 'failed' && s.phase === 'done';
      if (sameRunIdAlreadyHandled && !isFailedToDoneUpgrade) return;

      if (s.phase === 'failed') {
        this.dismissActive();
        this.lastHandledRunId = runId;
        this.lastHandledPhase = 'failed';

        const failures = s.verificationFailures ?? [];
        const details = failures.map(
          f => `${f.step}: ${f.observed ?? '(none)'} (expected ${f.expected ?? '?'})`
        );

        // Three failure shapes, ordered most-specific first. The
        // "still starting up" substring is the contract with the backend's
        // UpdateVerifier.DescribeHttpFailure helper (see backend.Tests
        // /UpdateVerifierTests.Status0_WithBackendAliveTrue_SaysStillStartingUp).
        const hasStillStartingUp = failures.some(f => f.observed?.includes('still starting up'));
        const hasNoResponse = failures.some(f => f.observed?.includes('no response'));
        const hasVerificationFailure = failures.length > 0;
        const message = hasVerificationFailure
          ? (hasStillStartingUp
            ? 'The backend is still starting up and did not finish draining within the verification window. Wait a moment and retry; rolling back is usually not needed in this case.'
            : hasNoResponse
              ? 'The backend did not respond after the update. It may still be starting up. You can roll back to the previous version or wait and retry.'
              : `Verification failed after restart: ${failures.map(f => f.step).join(', ')} did not pass. Roll back to restore the previous version.`)
          : (s.message ?? 'The update did not succeed.');

        this.activeToastId = this.notify.notify({
          kind: 'error',
          title: 'Update failed',
          message,
          details,
          durationMs: 0,
          actions: [
            {
              label: 'Roll back',
              testId: 'toast-update-rollback',
              primary: true,
              callback: () => {
                this.dismissed.add(runId);
                this.client.rollback(runId).catch(() => { /* status poll surfaces failure */ });
              },
            },
            {
              label: 'Other runs…',
              testId: 'toast-update-other-runs',
              callback: () => {
                this.dismissed.add(runId);
                const el = document.querySelector('[data-testid="update-center-trigger"]') as HTMLElement | null;
                el?.click();
              },
            },
            {
              label: 'Dismiss',
              testId: 'toast-update-dismiss',
              callback: () => { this.dismissed.add(runId); },
            },
          ],
        });
        return;
      }

      if (s.phase === 'done' && s.lastRunFinishedAt) {
        const finishedMs = Date.parse(s.lastRunFinishedAt);
        // The 60 s freshness gate is bypassed for the failed -> done
        // upgrade path: if the operator already saw a "failed" toast, the
        // belated success must overwrite it even when several minutes
        // have passed (e.g. cold-start drain that finally returned 200).
        const fresh = !Number.isNaN(finishedMs) && Date.now() - finishedMs <= 60_000;
        if (!fresh && !isFailedToDoneUpgrade) return;

        this.dismissActive();
        this.lastHandledRunId = runId;
        this.lastHandledPhase = 'done';

        const hasCodeChange = s.lastRunHeadAfter && s.lastRunHeadBefore && s.lastRunHeadAfter !== s.lastRunHeadBefore;

        if (hasCodeChange) {
          this.activeToastId = this.notify.notify({
            kind: 'success',
            title: 'Update finished',
            message: `${s.lastRunHeadBefore?.slice(0, 7)} → ${s.lastRunHeadAfter?.slice(0, 7)}. Reload required for the FE to pick up new code.`,
            durationMs: 0,
            actions: [
              {
                label: 'Reload',
                testId: 'toast-update-reload',
                primary: true,
                callback: () => {
                  this.dismissed.add(runId);
                  if (typeof window !== 'undefined') window.location.reload();
                },
              },
              {
                label: 'Dismiss',
                testId: 'toast-update-done-dismiss',
                callback: () => { this.dismissed.add(runId); },
              },
            ],
          });
        } else {
          this.activeToastId = this.notify.notify({
            kind: 'info',
            title: 'Update finished',
            message: `No code change (${s.lastRunHeadAfter?.slice(0, 7) ?? '?'}); reload not required.`,
            durationMs: 6000,
          });
          this.dismissed.add(runId);
        }
      }
    });
  }

  private dismissActive(): void {
    if (this.activeToastId !== null) {
      this.notify.dismiss(this.activeToastId);
      this.activeToastId = null;
    }
  }
}
