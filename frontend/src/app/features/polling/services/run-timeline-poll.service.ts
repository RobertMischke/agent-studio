import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type { RunTimeline } from '../../../features/run-timeline';
import { JobService } from '../../../services/task.service';
import { JobBackgroundPoller } from './task-background-poller';

/**
 * Polls the per-job run timeline (`/api/jobs/{id}/runs`) every 5 s
 * while a job is open. The timeline is the unit-of-conversation surface
 * documented in `docs/design-principles.md` - one card per CLI
 * invocation between user inputs - so the cadence has to keep up with
 * a user starting / stopping a run, not with per-frame log streaming.
 *
 * 5 s matches ClaudeSessionPollService and is fast enough that the
 * "runs: 3" badge in the protocol-pane header updates a heartbeat
 * after the runner emits a new session-event row.
 */
@Injectable()
export class RunTimelinePollService extends JobBackgroundPoller<RunTimeline | null> {
  private readonly jobService = inject(JobService);

  protected readonly intervalMs = 5_000;

  readonly timeline = signal<RunTimeline | null>(null);

  /** Convenience accessor: the runs array, empty when there is no data yet. */
  readonly runs = computed(() => this.timeline()?.runs ?? []);

  /** True while the most recent run is still streaming output. */
  readonly hasActiveRun = computed(() => this.timeline()?.hasActiveRun === true);

  protected fetch(jobId: string, watchPath: string): Observable<RunTimeline | null> {
    return this.jobService.getRunTimeline(jobId, watchPath);
  }

  protected applyResponse(res: RunTimeline | null): void {
    this.timeline.set(res ?? null);
  }

  protected clearValue(): void {
    this.timeline.set(null);
  }
}
