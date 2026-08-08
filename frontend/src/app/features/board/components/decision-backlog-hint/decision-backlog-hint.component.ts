import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';

export interface DecisionBacklogWaiter {
  key: string;
  title: string;
  task: TaskInfo | null;
}

export interface DecisionBacklogEntry {
  task: TaskInfo;
  key: string;
  count: number;
  waiters: DecisionBacklogWaiter[];
}

@Component({
  selector: 'app-decision-backlog-hint',
  templateUrl: './decision-backlog-hint.component.html',
  styleUrl: './decision-backlog-hint.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DecisionBacklogHintComponent {
  readonly tasks = input.required<readonly TaskInfo[]>();
  readonly allTasks = input<readonly TaskInfo[]>([]);
  readonly taskClick = output<TaskInfo>();
  readonly expandedKey = signal<string | null>(null);

  readonly entries = computed<DecisionBacklogEntry[]>(() => {
    const tasksByKey = new Map<string, TaskInfo>();
    for (const task of [...this.allTasks(), ...this.tasks()]) {
      for (const candidate of [task.key, task.displayKey, task.id]) {
        if (candidate) tasksByKey.set(candidate.trim().toUpperCase(), task);
      }
    }

    return this.tasks()
      .filter((task) => (task.transitiveWaiters?.count ?? 0) > 0)
      .map((task) => {
        const waiters = task.transitiveWaiters!.keys.map((key) => {
          const waitingTask = tasksByKey.get(key.trim().toUpperCase()) ?? null;
          return {
            key,
            title: waitingTask?.title?.trim() || 'Title unavailable',
            task: waitingTask,
          };
        });
        return {
          task,
          key: task.key || task.displayKey || task.id,
          count: waiters.length,
          waiters,
        };
      })
      .filter((entry) => entry.count > 0)
      .sort((a, b) => b.count - a.count || a.key.localeCompare(b.key));
  });

  toggle(entry: DecisionBacklogEntry): void {
    this.expandedKey.update((current) => current === entry.key ? null : entry.key);
  }

  openWaitingTask(task: TaskInfo | null): void {
    if (task) this.taskClick.emit(task);
  }
}
