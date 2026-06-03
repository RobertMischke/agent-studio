import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { TaskDetail, TaskInfo } from '../../../models/task.model';
import { TaskService } from '../../../services/task.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';
import { NotificationService } from '../../../services/notification.service';
import { TaskDetailPrefetchService } from './task-detail-prefetch.service';
import { LanePagerService } from './lane-pager.service';
import { perfMark, perfMeasure } from '../../../utils/perf-tracker';

/**
 * Cycle 9j job-detail-feature service: owns the "currently selected
 * job" state across the shell. Lifted out of `app.ts` per ADR-0034.
 *
 * Responsibilities:
 *   - `selected`        which TaskDetail (if any) the side panel renders
 *   - `triageToast`     transient banner shown by the triage panel
 *   - `triageLanePeers` siblings in the same lane (drives j/k navigation)
 *   - URL sync          `?job=<id>&watchPath=<wp>` reproduces the open detail
 *   - request token     drops late getDetail replies so panel doesn't
 *                       flash back open after Esc/lane-cleared close
 *
 * The triage HANDLERS (onTriageMove/Delete/Start, advanceToNextInLane)
 * stay in the shell because they orchestrate TaskService mutations,
 * ErrorDialogService, and the TaskDetailComponent ViewChild
 * (`clearTriageActing`). This service is just the selection state +
 * navigation primitives those handlers call.
 */
@Injectable({ providedIn: 'root' })
export class TaskSelectionService {
  private readonly jobService = inject(TaskService);
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly notifications = inject(NotificationService);
  private readonly pager = inject(LanePagerService);
  private readonly prefetch = inject(TaskDetailPrefetchService);

  /** How many slots ahead of the current pager index to warm. */
  private static readonly PREFETCH_LOOKAHEAD = 2;

  constructor() {
    // Ensure the lane-pager snapshot covers the currently selected job.
    // `openDetail` captures synchronously on the board-click path, so this
    // effect is a no-op there. The deep-link / URL-restore path sets
    // `selected` without capturing (no preceding click, and grouped() may
    // still be loading) - the effect re-runs once grouped() lands and
    // captures from the live lane peers so the pager appears without
    // requiring the user to press an arrow key first.
    //
    // `lastEnsuredJobKey` guards against re-capture after a mutation that
    // removes the open job from the snapshot (delete from menu, lane
    // dropdown move): `removeAndAdvance` shrinks the iteration BEFORE
    // `selected` is reset to the next job, so for one effect tick `snap`
    // no longer contains `selected.taskKey`. Without the guard the effect
    // would re-capture from the live (still-stale) grouped lane and
    // clobber the carefully-preserved iteration ordering.
    effect(() => {
      const sel = this.selected();
      if (!sel) {
        this.lastEnsuredJobKey = null;
        return;
      }
      const taskKey = sel.info.taskKey;
      const snap = this.pager.snapshot();
      if (snap && snap.jobs.some(j => j.taskKey === taskKey)) {
        // Existing iteration covers the open job; just keep the index
        // aligned (no-op when already aligned).
        this.pager.reanchorTo(taskKey);
        this.lastEnsuredJobKey = taskKey;
        return;
      }
      if (this.lastEnsuredJobKey === taskKey) return;
      const peers = this.peersForLane(sel.info.state);
      if (peers.length === 0) return;
      this.pager.capture(sel.info.state, peers, taskKey);
      this.lastEnsuredJobKey = taskKey;
    });

    // Detail prefetch: warm the next 1..PREFETCH_LOOKAHEAD entries in the
    // current pager snapshot whenever it changes. This is what makes the
    // accept -> next-task navigation feel instant: by the time the user
    // clicks Mark-as-Done, the next peer's TaskDetail is already cached.
    effect(() => {
      const snap = this.pager.snapshot();
      if (!snap) return;
      const lookahead = TaskSelectionService.PREFETCH_LOOKAHEAD;
      for (let offset = 1; offset <= lookahead; offset++) {
        const entry = snap.jobs[snap.index + offset];
        if (!entry) break;
        this.prefetch.prefetch(entry.id, entry.watchPath);
      }
    });
  }

  private lastEnsuredJobKey: string | null = null;

  /**
   * Set to `true` when the user starts a triage decision (accept etc.)
   * and consumed by the next selection update so we mark the
   * `accept-to-next-rendered` performance interval exactly once per
   * click. The mark/measure is best-effort: any environment without
   * `performance.mark` (older browsers, SSR) silently no-ops.
   */
  private awaitingNextTaskRender = false;

  /**
   * Stamp the start of the accept -> next-task render measurement.
   * Called by the triage controller right before it tears down the
   * outgoing job so the marker brackets exactly the latency the user
   * feels. Names are stable strings the Playwright budget spec asserts
   * on.
   */
  markAcceptClick(): void {
    this.awaitingNextTaskRender = true;
    try {
      performance.mark('accept-click');
    } catch { /* mark API missing or out of buffer space */ }
  }

  /** Internal: pair the `accept-click` mark with `next-task-rendered`. */
  private markNextTaskRendered(): void {
    if (!this.awaitingNextTaskRender) return;
    this.awaitingNextTaskRender = false;
    try {
      performance.mark('next-task-rendered');
      performance.measure('accept-to-next-task', 'accept-click', 'next-task-rendered');
    } catch { /* the click mark may have been GC'd; not fatal */ }
  }

  readonly selected = signal<TaskDetail | null>(null);

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
  readonly triageLanePeers = computed<TaskInfo[]>(() => {
    const sel = this.selected();
    if (!sel) return [];
    return this.peersForLane(sel.info.state);
  });

  /**
   * Peers in a specific on-disk lane. Used by `openDetail` to capture
   * the lane-pager snapshot at the moment of click, where `selected` is
   * still the previous detail (or null) and the lookup must key off the
   * incoming job's state, not the stale signal.
   */
  peersForLane(state: string): TaskInfo[] {
    const g = this.jobService.grouped();
    switch (state) {
      case '0-backlog':              return g.backlog ?? [];
      case '1-preparation':          return g.preparation ?? [];
      case '1a-orchestrator-prep':   return g.orchestratorPrep ?? [];
      case '2-ready':                return g.ready ?? [];
      case '3-progress':             return g.progress ?? [];
      case '3a-failed-pickup':       return g.failedPickup ?? [];
      case '4-auto-review':          return g.autoReview ?? [];
      case '5-human-review':         return g.humanReview ?? [];
      case '6-completed':            return g.completed ?? [];
      case '7-archive':              return g.archive ?? [];
      default:                       return [];
    }
  }

  isSelected(job: TaskInfo): boolean {
    return this.selected()?.info.taskKey === job.taskKey;
  }

  /**
   * Open the side panel for `job`. Updates URL + fetches detail. By
   * default captures a fresh lane-pager snapshot anchored on `job` —
   * pass `{ keepPagerSnapshot: true }` from the pager step itself so
   * the in-progress iteration is preserved rather than re-captured.
   */
  openDetail(job: TaskInfo, opts: { keepPagerSnapshot?: boolean } = {}): void {
    // Step 1 of the perf-baseline contract: job-select click span. The
    // accept-to-next-task pipeline owns its own marks via markAcceptClick;
    // this one covers ad-hoc board clicks where no accept-click preceded.
    perfMark('job-select-click');
    history.replaceState(null, '', `?job=${encodeURIComponent(job.id)}&watchPath=${encodeURIComponent(job.watchPath)}`);
    this.triageLaneState = job.state;
    if (!opts.keepPagerSnapshot) {
      // Capture peers for `job.state` directly: at this point `selected`
      // may still be null or pointing at a prior detail in a different
      // lane, so the `triageLanePeers` computed (which keys off
      // `selected.info.state`) would yield the wrong list or an empty
      // list. `peersForLane` looks up the live grouped lane.
      this.pager.capture(job.state, this.peersForLane(job.state), job.taskKey);
    }
    const token = ++this.openDetailToken;
    // Instant-paint path: serve a prefetched detail synchronously when
    // one is on hand, then re-fetch in the background so the panel
    // catches any post-prefetch drift (status, log tail). Without the
    // re-fetch a stale detail could linger past its TTL.
    const cached = this.prefetch.take(job.id, job.watchPath);
    if (cached) {
      this.selected.set(cached);
      this.markNextTaskRendered();
      perfMark('job-select-rendered');
      perfMeasure('job-select-to-rendered', 'job-select-click', 'job-select-rendered');
    }
    this.jobService.getDetail(job.id, job.watchPath).subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        this.selected.set(detail);
        if (!cached) {
          this.markNextTaskRendered();
          perfMark('job-select-rendered');
          perfMeasure('job-select-to-rendered', 'job-select-click', 'job-select-rendered');
        }
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        // Don't surface an error after we already painted from cache -
        // the panel is already showing the cached detail and a transient
        // network blip should not pop a modal.
        if (cached) return;
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
    const cached = this.prefetch.take(entry.id, entry.watchPath);
    if (cached) {
      this.triageLaneState = this.pager.snapshot()?.lane ?? cached.info.state;
      this.selected.set(cached);
      this.markNextTaskRendered();
    }
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
        if (!cached) this.markNextTaskRendered();
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        if (cached) return;
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
          // isn't part of the stored snapshot, drop it - the
          // ensure-snapshot effect then captures a fresh iteration from
          // the job's lane once grouped() lands, so the pager appears
          // without requiring keyboard navigation first.
          const snap = this.pager.snapshot();
          if (snap && snap.jobs.some(j => j.taskKey === detail.info.taskKey)) {
            this.triageLaneState = snap.lane;
            this.pager.reanchorTo(detail.info.taskKey);
          } else {
            if (snap) this.pager.clear();
            // Anchor triage navigation on the restored job's lane so the
            // external-move auto-advance in the shell still has a lane
            // to compare against after a deep-link restore.
            this.triageLaneState = detail.info.state;
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
  setSelectedFromAdvance(detail: TaskDetail, expectedToken: number): void {
    if (expectedToken !== this.openDetailToken) return;
    this.selected.set(detail);
    this.markNextTaskRendered();
  }

  /**
   * After a user-initiated mutation removes the currently visible job
   * from the lane (delete from detail, lane change via state dropdown,
   * triage move/delete): drop the job from the pager snapshot and
   * navigate to the entry that now sits at its slot. When the lane is
   * empty (the job was the last in the iteration) close the panel and
   * surface a "Lane cleared." toast so the user knows the iteration
   * finished.
   *
   * Returns `true` when an advance happened (the panel now shows the
   * next job), `false` when the lane was cleared or there is no active
   * pager snapshot (e.g. the detail was opened from a deep-link with
   * no preceding board click); in the latter case the caller's default
   * post-mutation behaviour - usually `closeDetail()` - still applies.
   */
  advanceAfterMutation(departingJobKey: string): boolean {
    const snapBefore = this.pager.snapshot();
    const wasInSnapshot = !!snapBefore && snapBefore.jobs.some(j => j.taskKey === departingJobKey);
    const entry = this.pager.removeAndAdvance(departingJobKey);
    if (!entry) {
      if (wasInSnapshot) {
        // Snapshot existed and the departing job was in it: removeAndAdvance
        // cleared the iteration. Close the panel and toast so the user knows
        // they finished the lane.
        this.closeDetail();
        this.showTriageToast('Lane cleared.');
        return true;
      }
      return false;
    }
    this.triageLaneState = this.pager.snapshot()?.lane ?? this.triageLaneState;
    history.replaceState(
      null,
      '',
      `?job=${encodeURIComponent(entry.id)}&watchPath=${encodeURIComponent(entry.watchPath)}`,
    );
    const token = ++this.openDetailToken;
    // Optimistic-navigation path: serve a prefetched detail synchronously
    // when one is on hand so the panel re-renders without waiting for the
    // move POST or a fresh GET. The follow-up fetch reconciles any drift
    // (status/log tail) and is the source of truth on a cache miss.
    const cached = this.prefetch.take(entry.id, entry.watchPath);
    if (cached) {
      this.selected.set(cached);
      this.markNextTaskRendered();
    }
    this.jobService.getDetail(entry.id, entry.watchPath).subscribe({
      next: (detail) => {
        if (token !== this.openDetailToken) return;
        this.selected.set(detail);
        if (!cached) this.markNextTaskRendered();
      },
      error: (err) => {
        if (token !== this.openDetailToken) return;
        if (cached) return;
        this.errorDialog.show(err, {
          title: 'Failed to load task details',
          fallbackMessage: 'Failed to load task details',
          source: `Task ${entry.id}`,
        });
      },
    });
    return true;
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
  showTriageToast(msg: string, durationMs = 3000): void {
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
