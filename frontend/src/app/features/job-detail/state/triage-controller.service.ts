import { Injectable, inject } from '@angular/core';
import { JobInfo } from '../../../models/job.model';
import { JobService } from '../../../services/job.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { JobSelectionService } from './job-selection.service';

/**
 * Cycle 10c job-detail-feature controller: orchestrates the triage
 * panel's move / move-to-top / delete / start actions, the j/k peer
 * navigation, and the auto-advance-after-mutation flow that walks the
 * user to the next job in the same lane.
 *
 * Bridges to the shell's `JobDetailComponent` ViewChild (where
 * `triageActing` lives) via a tiny callback the shell registers in
 * `ngAfterViewInit`. Avoids a hard dependency on the component class
 * here — the controller is a pure state-machine + JobService caller.
 *
 * The auto-advance EFFECT stays in the shell because it needs to
 * read `jobDetailRef.triageActingId()` reactively; the effect calls
 * `advanceToNextInLane` here when the conditions match.
 */
@Injectable({ providedIn: 'root' })
export class TriageController {
  private readonly jobService = inject(JobService);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly jobSelection = inject(JobSelectionService);

  /**
   * Invoked at every triage decision so the JobDetailComponent's
   * "acting" highlight clears even when the move/delete fails. The
   * shell registers this callback once in `ngAfterViewInit`.
   */
  private clearActingCallback: (() => void) | null = null;

  setClearActingCallback(fn: (() => void) | null): void {
    this.clearActingCallback = fn;
  }

  private clearActing(): void {
    this.clearActingCallback?.();
  }

  // ---------- triage-panel actions ----------

  /**
   * Lane-specific move from the triage panel. Same path as drag-and-drop
   * (optimistic paint + persist + revert-on-error), plus auto-advance
   * to the next peer in the lane the user was triaging.
   */
  move(info: JobInfo, ev: { targetState: string; actionId: string }): void {
    const lane = this.jobSelection.triageLaneState ?? info.state;
    const peers = this.jobSelection.triageLanePeers();
    const snapshot = this.jobService.applyOptimisticMove(info.id, info.watchPath, ev.targetState);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJob(info.id, ev.targetState, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.advanceToNextInLane(lane, info.jobKey, peers);
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (snapshot) this.jobService.revertOptimisticMove(snapshot);
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  /** Triage "Move to top" (only on 2-ready). Stays in lane; clears acting. */
  moveToTop(info: JobInfo, _ev: { actionId: string }): void {
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJobToTop(info.id, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.clearActing();
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to move task to top',
          fallbackMessage: 'Failed to move task to the top of the Ready queue',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  /**
   * Triage "Delete". Confirm-on-first-click already happened in the
   * panel; we still surface the standard system confirm to match the
   * menu's delete flow (so the user does not lose the safety net by
   * accident).
   */
  delete(info: JobInfo, _ev: { actionId: string }): void {
    const lane = this.jobSelection.triageLaneState ?? info.state;
    const peers = this.jobSelection.triageLanePeers();
    const label = info.title || info.id;
    const message =
      `Delete this task?\n\n"${label}"\n\nThis removes the job folder and all its files (prompt, logs, results). Do you really want this?`;
    if (typeof window !== 'undefined' && !window.confirm(message)) {
      this.clearActing();
      return;
    }
    this.jobService.deleteJob(info.id, info.watchPath).subscribe({
      next: () => {
        this.jobService.refresh();
        this.advanceToNextInLane(lane, info.jobKey, peers);
      },
      error: (err) => {
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to delete task',
          fallbackMessage: 'Failed to delete task',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  /**
   * Triage "Run now": kick the start path then leave the panel on the
   * same job (it will transition to 3-progress on its own).
   */
  start(info: JobInfo, _ev: { actionId: string }): void {
    this.jobService.startJob(info.id, info.watchPath).subscribe({
      next: () => this.clearActing(),
      error: (err) => {
        this.clearActing();
        this.errorDialog.show(err, {
          title: 'Failed to start task',
          fallbackMessage: 'Failed to start task',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  // ---------- peer navigation (j / k / arrows / pager buttons) ----------

  /**
   * j / ↓ / → / pager-next: advance through the snapshot captured when
   * the user entered the detail view. The snapshot is intentionally
   * stable - a status change on the currently visible job preserves its
   * slot in the iteration, so this call still lands on the next slug
   * captured at entry time, not on whatever live ordering shows now.
   *
   * Falls back to the live lane peers when no snapshot is available
   * (e.g. detail opened from a URL with no prior iteration).
   */
  next(info: JobInfo): void {
    if (this.jobSelection.pagerStep(1)) return;
    const peers = this.jobSelection.triageLanePeers();
    if (peers.length === 0) return;
    const idx = peers.findIndex((p) => p.jobKey === info.jobKey);
    const nextIdx = idx < 0 ? 0 : Math.min(peers.length - 1, idx + 1);
    if (nextIdx === idx) return;
    this.jobSelection.openDetail(peers[nextIdx]);
  }

  /** k / ↑ / ← / pager-prev: see `next` - same snapshot semantics. */
  prev(info: JobInfo): void {
    if (this.jobSelection.pagerStep(-1)) return;
    const peers = this.jobSelection.triageLanePeers();
    if (peers.length === 0) return;
    const idx = peers.findIndex((p) => p.jobKey === info.jobKey);
    const prevIdx = idx < 0 ? 0 : Math.max(0, idx - 1);
    if (prevIdx === idx) return;
    this.jobSelection.openDetail(peers[prevIdx]);
  }

  // ---------- auto-advance after mutation / external move ----------

  /**
   * After a triage decision (or external move), find the next peer in
   * the lane the user was triaging in, excluding the job that just
   * left. If the lane is empty, close the panel and toast.
   *
   * `external` flips the toast wording so the user can tell whether
   * the advance was their click or someone else's reshuffle.
   */
  advanceToNextInLane(
    lane: string,
    departingJobKey: string,
    peersBefore: ReadonlyArray<JobInfo>,
    external = false,
  ): void {
    this.clearActing();
    // Compute the candidate from the snapshot of peers we had before
    // the mutation: optimistic-persist may have already filtered out
    // the moving job, but we want the next peer that was after it in
    // the original list.
    const idx = peersBefore.findIndex((p) => p.jobKey === departingJobKey);
    let next: JobInfo | null = null;
    if (idx >= 0) {
      next = peersBefore[idx + 1] ?? peersBefore[idx - 1] ?? null;
    } else if (peersBefore.length > 0) {
      next = peersBefore[0];
    }
    // Filter out the departing job itself (the snapshot may include
    // it; we also want to skip jobs that have since been moved out of
    // the lane).
    const live = this.jobSelection.triageLanePeers().filter(
      (p) => p.jobKey !== departingJobKey && p.state === lane,
    );
    const candidate = (next && live.find((p) => p.jobKey === next!.jobKey)) ?? live[0] ?? null;
    if (candidate) {
      // Re-anchor lane to the new job's state (same lane unless poll drift).
      this.jobSelection.triageLaneState = candidate.state;
      const token = this.jobSelection.bumpOpenDetailToken();
      this.jobService.getDetail(candidate.id, candidate.watchPath).subscribe({
        next: (detail) => {
          history.replaceState(
            null,
            '',
            `?job=${encodeURIComponent(candidate.id)}&watchPath=${encodeURIComponent(candidate.watchPath)}`,
          );
          this.jobSelection.setSelectedFromAdvance(detail, token);
        },
        error: () => { /* leave panel on the previous job; the parent effect will reconcile */ },
      });
      if (external) this.jobSelection.showTriageToast('Job was moved externally; advancing.');
      return;
    }
    // Lane cleared — close the panel and toast.
    this.jobSelection.closeDetail();
    this.jobSelection.showTriageToast('Lane cleared.');
  }

  // ---------- post-review helper ----------

  /**
   * "Complete and next review": the user just accepted the open
   * review job; refresh the board and either jump to the next
   * pending review or close the panel if none remain.
   */
  completeAndNextReview(): void {
    const currentJobKey = this.jobSelection.selected()?.info.jobKey;
    const reviewJobs = this.jobService.grouped().review.filter((j) => j.jobKey !== currentJobKey);
    this.jobService.refresh();
    if (reviewJobs.length > 0) {
      this.jobSelection.openDetail(reviewJobs[0]);
    } else {
      this.jobSelection.closeDetail();
    }
  }
}
