import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { UpdateClientService } from '../../services/update.service';

/**
 * Always-mounted banner that:
 *  - stays invisible while everything is fine and we're up to date,
 *  - shows a "behind by N commits" hint with a manual update button when
 *    we're in dev mode and origin/main has moved ahead,
 *  - shows a sticky "Update in progress" banner while the orchestrator is
 *    running, surviving the moment the main backend restarts (the FE
 *    still polls the standalone UpdateService on port 5039),
 *  - shows a brief "Done — updated A → B" confirmation after success,
 *  - shows the failure reason when an update ends with status=failed.
 *
 * Mutations across the rest of the app should consult
 * `UpdateClientService.mutationsBlocked` and surface their own disabled
 * state; this banner only handles the visual notification.
 */
@Component({
  selector: 'app-update-banner',
  standalone: true,
  imports: [CommonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './update-banner.component.html',
  styleUrl: './update-banner.component.scss',
})
export class UpdateBannerComponent {
  private readonly client = inject(UpdateClientService);

  readonly status = this.client.status;

  /** Hide the success/failure toast after the user has clearly seen it. */
  private readonly dismissed = signal<string | null>(null);

  /**
   * Toast-style notifier for finished runs. The 'running' phase is owned
   * by <app-update-block-modal />; the 'behind' indicator lives in the
   * version badge + Update Center. We only own the brief done/failed
   * toast that informs the user the *previous* run completed.
   *
   *   done     - last run succeeded recently and isn't dismissed yet
   *   failed   - last run failed and isn't dismissed yet
   *   hidden   - nothing to show
   */
  readonly mode = computed<'done' | 'failed' | 'hidden'>(() => {
    const s = this.status();
    if (!s) return 'hidden';
    if (s.isRunning) return 'hidden'; // block modal handles this

    const dismissedRunId = this.dismissed();
    const sameRunStillDismissed = dismissedRunId !== null && s.currentRunId === dismissedRunId;

    if (s.phase === 'failed' && !sameRunStillDismissed) return 'failed';
    if (s.phase === 'done' && !sameRunStillDismissed) return 'done';
    return 'hidden';
  });

  dismiss(): void {
    const s = this.status();
    if (s?.currentRunId) this.dismissed.set(s.currentRunId);
    else this.dismissed.set('null-run');
  }
}
