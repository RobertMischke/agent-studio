import { Injectable, computed, inject } from '@angular/core';
import type { MarkdownTaskReference } from '../components/markdown-utils';
import type { TaskInfo } from '../models/task.model';
import { TaskService } from './task.service';
import { TaskSelectionService } from '../features/task-detail';
import { StudioTabStateService } from '../features/studio-shell';

@Injectable({ providedIn: 'root' })
export class TaskReferenceNavigationService {
  private readonly tasks = inject(TaskService);
  private readonly selection = inject(TaskSelectionService);
  private readonly tabs = inject(StudioTabStateService);

  private readonly jobsByTaskKey = computed(() => {
    const map = new Map<string, TaskInfo>();
    for (const job of this.tasks.jobs()) {
      map.set(job.taskKey, job);
    }
    return map;
  });

  readonly markdownReferences = computed<readonly MarkdownTaskReference[]>(() => {
    const references: MarkdownTaskReference[] = [];
    for (const job of this.tasks.jobs()) {
      const labels = new Set<string>();
      if (job.key) labels.add(job.key);
      if (job.id) labels.add(job.id);
      const taskKeyTail = job.taskKey.includes('::')
        ? job.taskKey.slice(job.taskKey.lastIndexOf('::') + 2)
        : job.taskKey;
      if (taskKeyTail) labels.add(taskKeyTail);
      for (const label of labels) {
        references.push({ label, taskKey: job.taskKey });
      }
    }
    return references;
  });

  openTaskKey(taskKey: string | null | undefined): boolean {
    if (!taskKey) return false;
    const job = this.jobsByTaskKey().get(taskKey);
    if (!job) return false;
    this.tabs.open({ kind: 'task', taskKey: job.taskKey });
    this.selection.openDetail(job);
    return true;
  }
}
