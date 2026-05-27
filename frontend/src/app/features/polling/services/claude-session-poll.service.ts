import { Injectable, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type { JobInfo } from '../../../models/task.model';
import type {
  ClaudeRateLimitSnapshot,
  ClaudeSessionInfo,
  ClaudeSessionResponse,
} from '../../../features/claude';
import { JobService } from '../../../services/task.service';
import { JobBackgroundPoller } from './task-background-poller';

/**
 * Polls live Claude session telemetry every 5 s for the currently-open
 * job. Two sources are merged on the backend: the CLI's JSONL log
 * (per-turn token usage) and the live process's last `rate_limit_event`
 * frame (per-turn quota window). Polling continues even when no
 * sessionId has been captured yet, since the rate-limit signal is
 * available from the CLI's first turn onward.
 *
 * Skips polling for non-claude jobs (other CLIs report nothing here).
 */
@Injectable()
export class ClaudeSessionPollService extends JobBackgroundPoller<ClaudeSessionResponse | null> {
  private readonly jobService = inject(JobService);

  protected readonly intervalMs = 5_000;

  readonly session = signal<ClaudeSessionInfo | null>(null);
  readonly rateLimit = signal<ClaudeRateLimitSnapshot | null>(null);

  protected override shouldPoll(info: JobInfo): boolean {
    return info.cliType === 'claude';
  }

  protected fetch(jobId: string, watchPath: string): Observable<ClaudeSessionResponse | null> {
    return this.jobService.getClaudeSessionInfo(jobId, watchPath);
  }

  protected applyResponse(res: ClaudeSessionResponse | null): void {
    this.session.set(res?.sessionInfo ?? null);
    this.rateLimit.set(res?.rateLimit ?? null);
  }

  protected clearValue(): void {
    this.session.set(null);
    this.rateLimit.set(null);
  }
}
