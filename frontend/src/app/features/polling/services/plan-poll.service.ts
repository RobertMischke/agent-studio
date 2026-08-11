import { Injectable, computed, effect, inject, signal } from '@angular/core';
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
 * Drives the plan strip in the activity stream. SignalR `planUpdated` pushes
 * trigger immediate refreshes; polling remains the convergence fallback.
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
      const event = this.hub.planUpdatedEvent();
      if (event) this.refreshIfCurrent(event.jobId);
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
