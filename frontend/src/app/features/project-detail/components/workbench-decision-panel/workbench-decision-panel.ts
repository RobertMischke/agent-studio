import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import {
  PrepareWorkbenchDecisionRequest,
  WorkbenchDecisionAnswer,
  WorkbenchDecisionPoint,
  WorkbenchDocument,
  WorkbenchTaskDraft,
} from '../../../../models/project-docs.model';
import { WorkbenchDecisionStore } from '../../state/workbench-decision.store';
import {
  workbenchDecisionAnswersComplete,
  workbenchDecisionSummary,
} from '../workbench-viewer/workbench-decision-markup';

type DraftMode = 'feature' | 'archive' | null;

@Component({
  selector: 'app-workbench-decision-panel',
  standalone: true,
  imports: [DatePipe, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-decision-panel.html',
  styleUrl: './workbench-decision-panel.scss',
})
export class WorkbenchDecisionPanelComponent {
  readonly projectName = input.required<string>();
  readonly document = input.required<WorkbenchDocument>();
  readonly decisionPoints = input.required<readonly WorkbenchDecisionPoint[]>();
  readonly answers = input.required<readonly WorkbenchDecisionAnswer[]>();
  readonly decisionChanged = output<void>();

  private readonly store = inject(WorkbenchDecisionStore);

  readonly actor = signal('Operator');
  readonly draftMode = signal<DraftMode>(null);
  readonly title = signal('');
  readonly goal = signal('');
  readonly archiveReason = signal('');
  readonly validationError = signal<string | null>(null);
  private readonly operationId = signal('');

  readonly requestState = computed(() =>
    this.store.state(this.projectName(), this.document().workbench.id));
  readonly persistedDecision = computed(() => this.document().workbench.decision ?? null);
  readonly pending = computed(() => this.requestState().pending !== null);
  readonly settled = computed(() => {
    const decision = this.persistedDecision();
    if (decision) return decision.state === 'succeeded';
    const status = this.document().workbench.status;
    return status === 'decided' || status === 'archived';
  });
  readonly gateReady = computed(() =>
    this.document().workbench.phase === 'decision-ready'
    || this.persistedDecision() !== null
    || this.settled());
  readonly mutationBlocked = computed(() =>
    this.document().workingTreeModified
    || (!this.document().revision && !this.document().fingerprint));
  readonly complete = computed(() => workbenchDecisionAnswersComplete(
    this.decisionPoints(),
    this.answers(),
  ));
  readonly selectionSummary = computed(() => workbenchDecisionSummary(
    this.decisionPoints(),
    this.answers(),
  ));
  readonly selectionRows = computed(() => {
    const summaries = this.selectionSummary();
    return this.answers()
      .filter(answer => answer.selectedOptions.length > 0)
      .map((answer, index) => ({ id: answer.decisionId, text: summaries[index] ?? answer.decisionId }));
  });
  readonly selectedOptionCount = computed(() => this.answers()
    .reduce((total, answer) => total + answer.selectedOptions.length, 0));
  readonly commentCount = computed(() => this.answers()
    .filter(answer => answer.comment).length);
  readonly decisionOwner = computed(() => this.persistedDecision()?.confirmedBy
    ?? this.persistedDecision()?.preparedBy
    ?? null);
  readonly decisionTime = computed(() => this.persistedDecision()?.decidedAt
    ?? this.persistedDecision()?.confirmedAt
    ?? null);
  readonly stage = computed(() =>
    this.document().workbench.decisionStage
    ?? this.requestState().result?.decisionStage
    ?? null);

  openFeatureDraft(): void {
    if (this.pending() || this.settled() || this.mutationBlocked() || !this.complete()) return;
    const workbench = this.document().workbench;
    const summary = this.selectionSummary();
    this.draftMode.set('feature');
    this.operationId.set(createOperationId());
    this.title.set(`Implement ${workbench.title}`);
    this.goal.set([
      workbench.summary,
      '',
      'Confirmed decisions:',
      ...summary.map(item => `- ${item}`),
    ].join('\n'));
    this.validationError.set(null);
  }

  openArchiveDraft(): void {
    if (this.pending() || this.settled() || this.mutationBlocked()) return;
    this.draftMode.set('archive');
    this.operationId.set(createOperationId());
    this.validationError.set(null);
  }

  cancelDraft(): void {
    if (this.pending()) return;
    this.draftMode.set(null);
    this.validationError.set(null);
    this.operationId.set('');
  }

  updateText(target: ReturnType<typeof signal<string>>, event: Event): void {
    target.set((event.target as HTMLInputElement | HTMLTextAreaElement).value);
  }

  submitDecision(): void {
    if (this.pending() || this.settled() || this.mutationBlocked()) return;
    const request = this.prepareRequest();
    if (!request) return;
    const workbench = this.document().workbench;
    this.store.prepare(this.projectName(), workbench.id, request).subscribe({
      next: prepared => this.store.confirm(this.projectName(), workbench.id, {
        ...request,
        expectedRevision: prepared.revision,
        expectedFingerprint: prepared.fingerprint,
        confirmed: true,
      }).subscribe({
        next: () => this.decisionChanged.emit(),
        error: () => undefined,
      }),
      error: () => undefined,
    });
  }

  stageLabel(): string {
    switch (this.stage()) {
      case 'prepared': return 'Decision ready';
      case 'pending': return 'Decision pending';
      case 'failed': return 'Retry needed';
      case 'succeeded': return 'Decided';
      case 'archived': return 'Archived';
      default: return this.gateReady() ? 'Decision ready' : 'In progress';
    }
  }

  private prepareRequest(): PrepareWorkbenchDecisionRequest | null {
    const actor = this.actor().trim();
    if (!actor) return this.invalid('Add the decision owner.');
    const mode = this.draftMode();
    if (mode === 'archive') {
      const reason = this.archiveReason().trim();
      if (!reason) return this.invalid('Add a reason before archiving the Workbench.');
      this.validationError.set(null);
      return this.baseRequest('archive', actor, null, reason);
    }
    if (mode !== 'feature' || !this.complete())
      return this.invalid('Complete every decision point before preparing the feature card.');

    const title = this.title().trim();
    const goal = this.goal().trim();
    if (!title || !goal) return this.invalid('Title and goal are required.');
    const summary = this.selectionSummary();
    const task: WorkbenchTaskDraft = {
      title,
      goal,
      acceptanceCriteria: [
        'Implement the confirmed Workbench decisions and verify the resulting behavior.',
      ],
      evidenceLinks: [this.document().workbench.entryPath],
      chosenOption: summary.join('\n'),
      relatedTaskKeys: this.document().workbench.sourceTaskKeys,
      targetProject: this.projectName(),
      initialLane: '1-preparation',
      mode: 'coding',
      taskType: 'feature',
    };
    this.validationError.set(null);
    return this.baseRequest('feature-spawn', actor, task, null);
  }

  private baseRequest(
    outcome: 'feature-spawn' | 'archive',
    actor: string,
    task: WorkbenchTaskDraft | null,
    archiveReason: string | null,
  ): PrepareWorkbenchDecisionRequest {
    return {
      operationId: this.operationId() || createOperationId(),
      outcome,
      expectedRevision: this.document().revision,
      expectedFingerprint: this.document().fingerprint,
      actor,
      archiveReason,
      task,
      answers: this.answers()
        .filter(answer => answer.selectedOptions.length > 0)
        .map(answer => ({
          ...answer,
          selectedOptions: answer.selectedOptions.map(option => ({ ...option })),
        })),
    };
  }

  private invalid(message: string): null {
    this.validationError.set(message);
    return null;
  }
}

function createOperationId(): string {
  const random = globalThis.crypto?.randomUUID?.()
    ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  return `workbench-ui-${random}`;
}
