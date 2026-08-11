import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { PlanStripComponent, type TaskPlanView } from '../../../plan-strip';

/** Live, read-only plan snapshot for the task currently in orchestrator scope. */
@Component({
  selector: 'app-orchestrator-task-progress',
  standalone: true,
  imports: [PlanStripComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-task-progress.component.html',
  styleUrl: './orchestrator-task-progress.component.scss',
})
export class OrchestratorTaskProgressComponent {
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly runActive = input(false);
  readonly plan = signal<TaskPlanView | null>(null);

  private readonly tasks = inject(TaskService);
  private readonly hub = inject(JobsHubClient);
  private readonly refreshKey = computed(() => {
    const jobId = this.jobId();
    const update = this.hub.planUpdatedEvent();
    const sequence = update?.jobId === jobId ? update.sequence : 0;
    return `${jobId ?? ''}::${this.watchPath() ?? ''}::${sequence}`;
  });

  constructor() {
    effect((onCleanup) => {
      this.refreshKey();
      const jobId = this.jobId();
      const watchPath = this.watchPath();
      if (!jobId || !watchPath) {
        this.plan.set(null);
        return;
      }

      const subscription = this.tasks.getPlan(jobId, watchPath).subscribe({
        next: plan => this.plan.set(plan?.hasPlan ? plan : null),
        error: () => this.plan.set(null),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
}
