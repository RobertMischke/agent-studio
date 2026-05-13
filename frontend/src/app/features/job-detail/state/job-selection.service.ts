import { Injectable, computed, inject, signal } from '@angular/core';
import { JobDetail, JobInfo } from '../../../models/job.model';
import { JobService } from '../../../services/job.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { NotificationService } from '../../../services/notification.service';
import { LanePagerService } from './lane-pager.service';

/**
 * Cycle 9j job-detail-feature service: owns the "currently selected
 * job" state across the shell. Lifted out of `app.ts` per ADR-0034.
 *
 * Responsibilities:
 *   - `selected`        which JobDetail (if any) the side panel renders
 *   - `triageToast`     transient banner shown by the triage panel
 *   - `triageLanePeers` siblings in the same lane (drives j/k navigation)
 *   - URL sync          `?job=<id>&watchPath=<wp>` reproduces the open detail
 *   - request token     drops late getDetail replies so panel doesn't
 *                       flash back open after Esc/lane-cleared close
 *
 * The triage HANDLERS (onTriageMove/Delete/Start, advanceToNextInLane)
 * stay in the shell because they orchestrate JobService mutations,
 * ErrorDialogService, and the JobDetailComponent ViewChild
 * (`clearTriageActing`). This service is just the selection state +
 * navigation primitives those handlers call.
 */
@Injectable({ providedIn: 'root' })
export class JobSelectionService {
  private readonly jobService = inject(JobService);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly notifications = inject(NotificationService);
  private readonly pager = inject(LanePagerService);

  readonly selected = signal<JobDetail | null>(null);

  /**
   * Transient banner shown by the triage panel auto-advance flow.
   * Kept as a signal so the existing template binding in the detail
   * pane (`@if (triageToast(); as toast) { … }`) keeps working; the
   * shared notification stack mirrors the same message so the user gets
   * it from either surface depending on what is on screen.
   */
  readonly triageToast = signal<string | null>(null);
  private triageToastTimer: ReturnType<typeof setTimeout> | null = null;
  private lastTriageNotificationId: number | null = null;

  /**
   * Anchors `triageLanePeers` to the lane the panel was opened in, so
   * walking peers and detecting external moves both key off this rather
   * than the live `selected().info.state` (which can change under us).
   */
  triageLaneState: string | null = null;

  /**
   * Monotonic token guarding the latest `openDetail` request. Bumped on
   * every open/close so a late HTTP reply for a stale job is dropped.
   * Without this the late reply re-sets `selected` and the panel pops
   * back open — visible as a "j to advance, Esc fails to close" race.
   */
  private openDetailToken = 0;

  /**
   * Peers in the same on-disk lane as the currently selected job. The
   * mapping is keyed by the filesystem state on `info.state`; virtual
   * sub-lanes (e.g. `2-ready-intake`) merge back into their parent
   * because they share the same disk lane.
   */
  readonly triageLanePeers = computed<JobInfo[]>(() => {
    const sel = this.selected();
    if (!sel) return [];
    const g = this.jobService.grouped();
    switch (sel.info.state) {
      case '0-backlog':              return g.backlog ?? [];
      case '1-preparation':          return g.preparation ?? [];
      case '1a-orchestrator-prep':   return g.orchestratorPrep ?? [];
      case '1b-needs-human-review':  return g.needsHumanReview ?? [];
      case '2-ready':                return g.ready ?? [];
      case '3-progress':             return g.progress ?? [];
      case '3a-failed-pickup':       return g.failedPickup ?? [];
      case '4-auto-review':          return g.autoReview ?? [];
      case '5-human-review':         return g.humanReview ?? [];
      case '6-completed':            return g.completed ?? [];
      case '7-archive':              return g.archive ?? [];
      default:                       return [];
    }
  });

  isSelected(job: JobInfo): boolean {
    return this.selected()?.info.jobKey === job.jobKey;
  }

  /**
   * Open the side panel for `job`. Updates URL + fetches detail. By
   * default captures a fresh lane-pager snapshot anchored on `job` —
   * pass `{ keepPagerSnapshot: true }` from the pager step itself so
   * the in-progress iteration is preserved rather than re-captured.
   */
  openDetail(job: JobInfo, opts: { keepPagerSnapshot?: boolean } = {}): void {
    history.replaceState(null, '', `?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
    this.triageLaneState = job.state;
    if (!opts.keepPagerSnapshot) {
      this.pager.capture(job.state, this.triageLanePeers(), job.jobKey);
    }
    const token = ++this.openDetailToken;
    this.jobService.getDetail(job.id, job.watchPath).subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        this.selected.set(detail);
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        history.replaceState(null, '', window.location.pathname);
        this.errorDialog.show(err, {
          title: 'Failed to load task details',
          fallbackMessage: 'Failed to load task details',
          source: `Task ${job.id}`,
        });
      },
    });
  }

  /**
   * Pager Prev / Next: step the snapshot's index and fetch the detail
   * at the new position. The snapshot is preserved (we don't re-capture
   * from the current live lane), so a status change on the previously
   * visible job does not break the iteration order. Returns true when
   * a step actually happened.
   */
  pagerStep(direction: -1 | 1): boolean {
    const entry = this.pager.step(direction);
    if (!entry) return false;
    history.replaceState(null, '', `?job=${encodeURIComponent(entry.id)}&watchPath=${encodeURIComponent(entry.watchPath)}`);
    const token = ++this.openDetailToken;
    this.jobService.getDetail(entry.id, entry.watchPath).subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        // Re-anchor the triage lane to the snapshot's lane so the
        // external-advance effect in the shell doesn't fire on the
        // brand-new selection (the new job's state matches the lane
        // for as long as it's still in it; once the user mutates it
        // the suppress-once flag below handles the divergence).
        this.triageLaneState = this.pager.snapshot()?.lane ?? detail.info.state;
        this.selected.set(detail);
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        this.errorDialog.show(err, {
          title: 'Failed to load task details',
          fallbackMessage: 'Failed to load task details',
          source: `Task ${entry.id}`,
        });
      },
    });
    return true;
  }

  closeDetail(): void {
    // Bump the token so any in-flight `openDetail` reply (e.g. user
    // pressed `j` then immediately Esc) drops its `selected.set` and
    // the panel does not pop back open after we close it.
    this.openDetailToken++;
    this.selected.set(null);
    this.triageLaneState = null;
    this.pager.clear();
    history.replaceState(null, '', window.location.pathname);
  }

  /**
   * Reload-survival: hydrate `selected` from `?job=<id>&watchPath=<wp>`
   * on app boot. If the detail fetch fails (job was deleted while the
   * tab was closed), strip the query so the URL stops referencing a
   * nonexistent job.
   */
  restoreFromUrl(): void {
    const params = new URLSearchParams(window.location.search);
    const jobId = params.get('job');
    const watchPath = params.get('watchPath');
    if (jobId && watchPath) {
      this.jobService.getDetail(jobId, watchPath).subscribe({
        next: (detail) => {
          this.selected.set(detail);
          // Re-anchor the pager to the restored job's position in the
          // existing sessionStorage snapshot (if any). If the open job
          // isn't part of the stored snapshot, drop it — iteration was
          // never about this job.
          const snap = this.pager.snapshot();
          if (snap) {
            const inSnap = snap.jobs.some(j => j.jobKey === detail.info.jobKey);
            if (inSnap) {
              this.triageLaneState = snap.lane;
              this.pager.reanchorTo(detail.info.jobKey);
            } else {
              this.pager.clear();
            }
          }
        },
        error: () => history.replaceState(null, '', window.location.pathname),
      });
    } else {
      // No URL detail to restore - any stored pager snapshot is stale.
      this.pager.clear();
    }
  }

  /**
   * Used by `advanceToNextInLane` in the shell to land on the next
   * peer without touching the URL via `openDetail` (which would
   * publish an intermediate state). Caller is responsible for the
   * URL update + token check; we just set the signal.
   */
  setSelectedFromAdvance(detail: JobDetail, expectedToken: number): void {
    if (expectedToken !== this.openDetailToken) return;
    this.selected.set(detail);
  }

  /** Bumps the request token and returns the new value. Use from advance handlers. */
  bumpOpenDetailToken(): number {
    return ++this.openDetailToken;
  }

  /**
   * Show a transient triage banner; auto-clears after `durationMs`.
   * Also raises a unified `info` notification so the same outcome shows
   * up in the shared notification stack — keeping the look consistent
   * with other app-wide feedback while the detail-anchored banner stays
   * for users focused on the panel.
   */
  showTriageToast(msg: string, durationMs: number = 3000): void {
    if (this.triageToastTimer) clearTimeout(this.triageToastTimer);
    this.triageToast.set(msg);
    this.triageToastTimer = setTimeout(() => {
      this.triageToast.set(null);
      this.triageToastTimer = null;
    }, durationMs);
    if (this.lastTriageNotificationId != null) {
      this.notifications.dismiss(this.lastTriageNotificationId);
    }
    this.lastTriageNotificationId = this.notifications.notify({
      message: msg,
      kind: 'info',
      durationMs,
    });
  }
}
