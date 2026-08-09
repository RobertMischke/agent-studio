import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { catchError, map, of, switchMap } from 'rxjs';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import {
  TaskReferenceMicrocardComponent,
  TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import {
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
} from '../../../../models/project-docs.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';

const MAX_INLINE_TASKS = 8;

@Component({
  selector: 'app-workbench-viewer-header',
  standalone: true,
  imports: [
    AppTooltipDirective,
    CopyableTaskKeyComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchDecisionPanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-viewer-header.component.html',
  styleUrl: './workbench-viewer-header.component.scss',
})
export class WorkbenchViewerHeaderComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly decisionPoints = input<readonly WorkbenchDecisionPoint[]>([]);
  readonly responses = input<readonly WorkbenchDecisionResponse[]>([]);
  readonly openWiki = output<void>();
  readonly decisionChanged = output<void>();

  private readonly docs = inject(ProjectDocsService);
  private readonly tasks = inject(TaskService);

  readonly referenceKeys = signal<string[]>([]);
  readonly taskStatuses = signal<TaskReferenceStatus[]>([]);
  readonly taskStatusesLoading = signal(false);

  readonly inlineTaskStatuses = computed(() => this.taskStatuses().slice(0, MAX_INLINE_TASKS));
  readonly hiddenTaskCount = computed(() =>
    Math.max(0, this.taskStatuses().length - MAX_INLINE_TASKS),
  );
  readonly openDecisionCount = computed(() => {
    const workbench = this.document().workbench;
    if (
      workbench.decision?.state === 'succeeded' ||
      workbench.status === 'decided' ||
      workbench.status === 'archived'
    )
      return 0;
    const answered = new Map(this.responses().map((response) => [response.decisionId, response]));
    return this.decisionPoints().filter(
      (point) => (answered.get(point.id)?.selectedOptionIds.length ?? 0) === 0,
    ).length;
  });
  readonly statusLabel = computed(() => {
    const workbench = this.document().workbench;
    return [humanize(workbench.status), workbench.phase ? humanize(workbench.phase) : null]
      .filter(Boolean)
      .join(' · ');
  });

  constructor() {
    effect((onCleanup) => {
      const workbench = this.document().workbench;
      const projectName = this.projectName();
      const fallbackKeys = uniqueKeys(workbench.relatedTaskKeys ?? []);
      const workbenchKey = normalizeKey(workbench.key);
      this.referenceKeys.set([]);
      this.taskStatuses.set([]);
      this.taskStatusesLoading.set(true);

      const keys$ = workbenchKey
        ? this.docs.getWorkbenchReferences(projectName, workbenchKey).pipe(
            map((references) =>
              uniqueKeys([
                ...references.items.flatMap((item) => (item.sourceKey ? [item.sourceKey] : [])),
                ...references.legacyTaskKeys,
                ...fallbackKeys,
              ]),
            ),
            catchError(() => of(fallbackKeys)),
          )
        : of(fallbackKeys);

      const subscription = keys$
        .pipe(
          switchMap((keys) => {
            this.referenceKeys.set(keys);
            if (keys.length === 0) return of([] as TaskReferenceStatus[]);
            return this.tasks.getReferenceStatuses(keys).pipe(
              map((statuses) => {
                const byKey = new Map(statuses.map((status) => [normalizeKey(status.key), status]));
                return keys.map(
                  (key) => byKey.get(normalizeKey(key)) ?? ghostStatus(key, projectName),
                );
              }),
              catchError(() => of(keys.map((key) => ghostStatus(key, projectName)))),
            );
          }),
        )
        .subscribe({
          next: (statuses) => {
            this.taskStatuses.set(statuses);
            this.taskStatusesLoading.set(false);
          },
        });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  closeDetails(disclosure: HTMLDetailsElement): void {
    disclosure.open = false;
  }
}

function uniqueKeys(keys: readonly string[]): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const key of keys) {
    const normalized = normalizeKey(key);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    result.push(key.trim());
  }
  return result;
}

function normalizeKey(value: string | null | undefined): string {
  return (value ?? '').trim().toUpperCase();
}

function humanize(value: string): string {
  const words = value.replaceAll('-', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}

function ghostStatus(key: string, projectName: string): TaskReferenceStatus {
  return {
    key,
    exists: false,
    taskKey: null,
    title: null,
    lane: null,
    projectId: '',
    projectName,
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}
