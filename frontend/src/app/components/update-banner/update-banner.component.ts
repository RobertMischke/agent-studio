import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { UpdateClientService } from '../../services/update.service';
import { DevToolsService } from '../../services/dev-tools.service';
import { ErrorDialogService } from '../../services/error-dialog.service';

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
  private readonly devTools = inject(DevToolsService);
  private readonly errors = inject(ErrorDialogService);

  readonly status = this.client.status;
  readonly serviceUnreachable = this.client.serviceUnreachable;
  readonly isRunning = this.client.isRunning;
  readonly behindBy = this.client.behindBy;
  // Dev mode = the same gate the existing Update-Stable button uses,
  // surfaced via DevTools flags from /api/environment.
  readonly isDev = computed(() => this.devTools.flags().updateStableEnabled);

  /** Hide the success/failure toast after the user has clearly seen it. */
  private readonly dismissed = signal<string | null>(null);

  /**
   * The banner has four mutually exclusive modes; expose one signal so the
   * template stays a flat *ngIf chain.
   *
   *   running  - update is in flight; sticky, blocks dismiss
   *   behind   - dev mode + behindBy>0 + idle; offers a Trigger button
   *   done     - last run succeeded recently and isn't dismissed yet
   *   failed   - last run failed and isn't dismissed yet
   *   hidden   - nothing to show
   */
  readonly mode = computed<'running' | 'behind' | 'done' | 'failed' | 'hidden'>(() => {
    const s = this.status();
    if (!s) return 'hidden';
    if (s.isRunning) return 'running';

    const dismissedRunId = this.dismissed();
    const sameRunStillDismissed = dismissedRunId !== null && s.currentRunId === dismissedRunId;

    if (s.phase === 'failed' && !sameRunStillDismissed) return 'failed';
    if (s.phase === 'done' && !sameRunStillDismissed) return 'done';

    if (this.isDev() && s.behindBy > 0) return 'behind';
    return 'hidden';
  });

  /** What we render in the running banner; a one-line status. */
  readonly runningLine = computed(() => {
    const s = this.status();
    if (!s) return '';
    const phase = humanPhase(s.phase);
    return s.message ? `${phase} — ${s.message}` : phase;
  });

  readonly triggerInFlight = signal(false);

  async trigger(): Promise<void> {
    if (this.triggerInFlight()) return;
    this.triggerInFlight.set(true);
    try {
      await this.client.trigger('manual via banner', false);
      this.dismissed.set(null); // make the next done/failed toast visible
    } catch (err: any) {
      this.errors.show({
        title: 'Update trigger failed',
        message: err?.message ?? 'Could not reach UpdateService at :5039.',
      });
    } finally {
      this.triggerInFlight.set(false);
    }
  }

  dismiss(): void {
    const s = this.status();
    if (s?.currentRunId) this.dismissed.set(s.currentRunId);
    else this.dismissed.set('null-run');
  }
}

function humanPhase(phase: string): string {
  switch (phase) {
    case 'preparing':       return 'Preparing update';
    case 'pausing-runners': return 'Pausing runners';
    case 'pulling':         return 'Pulling and restarting';
    case 'building':        return 'Building';
    case 'restarting':      return 'Waiting for backend';
    case 'resuming':        return 'Resuming runners';
    case 'done':            return 'Done';
    case 'failed':          return 'Failed';
    default:                return phase;
  }
}
