import { Injectable, computed, effect, inject, signal, untracked } from '@angular/core';
import { Observable } from 'rxjs';
import type { TaskPlanView } from '../../plan-strip/plan.model';
import { TaskService } from '../../../services/task.service';
import { TaskBackgroundPoller } from './task-background-poller';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';

/**
 * Polls the per-job plan endpoint at a 5 s cadence. The view is folded
 * from append-only telemetry (plan-snapshots.jsonl + tool-calls.jsonl)
 * that only changes when the agent re-emits its TodoWrite / update_plan
 * plan or fires a tool, so a tighter cadence would just burn requests.
 * SignalR is the primary plan-snapshot path; the interval remains a bounded
 * convergence fallback after a disconnect.
 */
@Injectable()
export class PlanPollService extends TaskBackgroundPoller<TaskPlanView | null> {
  private readonly jobService = inject(TaskService);
  private readonly hub = inject(JobsHubClient);

  protected readonly intervalMs = 5_000;

  readonly plan = signal<TaskPlanView | null>(null);

  /** True once the agent has emitted at least one plan frame for this job. */
  readonly hasPlan = computed(() => this.plan()?.hasPlan === true);

  constructor() {
    super();
    effect(() => {
      const update = this.hub.planUpdated();
      if (!update || !this.isCurrentJob(update.jobId)) return;
      untracked(() => this.refresh());
    });
  }

  protected fetch(jobId: string, watchPath: string): Observable<TaskPlanView | null> {
    return this.jobService.getPlan(jobId, watchPath);
  }

  protected applyResponse(res: TaskPlanView | null): void {
    this.plan.set(res ?? null);
  }

  protected clearValue(): void {
    this.plan.set(null);
  }
}
