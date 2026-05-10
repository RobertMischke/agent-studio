import { Directive, OnDestroy } from '@angular/core';
import { Observable } from 'rxjs';
import type { JobInfo } from '../../../models/job.model';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';

/**
 * Cycle 9k base class for "poll one endpoint at a fixed cadence for the
 * currently-open job" services. Lifts the shared timer + job-key
 * change-detection + visibility-aware ticking out of the four similar
 * poll services that used to copy this loop. Subclasses only specify
 * what to fetch, what to do with the response, how to clear, and the
 * cadence.
 *
 * NOT a fit for `CliOutputPollService` — that one has two buffers
 * (polled + optimistic), buffer caps, dedup against echoed user lines,
 * elapsed-time ticker, and starts/stops on the runner's execution
 * status, not just on job change. It deliberately stays standalone.
 *
 * Marked `@Directive()` (not `@Injectable()`) so Angular's DI accepts
 * the abstract class as a base for `@Injectable()` subclasses without
 * synthesising a constructor for the abstract one.
 */
@Directive()
export abstract class JobBackgroundPoller<TResponse> implements OnDestroy {
  protected abstract readonly intervalMs: number;

  /** Subclass calls the relevant JobService method. */
  protected abstract fetch(jobId: string, watchPath: string): Observable<TResponse>;

  /** Subclass updates whatever signals it owns from the fresh response. */
  protected abstract applyResponse(res: TResponse): void;

  /** Subclass clears its signals back to the "no job selected" state. */
  protected abstract clearValue(): void;

  /**
   * Override to filter which jobs trigger polling. Default: any
   * non-null job. Used by `ClaudeSessionPollService` to skip
   * non-claude jobs (so the loop doesn't burn requests for jobs that
   * have nothing to report).
   */
  protected shouldPoll(_info: JobInfo): boolean {
    return true;
  }

  private timer: VisibleIntervalHandle | null = null;
  private currentJob: { id: string; watchPath: string } | null = null;
  private currentKey = '';

  /**
   * Sync polling state to a job. Pass `null` (or a job that fails
   * `shouldPoll`) to stop and clear. Re-arms the timer when the
   * effective key changes; a no-op when the same job is passed again.
   */
  syncTo(info: JobInfo | null | undefined): void {
    const willPoll = info != null && this.shouldPoll(info);
    const key = willPoll ? `${info!.watchPath}::${info!.id}` : '';
    if (key === this.currentKey) return;
    this.currentKey = key;
    this.stop();
    if (!willPoll) {
      this.currentJob = null;
      this.clearValue();
      return;
    }
    this.currentJob = { id: info!.id, watchPath: info!.watchPath };
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), this.intervalMs);
  }

  /**
   * Force a fetch outside the timer (e.g. immediately after a user
   * action that should reflect in the data on the next render). Safe
   * to call when no job is set; clears the value.
   */
  refresh(): void {
    const job = this.currentJob;
    if (!job) {
      this.clearValue();
      return;
    }
    this.fetch(job.id, job.watchPath).subscribe({
      next: (res) => this.applyResponse(res),
      error: () => { /* non-fatal: keep previous snapshot */ },
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
