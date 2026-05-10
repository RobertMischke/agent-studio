import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { UpdateClientService } from '../../../../services/update.service';

/**
 * Always-mounted banner for finished-run notifications. The 'running'
 * phase is owned by <app-update-block-modal />; the 'behind' indicator
 * lives in the version badge + Update Center. This component owns:
 *
 *   - 'done' confirmation toast — a 60-second green strip after a
 *     successful run, driven by `lastRunFinishedAt` (ADR-0031). When
 *     `lastRunHeadAfter !== lastRunHeadBefore` we surface a "reload"
 *     button as the explicit cure for the broken-HMR symptom seen on
 *     2026-05-06: HMR-swapped Angular components can have orphaned
 *     click handlers and the user has no other in-app signal that a
 *     hard reload is required.
 *
 *   - 'failed' alert — red strip with the verification-failure list and
 *     a manual "Roll back" button (no automatic call; auto-rollback is
 *     opt-in on the server via ATP_UPDATE_AUTO_ROLLBACK).
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

  /** Tracks "now" so the 60-second linger window expires reactively. */
  private readonly nowMs = signal(Date.now());

  /**
   * ADR-0031: how many seconds the green completion strip lingers. Mirrors
   * the server option `DoneLingerSeconds`. We don't read it from the wire
   * (it's a server-internal constant); the FE just enforces the same 60 s.
   */
  private readonly DONE_LINGER_MS = 60_000;

  constructor() {
    // Tick once a second while the toast is potentially visible so the
    // computed mode() expires without a manual refresh.
    let timer: ReturnType<typeof setInterval> | null = null;
    effect(() => {
      const s = this.status();
      const hasRecent = !!s?.lastRunFinishedAt;
      if (hasRecent && timer === null) {
        timer = setInterval(() => this.nowMs.set(Date.now()), 1_000);
      } else if (!hasRecent && timer !== null) {
        clearInterval(timer);
        timer = null;
      }
    });
  }

  /**
   *   done            - last run succeeded recently and within linger window
   *   done-no-change  - succeeded but headAfter === headBefore (no reload needed)
   *   failed          - last run failed and isn't dismissed yet
   *   hidden          - nothing to show
   */
  readonly mode = computed<'done' | 'done-no-change' | 'failed' | 'hidden'>(() => {
    const s = this.status();
    if (!s) return 'hidden';
    if (s.isRunning) return 'hidden';

    const dismissedRunId = this.dismissed();
    const sameRunStillDismissed = dismissedRunId !== null && s.currentRunId === dismissedRunId;

    if (s.phase === 'failed' && !sameRunStillDismissed) return 'failed';

    if (s.phase === 'done' && !sameRunStillDismissed && s.lastRunFinishedAt) {
      const finishedMs = Date.parse(s.lastRunFinishedAt);
      if (!Number.isNaN(finishedMs) && this.nowMs() - finishedMs <= this.DONE_LINGER_MS) {
        return s.lastRunHeadAfter && s.lastRunHeadBefore && s.lastRunHeadAfter !== s.lastRunHeadBefore
          ? 'done'
          : 'done-no-change';
      }
    }
    return 'hidden';
  });

  readonly headBefore = computed(() => this.status()?.lastRunHeadBefore ?? '');
  readonly headAfter = computed(() => this.status()?.lastRunHeadAfter ?? '');

  readonly verificationFailures = computed(() => this.status()?.verificationFailures ?? []);

  dismiss(): void {
    const s = this.status();
    if (s?.currentRunId) this.dismissed.set(s.currentRunId);
    else this.dismissed.set('null-run');
  }

  /**
   * The explicit cure for the broken-HMR symptom: a fresh window with no
   * stale Angular components. We do this on user click only — never
   * automatically — so the user keeps full control over the reload.
   */
  hardReload(): void {
    if (typeof window !== 'undefined') window.location.reload();
  }

  /** Manual rollback: opt-in only. Disabled while a rollback is in flight. */
  async rollback(): Promise<void> {
    const s = this.status();
    if (!s?.currentRunId) return;
    try {
      await this.client.rollback(s.currentRunId);
    } catch {
      /* the next status poll will reflect the failure */
    }
  }
}
