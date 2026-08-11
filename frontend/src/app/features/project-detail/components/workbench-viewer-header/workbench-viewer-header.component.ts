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
import { catchError, finalize, map, of, switchMap, tap } from 'rxjs';
import { PendingButtonDirective } from '../../../../components/async-feedback';
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
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { NotificationService } from '../../../../services/notification.service';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchDecisionPanelComponent } from '../workbench-decision-panel/workbench-decision-panel';

const MAX_INLINE_TASKS = 8;
const IMPLEMENTATION_LOG_START = '<!-- agent-studio:implementation-log:start -->';
const IMPLEMENTATION_LOG_END = '<!-- agent-studio:implementation-log:end -->';

@Component({
  selector: 'app-workbench-viewer-header',
  standalone: true,
  imports: [
    AppTooltipDirective,
    CopyableTaskKeyComponent,
    PendingButtonDirective,
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
  readonly showWikiAction = input(true);
  readonly openWiki = output<void>();
  readonly decisionChanged = output<void>();

  private readonly docs = inject(ProjectDocsService);
  private readonly tasks = inject(TaskService);
  private readonly confirmDialog = inject(ConfirmDialogService);
  private readonly notifications = inject(NotificationService);

  readonly referenceKeys = signal<string[]>([]);
  readonly taskStatuses = signal<TaskReferenceStatus[]>([]);
  readonly taskStatusesLoading = signal(false);
  readonly refreshPending = signal(false);
  private readonly referenceReload = signal(0);

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
      dossierHasImplementationEntry(this.document().html),
    );
  });
  readonly statusLabel = computed(() => {
    const workbench = this.document().workbench;
    return [humanize(workbench.status), workbench.phase ? humanize(workbench.phase) : null]
      .filter(Boolean)
      .join(' · ');
  });

  constructor() {
    effect((onCleanup) => {
      this.referenceReload();
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

  requestRefreshCard(): void {
    const projectName = this.projectName();
    const workbench = this.document().workbench;
    const workbenchKey = workbench.key?.trim();
    if (!workbenchKey || this.refreshPending()) return;

    void this.confirmDialog.confirm({
      title: 'Create Dossier refresh card?',
      message: 'Create a preparation card with the Dossier source and refresh goal prefilled.',
      detail: `Refresh: ${workbench.title} · ${workbenchKey} · ${workbench.entryPath}`,
      confirmLabel: 'Create card',
      kind: 'primary',
    }).then((confirmed) => {
      const current = this.document().workbench;
      if (
        !confirmed
        || current.id !== workbench.id
        || this.projectName() !== projectName
        || this.refreshPending()
      ) return;
      this.createRefreshCard(projectName, workbenchKey);
    });
  }

  private createRefreshCard(projectName: string, workbenchKey: string): void {
    const workbench = this.document().workbench;
    this.refreshPending.set(true);
    this.tasks.getWatchPaths().pipe(
      map((entries) => {
        const path = entries.find((entry) => entry.name === projectName)?.path;
        if (!path) throw new Error(`Could not resolve the task path for ${projectName}.`);
        return path;
      }),
      switchMap((watchPath) => this.tasks.createJob({
        title: `Refresh: ${workbench.title}`,
        agent: 'claude',
        watchPath,
        promptMarkdown: dossierRefreshPrompt(this.document(), workbenchKey),
        targetState: TaskState.Preparation,
        taskType: 'chore',
        mode: 'coding',
      }).pipe(map((created) => ({ created, watchPath })))),
      switchMap(({ created, watchPath }) => this.tasks.setTaskReferences(created.id, {
        dependsOn: [],
        relatedTo: [],
        blockedBy: [],
        supersedes: [],
        workbenches: [workbenchKey],
      }, watchPath)),
      tap(() => this.tasks.refresh()),
      finalize(() => this.refreshPending.set(false)),
    ).subscribe({
      next: () => {
        this.notifications.success(
          `Refresh: ${workbench.title} is linked to this Dossier.`,
          'Refresh card created',
        );
        this.referenceReload.update((value) => value + 1);
      },
      error: (error) => this.notifications.error(
        refreshErrorMessage(error),
        'Could not create refresh card',
      ),
    });
  }
}

function dossierRefreshPrompt(document: WorkbenchDocument, workbenchKey: string): string {
  return [
    '# Dossier refresh',
    '',
    `Dossier path: \`${document.workbench.entryPath}\``,
    `Dossier key: \`${workbenchKey}\``,
    '',
    '## Goal',
    '',
    'Update the document against reality (incorporate findings, mark completed sections, refresh figures).',
    '',
    '## Constraint',
    '',
    'Update the repository document explicitly. Do not add automatic document self-modification.',
  ].join('\n');
}

function refreshErrorMessage(error: unknown): string {
  const candidate = error as { error?: { error?: string } | string; message?: string } | null;
  if (typeof candidate?.error === 'string') return candidate.error;
  if (candidate?.error && typeof candidate.error.error === 'string') return candidate.error.error;
  return candidate?.message || 'The Dossier refresh card could not be created and linked.';
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

export function dossierHasImplementationEntry(html: string): boolean {
  const logStart = html.indexOf(IMPLEMENTATION_LOG_START);
  const logEnd = html.indexOf(IMPLEMENTATION_LOG_END, logStart + IMPLEMENTATION_LOG_START.length);
  if (logStart < 0 || logEnd <= logStart) return false;
  const log = html.slice(logStart + IMPLEMENTATION_LOG_START.length, logEnd);
  return /<li\b[^>]*\bdata-implementation-entry\s*=/i.test(log);
}

export function implementationStatusFor(
  workbenchStatus: string,
  references: readonly TaskReferenceStatus[],
  hasImplementationEntry = false,
): string | null {
  if (workbenchStatus !== 'decision-pending' && workbenchStatus !== 'decided') return null;
  const known = references.filter((task) => task.exists && task.lane);
  if (known.length === 0) return null;
  const allTerminal = references.every(
    (task) => task.exists && task.lane !== null && isTerminalLane(task.lane),
  );
  const activeImplementation = known.some(
    (task) => !isTerminalLane(task.lane) && hasImplementationStarted(task.lane),
  );
  return !allTerminal && (activeImplementation || hasImplementationEntry)
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
