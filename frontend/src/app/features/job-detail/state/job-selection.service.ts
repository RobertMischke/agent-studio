import { Injectable, computed, inject, signal } from '@angular/core';
import { JobDetail, JobInfo } from '../../../models/job.model';
import { JobService } from '../../../services/job.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';

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

  readonly selected = signal<JobDetail | null>(null);

  /** Transient banner shown by the triage panel auto-advance flow. */
  readonly triageToast = signal<string | null>(null);
  private triageToastTimer: ReturnType<typeof setTimeout> | null = null;

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

  /** Open the side panel for `job`. Updates URL + fetches detail. */
  openDetail(job: JobInfo): void {
    history.replaceState(null, '', `?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
    this.triageLaneState = job.state;
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

  closeDetail(): void {
    // Bump the token so any in-flight `openDetail` reply (e.g. user
    // pressed `j` then immediately Esc) drops its `selected.set` and
    // the panel does not pop back open after we close it.
    this.openDetailToken++;
    this.selected.set(null);
    this.triageLaneState = null;
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
        next: (detail) => this.selected.set(detail),
        error: () => history.replaceState(null, '', window.location.pathname),
      });
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

  /** Show a transient triage banner; auto-clears after 3 s. */
  showTriageToast(msg: string, durationMs: number = 3000): void {
    if (this.triageToastTimer) clearTimeout(this.triageToastTimer);
    this.triageToast.set(msg);
    this.triageToastTimer = setTimeout(() => {
      this.triageToast.set(null);
      this.triageToastTimer = null;
    }, durationMs);
  }
}
