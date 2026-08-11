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
import { TaskState } from '../../../../models/task.model';
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
  readonly liveConnected = input.required<boolean>();
  readonly connectionStateChangedAtUtc = input.required<string>();
  readonly lastUpdatedAtUtc = input<string | null>(null);
  readonly decisionChanged = output<void>();
  readonly draftDiscarded = output<void>();
  readonly manualRefresh = output<void>();

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
  readonly implementationStatus = computed(() => {
    return implementationStatusFor(
      this.document().workbench.status,
      this.taskStatuses(),
    );
  });
  readonly statusLabel = computed(() => {
    const workbench = this.document().workbench;
    return [
      this.implementationStatus() ?? humanize(workbench.status),
      workbench.phase ? humanize(workbench.phase) : null,
    ]
      .filter(Boolean)
      .join(' · ');
  });
  readonly connectionSinceLabel = computed(() =>
    `${this.liveConnected() ? 'Connected' : 'Disconnected'} since ${formatStatusDateTime(this.connectionStateChangedAtUtc())}`,
  );
  readonly lastUpdateLabel = computed(() => formatStatusDateTime(this.lastUpdatedAtUtc()));
  readonly lastUpdateTimeLabel = computed(() => formatStatusTime(this.lastUpdatedAtUtc()));

  constructor() {
    effect((onCleanup) => {
      const workbench = this.document().workbench;
      const projectName = this.projectName();
      const fallbackKeys = uniqueKeys([
        ...workbench.sourceTaskKeys,
        ...(workbench.relatedTaskKeys ?? []),
      ]);
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

  refreshView(disclosure: HTMLDetailsElement): void {
    disclosure.open = false;
    this.manualRefresh.emit();
  }
}

function parseStatusTime(value: string | null): Date | null {
  if (!value) return null;
  const timestamp = new Date(value);
  return Number.isNaN(timestamp.getTime()) ? null : timestamp;
}

function formatStatusDateTime(value: string | null): string {
  const timestamp = parseStatusTime(value);
  if (!timestamp) return 'not recorded';
  return new Intl.DateTimeFormat('en-GB', {
    day: '2-digit',
    month: 'short',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  }).format(timestamp);
}

function formatStatusTime(value: string | null): string {
  const timestamp = parseStatusTime(value);
  if (!timestamp) return 'not recorded';
  return new Intl.DateTimeFormat('en-GB', {
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(timestamp);
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

export function implementationStatusFor(
  workbenchStatus: string,
  references: readonly TaskReferenceStatus[],
): string | null {
  if (workbenchStatus !== 'decision-pending' && workbenchStatus !== 'decided') return null;
  const known = references.filter((task) => task.exists && task.lane);
  if (known.length === 0) return null;
  const allTerminal = references.every(
    (task) => task.exists && task.lane !== null && isTerminalLane(task.lane),
  );
  return !allTerminal && known.some((task) => hasImplementationStarted(task.lane))
    ? 'In implementation'
    : null;
}

function isTerminalLane(lane: string | null): boolean {
  return lane === TaskState.Completed || lane === TaskState.Archive;
}

function hasImplementationStarted(lane: string | null): boolean {
  return lane !== null
    && lane !== TaskState.Backlog
    && lane !== TaskState.Preparation
    && lane !== TaskState.Ready;
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
