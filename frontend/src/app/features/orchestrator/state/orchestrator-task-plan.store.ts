import { Injectable, OnDestroy, effect, inject, signal, untracked } from '@angular/core';
import type { Subscription } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import { JobsHubClient } from '../../../services/jobs-hub-client.service';
import type { TaskPlanView } from '../../plan-strip';

interface TaskPlanSelection {
  jobId: string;
  watchPath: string;
}

/** Live native-agent plan for the task context currently shown in Chat. */
@Injectable()
export class OrchestratorTaskPlanStore implements OnDestroy {
  private readonly tasks = inject(TaskService);
  private readonly hub = inject(JobsHubClient);
  private readonly selection = signal<TaskPlanSelection | null>(null);
  private request: Subscription | null = null;
  private loadedKey = '';
  private seenRevision = 0;

  readonly plan = signal<TaskPlanView | null>(null);

  constructor() {
    effect(() => {
      const selected = this.selection();
      const pushed = this.hub.planUpdated();
      untracked(() => {
        const key = selected ? `${selected.watchPath}::${selected.jobId}` : '';
        const selectionChanged = key !== this.loadedKey;
        const matchingPush = !!selected
          && !!pushed
          && pushed.revision !== this.seenRevision
          && pushed.jobId === selected.jobId;
        this.seenRevision = pushed?.revision ?? this.seenRevision;
        if (!selectionChanged && !matchingPush) return;
        this.loadedKey = key;
        this.load(selected);
      });
    });
  }

  select(jobId: string | null, watchPath: string | null): void {
    const next = jobId && watchPath ? { jobId, watchPath } : null;
    const current = this.selection();
    if (current?.jobId === next?.jobId && current?.watchPath === next?.watchPath) return;
    this.selection.set(next);
  }

  private load(selected: TaskPlanSelection | null): void {
    this.request?.unsubscribe();
    this.request = null;
    if (!selected) {
      this.plan.set(null);
      return;
    }
    this.request = this.tasks.getPlan(selected.jobId, selected.watchPath).subscribe({
      next: plan => this.plan.set(plan?.hasPlan ? plan : null),
      error: () => this.plan.set(null),
    });
  }

  ngOnDestroy(): void {
    this.request?.unsubscribe();
  }
}
