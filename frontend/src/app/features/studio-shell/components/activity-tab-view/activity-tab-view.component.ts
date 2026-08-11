import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { TaskStatusCardComponent } from '../../../../components/task-status-card';
import type { TaskInfo } from '../../../../models/task.model';

/**
 * Full-screen "Activity" tab. Looks up the owning job by taskKey and
 * renders the live execution + run-outcome summary, plus a CTA back to
 * the in-task chat view (which still owns the streaming protocol
 * pane). The inline activity log streaming is a follow-up.
 *
 * The fact list at the top is rendered by the shared
 * `<app-task-status-card>` so the same surface is reused across the
 * open-tabs hover popover, the activity tab, and any future task
 * info-modal.
 */
@Component({
  selector: 'app-studio-activity-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TaskStatusCardComponent],
  templateUrl: './activity-tab-view.component.html',
  styleUrl: './activity-tab-view.component.scss',
})
export class StudioActivityViewComponent {
  private readonly jobService = inject(TaskService);

  readonly taskKey = input.required<string>();

  readonly job = computed<TaskInfo | null>(() => {
    const key = this.taskKey();
    return this.jobService.jobs().find(j => j.taskKey === key) ?? null;
  });
}
