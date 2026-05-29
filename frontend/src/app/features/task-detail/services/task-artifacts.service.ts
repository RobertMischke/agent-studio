import { Injectable, inject, signal } from '@angular/core';
import { TaskService } from '../../../services/task.service';
import type { TaskArtifact, TaskInfo } from '../../../models/task.model';

/**
 * Per-detail Files-tab manifest. Refreshes when the open job changes
 * and exposes a manual {@link reload} call so a successful file save
 * can pick up the new size + mtime without a page reload. Provided
 * locally on `TaskDetailComponent` (no global state).
 */
@Injectable()
export class TaskArtifactsService {
  private readonly jobs = inject(TaskService);
  private currentKey: string | null = null;

  readonly artifacts = signal<TaskArtifact[]>([]);

  /** Called from a detail-component effect whenever the open job changes. */
  syncTo(info: TaskInfo | null): void {
    if (!info) {
      this.currentKey = null;
      this.artifacts.set([]);
      return;
    }
    if (this.currentKey === info.jobKey) return;
    this.currentKey = info.jobKey;
    this.fetch(info);
  }

  /** Forces a re-fetch against the currently tracked job. No-op when unbound. */
  reload(info: TaskInfo | null): void {
    if (!info) return;
    this.fetch(info);
  }

  private fetch(info: TaskInfo): void {
    this.jobs.listJobArtifacts(info.id, info.watchPath).subscribe({
      next: (resp) => this.artifacts.set(resp?.files ?? []),
      error: () => this.artifacts.set([]),
    });
  }
}
