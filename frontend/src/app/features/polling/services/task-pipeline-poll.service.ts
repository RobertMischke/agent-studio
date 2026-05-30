import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type { TaskPipelineResponse } from '../../task-pipeline';
import { TaskService } from '../../../services/task.service';
import { TaskBackgroundPoller } from './task-background-poller';

/**
 * Polls `/api/tasks/{id}/pipeline` every 10 s while a job is open.
 * Drives the Overview tab's pipeline block: the ordered pre/core/post
 * steps, their recorded status, per-step tokens + cost, and the task
 * total. 10 s matches the other Overview pollers (agent-work,
 * task-timeline) — pipeline steps land in coarse bursts at the end of a
 * run, not per frame, so a tighter cadence would just burn requests.
 */
@Injectable()
export class TaskPipelinePollService extends TaskBackgroundPoller<TaskPipelineResponse | null> {
  private readonly jobService = inject(TaskService);

  protected readonly intervalMs = 10_000;

  readonly pipeline = signal<TaskPipelineResponse | null>(null);

  /** True once at least one step execution has been recorded. */
  readonly hasExecution = computed(() => {
    const steps = this.pipeline()?.execution?.steps;
    return steps != null && steps.length > 0;
  });

  protected fetch(jobId: string, watchPath: string): Observable<TaskPipelineResponse | null> {
    return this.jobService.getJobPipeline(jobId, watchPath);
  }

  protected applyResponse(res: TaskPipelineResponse | null): void {
    this.pipeline.set(res ?? null);
  }

  protected clearValue(): void {
    this.pipeline.set(null);
  }
}
