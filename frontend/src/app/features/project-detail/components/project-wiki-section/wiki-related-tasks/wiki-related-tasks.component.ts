import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { RelatedTaskReference } from '../../../../../models/project-docs.model';
import { TaskService } from '../../../../../services/task.service';
import {
  TaskReferenceMicrocardComponent,
  TaskReferenceStatus,
} from '../../../../../components/task-reference-microcard/task-reference-microcard';

/**
 * Wiki -> task reverse cross-reference (AGT-2053). Renders a wiki page's stored
 * `relatedTasks` as the very same AGT-2050 task-reference micro-cards used inline
 * in prose, so a page and a task view speak one visual language for a reference.
 *
 * Each stored key is hydrated to its live-or-ghost projection through the shared
 * `POST /api/tasks/reference-status` batch. The association is deliberately
 * deletion-tolerant (never pruned): a stored reference the registry no longer
 * resolves - the batch drops unknown-project keys and returns a ghost for a
 * known-project-but-deleted task - still renders, as a ghost card carrying the
 * persisted title, so "this bond existed; the target is gone" stays legible.
 * Navigation is owned by the micro-card itself (TaskReferenceNavigationService),
 * with ghosts inert because their `taskKey` is null.
 */
@Component({
  selector: 'app-wiki-related-tasks',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [TaskReferenceMicrocardComponent],
  templateUrl: './wiki-related-tasks.component.html',
  styleUrl: './wiki-related-tasks.component.scss',
})
export class WikiRelatedTasksComponent {
  private readonly tasks = inject(TaskService);

  readonly related = input<RelatedTaskReference[]>([]);

  /** Live-or-ghost projections keyed by uppercased task key, from the batch endpoint. */
  private readonly resolved = signal<Map<string, TaskReferenceStatus>>(new Map());

  constructor() {
    effect((onCleanup) => {
      const keys = [
        ...new Set(this.related().map((r) => r.key.trim().toUpperCase()).filter((k) => k.length > 0)),
      ];
      if (keys.length === 0) {
        this.resolved.set(new Map());
        return;
      }
      const sub = this.tasks.getReferenceStatuses(keys).subscribe({
        next: (items) => {
          const map = new Map<string, TaskReferenceStatus>();
          for (const item of items) map.set(item.key.toUpperCase(), item);
          this.resolved.set(map);
        },
        error: () => this.resolved.set(new Map()),
      });
      onCleanup(() => sub.unsubscribe());
    });
  }

  /**
   * One micro-card status per stored reference, in stored order. A key the batch
   * dropped (or returned as a ghost) renders from {@link ghostStatus} so every
   * persisted link survives its target's deletion.
   */
  readonly statuses = computed<TaskReferenceStatus[]>(() => {
    const resolved = this.resolved();
    return this.related().map((ref) => resolved.get(ref.key.trim().toUpperCase()) ?? ghostStatus(ref));
  });
}

/** Synthesize the ghost projection the micro-card renders for a lost target. */
function ghostStatus(ref: RelatedTaskReference): TaskReferenceStatus {
  return {
    key: ref.key,
    exists: false,
    taskKey: null,
    title: ref.title || null,
    lane: null,
    projectId: '',
    projectName: '',
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}
