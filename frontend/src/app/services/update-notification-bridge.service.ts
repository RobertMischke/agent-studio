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
  private dismissed = new Set<string>();

  constructor() {
    effect(() => {
      const s = this.client.status();
      if (!s) return;
      if (s.isRunning) return;

      const runId = s.currentRunId ?? 'null-run';

      if (this.dismissed.has(runId)) return;
      if (this.lastHandledRunId === runId) return;

      if (s.phase === 'failed') {
        this.dismissActive();
        this.lastHandledRunId = runId;

        const failures = s.verificationFailures ?? [];
        const details = failures.map(
          f => `${f.step}: ${f.observed ?? '(none)'} (expected ${f.expected ?? '?'})`
        );

        this.activeToastId = this.notify.notify({
          kind: 'error',
          title: 'Update failed',
          message: s.message ?? 'The update did not succeed.',
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
        if (Number.isNaN(finishedMs) || Date.now() - finishedMs > 60_000) return;

        this.dismissActive();
        this.lastHandledRunId = runId;

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
