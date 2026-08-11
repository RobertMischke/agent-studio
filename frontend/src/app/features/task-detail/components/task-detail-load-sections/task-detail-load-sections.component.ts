import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { laneLabelFor } from '../../state/triage-actions.model';

interface LoadingSection {
  id: 'context' | 'activity' | 'evidence';
  label: string;
}

@Component({
  selector: 'app-task-detail-load-sections',
  standalone: true,
  templateUrl: './task-detail-load-sections.component.html',
  styleUrl: './task-detail-load-sections.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskDetailLoadSectionsComponent {
  readonly info = input.required<TaskInfo>();
  readonly errorMessage = input<string | null>(null);
  readonly back = output<void>();
  readonly retry = output<void>();
  readonly laneLabel = laneLabelFor;
  readonly sections: readonly LoadingSection[] = [
    { id: 'context', label: 'Task context' },
    { id: 'activity', label: 'Activity and result' },
    { id: 'evidence', label: 'Git evidence' },
  ];
}
