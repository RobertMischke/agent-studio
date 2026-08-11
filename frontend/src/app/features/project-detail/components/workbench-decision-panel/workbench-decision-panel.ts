import { DatePipe } from '@angular/common';
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
import { EMPTY, Observable, catchError, map, of, switchMap, tap } from 'rxjs';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import { CopyableTaskKeyComponent } from '../../../../components/copyable-task-key/copyable-task-key.component';
import {
  TaskReferenceMicrocardComponent,
  TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import {
  ConfirmWorkbenchDecisionRequest,
  PrepareWorkbenchDecisionRequest,
  WorkbenchDecisionPoint,
  WorkbenchDecisionProjection,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
  WorkbenchTaskDraft,
} from '../../../../models/project-docs.model';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchDecisionStore } from '../../state/workbench-decision.store';

type DraftMode = 'feature-spawn' | 'archive' | null;

interface CreatedCard {
  key: string;
  taskKey: string;
  title: string;
  lane: string;
}

@Component({
  selector: 'app-workbench-decision-panel',
  standalone: true,
  imports: [
    CopyableTaskKeyComponent,
    DatePipe,
    PendingButtonDirective,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-decision-panel.html',
  styleUrl: './workbench-decision-panel.scss',
})
export class WorkbenchDecisionPanelComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly decisionPoints = input<readonly WorkbenchDecisionPoint[]>([]);
  readonly responses = input<readonly WorkbenchDecisionResponse[]>([]);
  readonly showWikiAction = input(true);
  readonly decisionChanged = output<void>();
  readonly openWiki = output<void>();

  private readonly store = inject(WorkbenchDecisionStore);
  private readonly tasks = inject(TaskService);

  readonly actor = signal('Operator');
  readonly draftMode = signal<DraftMode>(null);
  readonly title = signal('');
  readonly goal = signal('');
  readonly archiveReason = signal('');
  readonly validationError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly createdCard = signal<CreatedCard | null>(null);
  readonly taskStatuses = signal<TaskReferenceStatus[]>([]);
  private readonly operationId = signal('');

  readonly requestState = computed(() =>
    this.store.state(this.projectName(), this.document().workbench.id));
  readonly persistedDecision = computed(() => this.document().workbench.decision ?? null);
  readonly result = computed(() => this.requestState().result);
  readonly pending = computed(() => this.requestState().pending !== null);
  readonly settled = computed(() => {
    const decision = this.persistedDecision();
    if (decision) return decision.state === 'succeeded';
    const status = this.document().workbench.status;
    return status === 'decided' || status === 'documented' || status === 'archived';
  });
  readonly gateReady = computed(() =>
    this.document().workbench.phase === 'decision-ready'
    || this.persistedDecision() !== null
    || this.settled());
  readonly mutationBlocked = computed(() =>
    this.document().workingTreeModified
    || (!this.document().revision && !this.document().fingerprint));
  readonly stage = computed(() =>
    this.document().workbench.decisionStage
    ?? this.result()?.decisionStage
    ?? null);
  readonly answersComplete = computed(() => {
    if (this.decisionPoints().length === 0) return true;
    const responses = new Map(this.responses().map(response => [response.decisionId, response]));
    return this.decisionPoints().every(point =>
      (responses.get(point.id)?.selectedOptionIds.length ?? 0) > 0);
  });
  readonly selectedSummary = computed(() => selectedDecisionText(
    this.decisionPoints(), this.responses()));
  readonly canPrepareFeature = computed(() =>
    this.gateReady() && !this.settled() && !this.mutationBlocked()
    && !this.pending() && this.answersComplete());
  readonly canConfirm = computed(() =>
    !this.settled() && !this.mutationBlocked() && this.stage() === 'prepared');

  constructor() {
    effect(() => {
      const decision = this.persistedDecision();
      if (decision?.confirmedBy || decision?.preparedBy)
        this.actor.set(decision.confirmedBy || decision.preparedBy);
    });
    effect(onCleanup => {
      const keys = this.persistedDecision()?.spawnedTaskKeys ?? [];
      if (keys.length === 0) {
        this.taskStatuses.set([]);
        return;
      }
      const subscription = this.tasks.getReferenceStatuses(keys).subscribe({
        next: statuses => this.taskStatuses.set(statuses),
        error: () => this.taskStatuses.set([]),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }

  updateActor(event: Event): void {
    this.actor.set((event.target as HTMLInputElement).value);
  }

  updateTitle(event: Event): void {
    this.title.set((event.target as HTMLInputElement).value);
  }

  updateGoal(event: Event): void {
    this.goal.set((event.target as HTMLTextAreaElement).value);
  }

  updateArchiveReason(event: Event): void {
    this.archiveReason.set((event.target as HTMLTextAreaElement).value);
  }

  prepareFeatureCard(): void {
    if (!this.canPrepareFeature()) return;
    this.seedFeatureDraft();
    this.draftMode.set('feature-spawn');
    this.prepare('feature-spawn');
  }

  beginArchive(): void {
    if (this.pending() || this.settled()) return;
    this.resetFeedback();
    this.operationId.set(createOperationId());
    this.draftMode.set('archive');
  }

  prepareArchive(): void {
    if (this.draftMode() !== 'archive' || this.pending()) return;
    this.prepare('archive');
  }

  cancelDraft(): void {
    if (this.pending()) return;
    this.draftMode.set(null);
    this.archiveReason.set('');
    this.operationId.set('');
    this.store.clear(this.projectName(), this.document().workbench.id);
    this.resetFeedback();
  }

  createFeatureCard(): void {
    if (!this.canConfirm() || this.pending()) return;
    const request = this.prepareRequest('feature-spawn');
    if (!request?.task) return;
    this.actionError.set(null);
    this.createCard(request.task).pipe(
      switchMap(card => this.confirm(request, [card.key])),
      catchError(error => {
        this.actionError.set(actionErrorMessage(error));
        return EMPTY;
      }),
    ).subscribe(() => this.decisionChanged.emit());
  }

  confirmArchive(): void {
    if (!this.canConfirm() || this.pending()) return;
    const request = this.prepareRequest('archive');
    if (!request) return;
    this.confirm(request, []).subscribe({
      next: () => this.decisionChanged.emit(),
      error: () => undefined,
    });
  }

  outcomeLabel(decision: WorkbenchDecisionProjection | null = this.persistedDecision()): string {
    return (decision?.outcome ?? this.draftMode()) === 'archive'
      ? 'Archived'
      : 'Feature decision';
  }

  stageLabel(): string {
    if (this.document().workbench.status === 'documented') return 'Documented';
    switch (this.stage()) {
      case 'prepared': return 'Ready to confirm';
      case 'pending': return 'Decision in progress';
      case 'failed': return 'Retry needed';
      case 'succeeded': return 'Decided';
      case 'archived': return 'Archived';
      default: return this.gateReady() ? 'Decision ready' : 'In progress';
    }
  }

  fallbackTaskTitle(): string {
    return this.persistedDecision()?.taskDraft?.title ?? 'Created feature card';
  }

  fallbackTaskLane(): string {
    return laneLabel(this.persistedDecision()?.taskDraft?.initialLane ?? null);
  }

  private prepare(outcome: Exclude<DraftMode, null>): void {
    const request = this.prepareRequest(outcome);
    if (!request) return;
    this.store.prepare(this.projectName(), this.document().workbench.id, request).subscribe({
      error: () => undefined,
    });
  }

  private confirm(
    prepared: PrepareWorkbenchDecisionRequest,
    spawnedTaskKeys: string[],
  ): Observable<unknown> {
    const result = this.result();
    const operationId = result?.operationId ?? prepared.operationId;
    const matches = result?.operationId === operationId;
    const request: ConfirmWorkbenchDecisionRequest = {
      ...prepared,
      operationId,
      expectedRevision: matches ? result!.revision : this.document().revision,
      expectedFingerprint: matches ? result!.fingerprint : this.document().fingerprint,
      spawnedTaskKeys,
      confirmed: true,
    };
    return this.store.confirm(this.projectName(), this.document().workbench.id, request);
  }

  private createCard(draft: WorkbenchTaskDraft): Observable<CreatedCard> {
    const alreadyCreated = this.createdCard();
    if (alreadyCreated) return of(alreadyCreated);
    return this.tasks.getWatchPaths().pipe(
      map(entries => {
        const path = entries.find(entry => entry.name === this.projectName())?.path;
        if (!path) throw new Error(`Could not resolve the task path for ${this.projectName()}.`);
        return path;
      }),
      switchMap(watchPath => this.tasks.createJob({
        title: draft.title,
        agent: 'claude',
        watchPath,
        promptMarkdown: cardPrompt(this.document(), draft, this.decisionPoints(), this.responses()),
        targetState: draft.initialLane,
        taskType: draft.taskType,
        mode: draft.mode,
      })),
      switchMap(created => this.tasks.getDetailByProject(created.id, this.projectName())),
      map(detail => ({
        key: detail.info.key || detail.info.displayKey || taskKeyTail(detail.info.taskKey),
        taskKey: detail.info.taskKey,
        title: detail.info.title,
        lane: detail.info.state,
      })),
      tap(card => {
        this.createdCard.set(card);
        this.tasks.refresh();
      }),
    );
  }

  private prepareRequest(outcome: Exclude<DraftMode, null>): PrepareWorkbenchDecisionRequest | null {
    const actor = this.actor().trim();
    if (!actor) return this.invalid('Add the decision owner.');
    if (outcome === 'archive') {
      const reason = this.archiveReason().trim();
      if (!reason) return this.invalid('Add a reason before preparing the archive decision.');
      return this.baseRequest(outcome, actor, null, reason);
    }
    if (!this.answersComplete()) return this.invalid('Answer every inline decision point first.');
    const title = this.title().trim();
    const goal = this.goal().trim();
    if (!title || !goal) return this.invalid('Title and goal are required.');
    const task: WorkbenchTaskDraft = {
      title,
      goal,
      acceptanceCriteria: [
        'Implement every recorded Dossier selection and preserve its stated constraints.',
        'Verify the resulting behavior with the checks required by the affected surface.',
      ],
      evidenceLinks: [this.document().workbench.entryPath],
      chosenOption: bounded(this.selectedSummary(), 2_000) || null,
      relatedTaskKeys: this.document().workbench.sourceTaskKeys,
      targetProject: this.projectName(),
      initialLane: '1-preparation',
      mode: 'coding',
      taskType: 'feature',
    };
    return this.baseRequest(outcome, actor, task, null);
  }

  private baseRequest(
    outcome: Exclude<DraftMode, null>,
    actor: string,
    task: WorkbenchTaskDraft | null,
    archiveReason: string | null,
  ): PrepareWorkbenchDecisionRequest {
    this.validationError.set(null);
    return {
      operationId: this.operationId() || createOperationId(),
      outcome,
      expectedRevision: this.document().revision,
      expectedFingerprint: this.document().fingerprint,
      actor,
      archiveReason,
      task,
      responses: [...this.responses()],
    };
  }

  private seedFeatureDraft(): void {
    this.resetFeedback();
    this.operationId.set(createOperationId());
    this.title.set(`Implement ${this.document().workbench.title}`);
    const decisions = this.selectedSummary();
    this.goal.set(bounded([
      this.document().workbench.summary.trim(),
      decisions ? `Recorded decisions:\n${decisions}` : '',
    ].filter(Boolean).join('\n\n'), 20_000));
  }

  private invalid(message: string): null {
    this.validationError.set(message);
    return null;
  }

  private resetFeedback(): void {
    this.validationError.set(null);
    this.actionError.set(null);
  }
}

function selectedDecisionText(
  points: readonly WorkbenchDecisionPoint[],
  responses: readonly WorkbenchDecisionResponse[],
): string {
  const responseById = new Map(responses.map(response => [response.decisionId, response]));
  return points.flatMap(point => {
    const response = responseById.get(point.id);
    if (!response) return [];
    const selected = new Set(response.selectedOptionIds);
    const labels = point.options.filter(option => selected.has(option.id)).map(option => option.label);
    const choice = `${point.label}: ${labels.join(', ') || 'No option selected'}`;
    return [response.comment ? `${choice}. Note: ${response.comment}` : choice];
  }).join('\n');
}

function cardPrompt(
  document: WorkbenchDocument,
  draft: WorkbenchTaskDraft,
  points: readonly WorkbenchDecisionPoint[],
  responses: readonly WorkbenchDecisionResponse[],
): string {
  return [
    '# Dossier-backed feature',
    '',
    `Source: \`${document.workbench.entryPath}\``,
    '',
    '## Goal',
    '',
    draft.goal,
    '',
    '## Recorded decisions',
    '',
    selectedDecisionText(points, responses) || '(No inline decision points were present.)',
    '',
    '## Acceptance criteria',
    '',
    ...draft.acceptanceCriteria.map(item => `- ${item}`),
  ].join('\n');
}

function taskKeyTail(taskKey: string): string {
  return taskKey.includes('::') ? taskKey.slice(taskKey.lastIndexOf('::') + 2) : taskKey;
}

function bounded(value: string, length: number): string {
  return value.length <= length ? value : value.slice(0, length);
}

function laneLabel(lane: string | null): string {
  return lane === '1-preparation' ? 'Preparation' : lane ?? 'Unknown lane';
}

function actionErrorMessage(error: unknown): string {
  const candidate = error as { error?: { error?: string } | string; message?: string } | null;
  if (typeof candidate?.error === 'string') return candidate.error;
  if (candidate?.error && typeof candidate.error.error === 'string') return candidate.error.error;
  return candidate?.message || 'The feature card could not be created.';
}

function createOperationId(): string {
  const random = globalThis.crypto?.randomUUID?.()
    ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  return `workbench-ui-${random}`;
}
