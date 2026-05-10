import { Injectable, OnDestroy, computed, signal } from '@angular/core';
import type { JobInfo } from '../../../models/job.model';
import type { RunTimeline } from '../../../features/run-timeline';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';

/**
 * Polls the per-job run timeline (`/api/jobs/{id}/runs`) every 5 s
 * while a job is open. The timeline is the unit-of-conversation surface
 * documented in `docs/design-principles.md` - one card per CLI
 * invocation between user inputs - so the cadence has to keep up with
 * a user starting / stopping a run, not with per-frame log streaming.
 *
 * 5 s matches ClaudeSessionPollService and is fast enough that the
 * "runs: 3" badge in the protocol-pane header updates a heartbeat
 * after the runner emits a new session-event row, but slow enough that
 * the activity-log poll (which runs at sub-second cadence) is the
 * source of live tail updates rather than this one.
 */
@Injectable()
export class RunTimelinePollService implements OnDestroy {
  readonly timeline = signal<RunTimeline | null>(null);

  /** Convenience accessor: the runs array, empty when there is no data yet. */
  readonly runs = computed(() => this.timeline()?.runs ?? []);

  /** True while the most recent run is still streaming output. */
  readonly hasActiveRun = computed(() => this.timeline()?.hasActiveRun === true);

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
      this.timeline.set(null);
      return;
    }
    this.currentJob = { id: info.id, watchPath: info.watchPath };
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), 5_000);
  }

  refresh(): void {
    const job = this.currentJob;
    if (!job) {
      this.timeline.set(null);
      return;
    }
    this.jobService.getRunTimeline(job.id, job.watchPath).subscribe({
      next: (res) => this.timeline.set(res ?? null),
      error: () => { /* non-fatal: keep previous snapshot */ }
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
