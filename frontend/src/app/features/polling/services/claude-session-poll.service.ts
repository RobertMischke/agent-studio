import { Injectable, OnDestroy, signal } from '@angular/core';
import { ClaudeRateLimitSnapshot, ClaudeSessionInfo, JobInfo } from '../../../models/job.model';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';

/**
 * Polls live Claude session telemetry every 5 s for the currently-open
 * job. Maintained as a local service on JobDetailComponent so each
 * detail instance has its own cadence and signals.
 *
 * Two sources are merged on the backend: the CLI's JSONL log (per-turn
 * token usage) and the live process's last `rate_limit_event` frame
 * (per-turn quota window). Polling continues even when no sessionId has
 * been captured yet, since the rate-limit signal is available from the
 * CLI's first turn onward.
 */
@Injectable()
export class ClaudeSessionPollService implements OnDestroy {
  readonly session = signal<ClaudeSessionInfo | null>(null);
  readonly rateLimit = signal<ClaudeRateLimitSnapshot | null>(null);

  private timer: VisibleIntervalHandle | null = null;
  private currentJob: { id: string; watchPath: string } | null = null;
  /** Tracks the currently-polled job so we can re-arm when it changes. */
  private currentKey = '';

  constructor(private jobService: JobService) {}

  /**
   * Sync polling state to a job. Pass `null` (or a non-Claude job) to
   * stop. Re-arms the 5 s timer when the job changes.
   */
  syncTo(info: JobInfo | null | undefined): void {
    const key = info && info.cliType === 'claude' ? info.id : '';
    if (key === this.currentKey) return;
    this.currentKey = key;
    this.stop();
    if (!info || info.cliType !== 'claude') {
      this.session.set(null);
      this.rateLimit.set(null);
      return;
    }
    this.currentJob = { id: info.id, watchPath: info.watchPath };
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), 5_000);
  }

  refresh(): void {
    const job = this.currentJob;
    if (!job) {
      this.session.set(null);
      this.rateLimit.set(null);
      return;
    }
    this.jobService.getClaudeSessionInfo(job.id, job.watchPath).subscribe({
      next: (res) => {
        this.session.set(res?.sessionInfo ?? null);
        this.rateLimit.set(res?.rateLimit ?? null);
      },
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
