import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';

export interface DecisionBacklogEntry {
  task: TaskInfo;
  key: string;
  count: number;
  waitingKeys: string[];
}

@Component({
  selector: 'app-decision-backlog-hint',
  templateUrl: './decision-backlog-hint.component.html',
  styleUrl: './decision-backlog-hint.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DecisionBacklogHintComponent {
  readonly tasks = input.required<readonly TaskInfo[]>();
  readonly taskClick = output<TaskInfo>();

  readonly entries = computed<DecisionBacklogEntry[]>(() =>
    this.tasks()
      .filter((task) => (task.transitiveWaiters?.count ?? 0) > 0)
      .map((task) => ({
        task,
        key: task.key || task.displayKey || task.id,
        count: task.transitiveWaiters!.count,
        waitingKeys: task.transitiveWaiters!.keys,
      }))
      .sort((a, b) => b.count - a.count || a.key.localeCompare(b.key)),
  );
}
