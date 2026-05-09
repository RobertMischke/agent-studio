import { Injectable, OnDestroy, computed, signal } from '@angular/core';
import { JobInfo, SessionEventsResponse } from '../../models/job.model';
import { JobService } from '../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../utils/visible-interval';

/**
 * Polls the per-job session-event log every 10 s. Drives the
 * "session continued / lost" chip in the protocol pane header so the
 * user can tell at a glance whether a follow-up actually rode the
 * existing CLI session or had to reconstruct from files.
 *
 * Slower cadence than ClaudeSessionPollService (5 s) on purpose —
 * session events only flip on start/continue/recovery, not per turn.
 */
@Injectable()
export class SessionEventsPollService implements OnDestroy {
  readonly response = signal<SessionEventsResponse | null>(null);

  /** The most recent event in the log, or null when there is none. */
  readonly latest = computed(() => {
    const r = this.response();
    if (!r || r.events.length === 0) return null;
    return r.events[r.events.length - 1];
  });

  /**
   * Number of segments in the chain — i.e. one more than the number of
   * `(recovery)` markers. A value of 1 means "uninterrupted lineage";
   * higher values mean the user has had to reconstruct at least once.
   */
  readonly chainSegmentCount = computed(() => {
    const r = this.response();
    if (!r || r.sessionChain.length === 0) return 0;
    return r.sessionChain.filter((s) => s === '(recovery)').length + 1;
  });

  /** Number of real (non-recovery-marker) session ids on record. */
  readonly chainLength = computed(() => {
    const r = this.response();
    if (!r) return 0;
    return r.sessionChain.filter((s) => s && s !== '(recovery)').length;
  });

  private timer: VisibleIntervalHandle | null = null;
  private currentJob: { id: string; watchPath: string } | null = null;
  private currentKey = '';

  constructor(private jobService: JobService) {}

  syncTo(info: JobInfo | null | undefined): void {
    const key = info ? `${info.watchPath}::${info.id}` : '';
    if (key === this.currentKey) return;
    this.currentKey = key;
    this.stop();
    if (!info) {
      this.response.set(null);
      return;
    }
    this.currentJob = { id: info.id, watchPath: info.watchPath };
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), 10_000);
  }

  refresh(): void {
    const job = this.currentJob;
    if (!job) {
      this.response.set(null);
      return;
    }
    this.jobService.getSessionEvents(job.id, job.watchPath).subscribe({
      next: (res) => this.response.set(res ?? null),
      error: () => { /* non-fatal — keep previous snapshot */ }
    });
  }

  stop(): void {
    if (this.timer) {
      clearVisibleInterval(this.timer);
      this.timer = null;
    }
  }

  ngOnDestroy(): void {
    this.stop();
  }
}
