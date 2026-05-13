import { Injectable, inject, signal } from '@angular/core';
import { forkJoin } from 'rxjs';
import { JobInfo } from '../../../models/job.model';
import { JobService } from '../../../services/job.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { JobSelectionService } from '../../job-detail';

/**
 * Cycle 10b board-feature service: orchestrates the board's mutation
 * handlers (drag/drop move, reorder, delete, lane-dropdown move,
 * archive-all, file-saved, project-changed) so the shell stays a thin
 * coordinator. Each method:
 *
 *   - calls the relevant JobService API
 *   - applies optimistic snapshots where the board paints ahead of the
 *     POST (move + reorder)
 *   - reverts the snapshot + raises an ErrorDialog on failure
 *   - keeps the open detail in sync via JobSelectionService
 *
 * The shell's `confirm()` UX gate (window.confirm for delete) lives in
 * `confirmAndDeleteJob` so the service is the single place a delete
 * needs to round-trip user intent + backend + detail-close handling.
 */
@Injectable({ providedIn: 'root' })
export class BoardMutationsService {
  private readonly jobService = inject(JobService);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly jobSelection = inject(JobSelectionService);

  // ---------- drag-and-drop move ----------

  /**
   * Optimistic move: paint the new lane immediately, let the backend
   * catch up. While the POST is in flight, silent polls are suppressed
   * so a stale `/api/jobs/grouped` response can't repaint the old lane.
   * On failure, revert the local snapshot and surface the error.
   */
  moveJob(event: { jobId: string; watchPath: string; targetState: string }): void {
    // Virtual lanes inside the same filesystem state (e.g. the intake
    // sub-lane that splits 2-ready into "Human Ready" and "Orch Intake")
    // map back to the real state for the backend move; the orchestrator
    // intake loop is the only producer of the lane-defining `phase`
    // field, so a manual drag never has to write phase from the UI.
    if (event.targetState === '2-ready-intake') event = { ...event, targetState: '2-ready' };
    // Same-state drops (drag onto a sibling card in the same lane) are a
    // no-op: the column-level drop handler already filters the common path,
    // this is defense in depth so a stray emit cannot trigger a wasted
    // backend round-trip or a vanish-and-recover repaint.
    const moving = this.jobService.jobs().find((j) => j.id === event.jobId && j.watchPath === event.watchPath);
    if (moving && moving.state === event.targetState) return;
    const snapshot = this.jobService.applyOptimisticMove(event.jobId, event.watchPath, event.targetState);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJob(event.jobId, event.targetState, event.watchPath).subscribe({
      next: () => this.jobService.endOptimisticPersist(),
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (snapshot) this.jobService.revertOptimisticMove(snapshot);
        this.jobService.error.set(err.message || 'Failed to move job');
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${event.jobId}`,
        });
      },
    });
  }

  // ---------- within-lane reorder ----------

  /**
   * Optimistic reorder. The lane updates synchronously; in-flight POST
   * tracking + a short grace window after the response keep the
   * user-visible order stable while the backend rewrites job.json.
   */
  reorderJobs(event: { state: string; jobs: { jobId: string; watchPath: string }[] }): void {
    const before = this.jobService.applyOptimisticReorder(event.state, event.jobs);
    this.jobService.beginOptimisticPersist();
    this.jobService.reorderJobs(event.jobs).subscribe({
      next: () => this.jobService.endOptimisticPersist(),
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (before) this.jobService.revertOptimisticReorder(event.state, before);
        this.jobService.error.set(err.message || 'Failed to reorder');
        this.errorDialog.show(err, {
          title: 'Failed to reorder tasks',
          fallbackMessage: 'Failed to reorder tasks',
          source: `Column ${event.state}`,
        });
      },
    });
  }

  // ---------- delete (board + detail) ----------

  deleteFromBoard(job: JobInfo): void {
    this.confirmAndDeleteJob(job, false);
  }

  deleteFromDetail(info: JobInfo): void {
    this.confirmAndDeleteJob(info, true);
  }

  private confirmAndDeleteJob(job: JobInfo, closeDetailOnSuccess: boolean): void {
    const label = job.title || job.id;
    const message =
      `Delete this task?\n\n"${label}"\n\nThis removes the job folder and all its files (prompt, logs, results). Do you really want this?`;
    if (typeof window === 'undefined' || !window.confirm(message)) return;

    this.jobService.deleteJob(job.id, job.watchPath).subscribe({
      next: () => {
        if (closeDetailOnSuccess) this.jobSelection.closeDetail();
        this.jobService.refresh();
      },
      error: (err) => {
        this.errorDialog.show(err, {
          title: 'Failed to delete task',
          fallbackMessage: 'Failed to delete task',
          source: `Task ${job.id}`,
        });
        this.jobService.refresh();
      },
    });
  }

  // ---------- lane-dropdown move from detail ----------

  /**
   * Mirrors the drag-and-drop path so the board repaints optimistically
   * while the POST is in flight, then re-fetches the open detail so the
   * lane dropdown reflects the new lane. The detail-view's local
   * "changing" flag is cleared by the detail component's effect when
   * the new `state` arrives.
   */
  changeStateFromDetail(info: JobInfo, targetState: string): void {
    if (!targetState || targetState === info.state) return;
    // Re-anchor the triage lane to the user-chosen target so the
    // app-shell's external-advance effect does not interpret the user's
    // own dropdown change as a foreign reshuffle and jump away. The
    // LanePager snapshot keeps its original lane intentionally - the
    // iteration order survives status changes by design.
    this.jobSelection.triageLaneState = targetState;
    const snapshot = this.jobService.applyOptimisticMove(info.id, info.watchPath, targetState);
    this.jobService.beginOptimisticPersist();
    this.jobService.moveJob(info.id, targetState, info.watchPath).subscribe({
      next: () => {
        this.jobService.endOptimisticPersist();
        this.jobService.getDetail(info.id, info.watchPath).subscribe({
          next: (detail) => this.jobSelection.selected.set(detail),
          error: () => { /* polling will reconcile */ },
        });
      },
      error: (err) => {
        this.jobService.endOptimisticPersist();
        if (snapshot) this.jobService.revertOptimisticMove(snapshot);
        this.jobService.error.set(err.message || 'Failed to move job');
        this.errorDialog.show(err, {
          title: 'Failed to move task',
          fallbackMessage: 'Failed to move task',
          source: `Task ${info.id}`,
        });
      },
    });
  }

  // ---------- bulk archive ----------

  /**
   * Live flag for the Archive-all button: true while a bulk archive is
   * in flight so the column header can disable the trigger and show a
   * spinner. A double-click while archiving would re-submit the same N
   * folder moves and surface a 409 storm from the second pass.
   */
  readonly archiving = signal(false);

  /**
   * Move every job in `completed` to 7-archive in parallel. The shell
   * passes the list (typically `filteredGrouped().completed`) so the
   * service stays free of BoardFilters coupling.
   */
  archiveAllCompleted(completed: ReadonlyArray<JobInfo>): void {
    if (completed.length === 0) return;
    if (this.archiving()) return;
    this.archiving.set(true);
    const moves = completed.map((job) => this.jobService.moveJob(job.id, '7-archive', job.watchPath));
    forkJoin(moves).subscribe({
      next: () => {
        this.archiving.set(false);
        this.jobService.refresh();
      },
      error: (err) => {
        this.archiving.set(false);
        this.errorDialog.show(err, {
          title: 'Failed to archive tasks',
          fallbackMessage: 'One or more tasks could not be moved to Archive',
          source: 'Archive all',
        });
        this.jobService.refresh();
      },
    });
  }

  // ---------- detail-side post-mutation refresh ----------

  /**
   * Re-fetch the open detail and refresh the board. Wired to the
   * file-saved event from the detail's editors so changes propagate
   * to card titles + task-nav without a full reload.
   */
  refreshAfterFileSave(): void {
    const current = this.jobSelection.selected();
    if (current) {
      this.jobService.getDetail(current.info.id, current.info.watchPath).subscribe({
        next: (detail) => this.jobSelection.selected.set(detail),
      });
    }
    this.jobService.refresh(true);
  }

  /**
   * Project change from the detail view: the job has moved to a
   * different watchPath. Close the panel, refresh the board, then
   * re-open detail at the new location.
   */
  reopenAfterProjectChange(targetWatchPath: string): void {
    const current = this.jobSelection.selected();
    this.jobSelection.closeDetail();
    this.jobService.refresh();
    if (current) {
      // Re-open detail after refresh
      setTimeout(() => {
        this.jobService.getDetail(current.info.id, targetWatchPath).subscribe({
          next: (detail) => this.jobSelection.selected.set(detail),
          error: (err) => {
            this.errorDialog.show(err, {
              title: 'Task moved, but detail view could not be reopened',
              fallbackMessage: 'Task moved, but detail view could not be reopened automatically.',
              source: `Task ${current.info.id}`,
            });
          },
        });
      }, 500);
    }
  }
}
