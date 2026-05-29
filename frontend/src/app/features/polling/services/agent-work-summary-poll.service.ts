import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type { AgentWorkSummary } from '../../../features/session-events';
import { TaskService } from '../../../services/task.service';
import { TaskBackgroundPoller } from './task-background-poller';

/**
 * Polls the per-job agent-work-summary endpoint at a slow cadence (10 s).
 * The summary is folded from append-only JSONL logs that only flip on
 * CLI start / continue / recovery and on tool started / completed - a
 * shorter cadence would not catch more state, just burn requests. Drives
 * the Overview tab's Agent Work block.
 */
@Injectable()
export class AgentWorkSummaryPollService extends TaskBackgroundPoller<AgentWorkSummary | null> {
  private readonly jobService = inject(TaskService);

  protected readonly intervalMs = 10_000;

  readonly summary = signal<AgentWorkSummary | null>(null);

  readonly hasAnyWork = computed(() => {
    const s = this.summary();
    return s != null && (s.calls > 0 || s.toolCalls > 0);
  });

  protected fetch(jobId: string, watchPath: string): Observable<AgentWorkSummary | null> {
    return this.jobService.getAgentWorkSummary(jobId, watchPath);
  }

  protected applyResponse(res: AgentWorkSummary | null): void {
    this.summary.set(res ?? null);
  }

  protected clearValue(): void {
    this.summary.set(null);
  }
}
