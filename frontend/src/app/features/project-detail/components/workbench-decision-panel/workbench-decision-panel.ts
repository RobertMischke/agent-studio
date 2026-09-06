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
import {
  TaskReferenceMicrocardComponent,
  TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import {
  ConfirmWorkbenchDecisionRequest,
  PrepareWorkbenchDecisionRequest,
  WorkbenchDecisionPoint,
  WorkbenchDecisionProjection,
  WorkbenchDecisionResponse,
  WorkbenchDecisionResult,
  WorkbenchDocument,
  WorkbenchTaskDraft,
} from '../../../../models/project-docs.model';
import { TaskService } from '../../../../services/task.service';
import { PublicDemoModeService } from '../../../../services/public-demo-mode.service';
import {
  WorkbenchDecisionDraftCard,
  WorkbenchDecisionDraftStore,
} from '../../state/workbench-decision-draft.store';
import { WorkbenchDecisionStore } from '../../state/workbench-decision.store';
import {
  actionErrorMessage,
  bounded,
  cardPrompt,
  createOperationId,
  laneLabel,
  selectedDecisionText,
  taskKeyTail,
} from './workbench-decision-panel.util';

type DraftMode = 'feature-spawn' | 'archive' | null;

@Component({
  selector: 'app-workbench-decision-panel',
  standalone: true,
  imports: [
    DatePipe,
    PendingButtonDirective,
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
  readonly decisionChanged = output<void>();
  readonly draftDiscarded = output<void>();

  private readonly store = inject(WorkbenchDecisionStore);
  private readonly drafts = inject(WorkbenchDecisionDraftStore);
  private readonly tasks = inject(TaskService);
  private readonly publicDemo = inject(PublicDemoModeService);
  private restoredDraftKey = '';

  readonly actor = signal('Operator');
  readonly draftMode = signal<DraftMode>(null);
  readonly title = signal('');
  readonly goal = signal('');
  readonly archiveReason = signal('');
  readonly validationError = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly createdCard = signal<WorkbenchDecisionDraftCard | null>(null);
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
    this.publicDemo.readOnly()
    || this.document().workingTreeModified
    || (!this.document().revision && !this.document().fingerprint));
  readonly readOnly = this.publicDemo.readOnly;
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
  readonly browserDraft = computed(() =>
    this.drafts.draft(this.projectName(), this.document().workbench.id));
  readonly hasBrowserDraft = computed(() => this.browserDraft() !== null && !this.settled());
  readonly canPrepareFeature = computed(() =>
    this.gateReady() && !this.settled() && !this.mutationBlocked()
    && !this.pending() && this.answersComplete());
  readonly canCreateFeature = computed(() =>
    this.canPrepareFeature() && this.draftMode() === 'feature-spawn'
    && this.title().trim().length > 0 && this.goal().trim().length > 0);
  readonly canConfirm = computed(() =>
    !this.settled() && !this.mutationBlocked() && this.stage() === 'prepared');

  constructor() {
    effect(() => {
      const decision = this.persistedDecision();
      if (decision?.confirmedBy || decision?.preparedBy)
        this.actor.set(decision.confirmedBy || decision.preparedBy);
    });
    effect(() => {
      const projectName = this.projectName();
      const document = this.document();
      const key = `${projectName}\u0000${document.workbench.id}`;
      const draft = this.drafts.draft(projectName, document.workbench.id);
      if (this.settled()) {
        this.drafts.discard(projectName, document.workbench.id);
        return;
      }
      if (draft?.mode === 'feature-spawn') {
        this.draftMode.set('feature-spawn');
        this.actor.set(draft.actor);
        this.title.set(draft.title);
        this.goal.set(draft.goal);
        this.operationId.set(draft.operationId ?? '');
        this.createdCard.set(draft.createdCard);
      } else if (key !== this.restoredDraftKey) {
        this.draftMode.set(null);
        this.title.set('');
        this.goal.set('');
        this.operationId.set('');
        this.createdCard.set(null);
      }
      this.restoredDraftKey = key;
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
    const actor = (event.target as HTMLInputElement).value;
    this.actor.set(actor);
    if (this.hasBrowserDraft())
      this.drafts.updateFeature(this.projectName(), this.document().workbench.id, { actor });
  }

  updateTitle(event: Event): void {
    const title = (event.target as HTMLInputElement).value;
    this.title.set(title);
    this.drafts.updateFeature(this.projectName(), this.document().workbench.id, { title });
  }

  updateGoal(event: Event): void {
    const goal = (event.target as HTMLTextAreaElement).value;
    this.goal.set(goal);
    this.drafts.updateFeature(this.projectName(), this.document().workbench.id, { goal });
  }

  updateArchiveReason(event: Event): void {
    this.archiveReason.set((event.target as HTMLTextAreaElement).value);
  }

  prepareFeatureCard(): void {
    if (!this.canPrepareFeature()) return;
    this.seedFeatureDraft();
    this.draftMode.set('feature-spawn');
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

  discardDraft(): void {
    if (this.pending()) return;
    this.drafts.discard(this.projectName(), this.document().workbench.id);
    this.draftMode.set(null);
    this.title.set('');
    this.goal.set('');
    this.operationId.set('');
    this.createdCard.set(null);
    this.store.clear(this.projectName(), this.document().workbench.id);
    this.resetFeedback();
    this.draftDiscarded.emit();
  }

  createFeatureCard(): void {
    if (!this.canCreateFeature() || this.pending()) return;
    const request = this.prepareRequest('feature-spawn');
    if (!request?.task) return;
    this.actionError.set(null);
    this.store.prepare(this.projectName(), this.document().workbench.id, request).pipe(
      switchMap(prepared => this.createCard(prepared.taskDraft ?? request.task!).pipe(
        map(card => ({ prepared, card })))),
      switchMap(({ prepared, card }) => this.confirm(request, [card.key], prepared)),
      catchError(error => {
        this.actionError.set(actionErrorMessage(error));
        return EMPTY;
      }),
    ).subscribe(() => {
      this.drafts.discard(this.projectName(), this.document().workbench.id);
      this.decisionChanged.emit();
    });
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
      default: return this.gateReady() ? 'Decision ready' : 'In progress'; // lane-presentation-lint: allow, decision stage
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
    preparedResult: WorkbenchDecisionResult | null = this.result(),
  ): Observable<unknown> {
    const result = preparedResult;
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

  private createCard(draft: WorkbenchTaskDraft): Observable<WorkbenchDecisionDraftCard> {
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
        this.drafts.rememberCreatedCard(this.projectName(), this.document().workbench.id, card);
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
    const operationId = createOperationId();
    const title = `Implement ${this.document().workbench.title}`;
    const decisions = this.selectedSummary();
    const goal = bounded([
      this.document().workbench.summary.trim(),
      decisions ? `Recorded decisions:\n${decisions}` : '',
    ].filter(Boolean).join('\n\n'), 20_000);
    const draft = this.drafts.beginFeature(
      this.projectName(),
      this.document().workbench.id,
      { actor: this.actor(), title, goal, operationId },
      this.responses(),
    );
    this.actor.set(draft.actor);
    this.title.set(draft.title);
    this.goal.set(draft.goal);
    this.operationId.set(draft.operationId ?? operationId);
    this.createdCard.set(draft.createdCard);
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
