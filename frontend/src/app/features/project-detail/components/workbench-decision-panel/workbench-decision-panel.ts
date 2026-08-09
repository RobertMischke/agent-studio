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
  WorkbenchDecisionProjection,
  WorkbenchDocument,
  WorkbenchTaskDraft,
} from '../../../../models/project-docs.model';
import { WorkbenchDecisionStore } from '../../state/workbench-decision.store';

type DecisionChoice = 'feature-spawn' | 'archive';

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
  readonly decisionChanged = output<void>();

  private readonly store = inject(WorkbenchDecisionStore);

  readonly choice = signal<DecisionChoice | null>(null);
  readonly actor = signal('Operator');
  readonly title = signal('');
  readonly goal = signal('');
  readonly acceptanceCriteria = signal('');
  readonly evidenceLinks = signal('');
  readonly chosenOption = signal('');
  readonly archiveReason = signal('');
  readonly validationError = signal<string | null>(null);
  private readonly operationId = signal('');

  readonly requestState = computed(() =>
    this.store.state(this.projectName(), this.document().workbench.id));
  readonly persistedDecision = computed(() => this.document().workbench.decision ?? null);
  readonly result = computed(() => this.requestState().result);
  readonly pending = computed(() => this.requestState().pending !== null);
  readonly settled = computed(() => {
    const decision = this.persistedDecision();
    if (decision) return decision.state === 'succeeded';
    // Schema-v1 descriptors carry no decision receipt in the projection; their
    // settled state is visible only through the status field.
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
  readonly activeOutcome = computed<DecisionChoice | null>(() =>
    this.persistedDecision()?.outcome
    ?? this.result()?.outcome
    ?? this.choice());
  readonly stage = computed(() =>
    this.document().workbench.decisionStage
    ?? this.result()?.decisionStage
    ?? null);
  readonly canConfirm = computed(() => {
    const stage = this.stage();
    return !this.settled()
      && !this.mutationBlocked()
      && (stage === 'prepared' || stage === 'pending' || stage === 'failed');
  });

  choose(choice: DecisionChoice): void {
    if (this.persistedDecision() || this.pending()) return;
    this.choice.set(choice);
    this.validationError.set(null);
    this.operationId.set(createOperationId());
    if (choice === 'feature-spawn' && !this.title()) {
      const workbench = this.document().workbench;
      this.title.set(`Implement ${workbench.title}`);
      this.goal.set(workbench.summary);
      this.acceptanceCriteria.set('Implement the confirmed Workbench decision and verify the resulting behavior.');
      this.evidenceLinks.set(workbench.entryPath);
    }
  }

  cancelDraft(): void {
    if (this.pending()) return;
    this.choice.set(null);
    this.validationError.set(null);
    this.operationId.set('');
  }

  updateText(target: ReturnType<typeof signal<string>>, event: Event): void {
    target.set((event.target as HTMLInputElement | HTMLTextAreaElement).value);
  }

  prepare(): void {
    const choice = this.choice();
    if (!choice || this.pending() || this.mutationBlocked()) return;
    const request = this.prepareRequest(choice);
    if (!request) return;
    const workbench = this.document().workbench;
    this.store.prepare(this.projectName(), workbench.id, request).subscribe({
      next: () => this.decisionChanged.emit(),
      error: () => undefined,
    });
  }

  /**
   * Prepare only validates and fingerprints; nothing is written until here. So
   * confirm repeats the prepared payload against the revision/fingerprint that
   * prepare reported back.
   */
  confirm(): void {
    if (!this.canConfirm() || this.pending()) return;
    const outcome = this.activeOutcome();
    if (!outcome) return;
    const prepared = this.prepareRequest(outcome);
    if (!prepared) return;
    const decision = this.persistedDecision();
    const result = this.result();
    const operationId = decision?.operationId ?? result?.operationId ?? prepared.operationId;
    const matches = result?.operationId === operationId;
    this.store.confirm(this.projectName(), this.document().workbench.id, {
      operationId,
      outcome,
      expectedRevision: matches ? result!.revision : this.document().revision,
      expectedFingerprint: matches ? result!.fingerprint : this.document().fingerprint,
      actor: this.actor().trim() || decision?.preparedBy || 'Operator',
      archiveReason: prepared.archiveReason,
      task: prepared.task,
      confirmed: true,
    }).subscribe({
      next: () => this.decisionChanged.emit(),
      error: () => undefined,
    });
  }

  outcomeLabel(decision: WorkbenchDecisionProjection | null = this.persistedDecision()): string {
    const outcome = decision?.outcome ?? this.activeOutcome();
    return outcome === 'archive' ? 'Archive Workbench' : 'Build as feature';
  }

  stageLabel(): string {
    switch (this.stage()) {
      case 'prepared': return 'Awaiting confirmation';
      case 'pending': return 'Decision in progress';
      case 'failed': return 'Retry needed';
      case 'succeeded': return 'Feature created';
      case 'archived': return 'Archived';
      default: return this.gateReady() ? 'Ready for Sichtblick' : 'Still being shaped';
    }
  }

  private prepareRequest(choice: DecisionChoice): PrepareWorkbenchDecisionRequest | null {
    const actor = this.actor().trim();
    if (!actor) return this.invalid('Add the decision owner.');
    if (choice === 'archive') {
      const reason = this.archiveReason().trim();
      if (!reason) return this.invalid('Add a reason before preparing the archive decision.');
      this.validationError.set(null);
      return this.baseRequest(choice, actor, null, reason);
    }

    const title = this.title().trim();
    const goal = this.goal().trim();
    const acceptanceCriteria = lines(this.acceptanceCriteria());
    if (!title || !goal || acceptanceCriteria.length === 0)
      return this.invalid('Title, goal, and at least one acceptance criterion are required.');
    const task: WorkbenchTaskDraft = {
      title,
      goal,
      acceptanceCriteria,
      evidenceLinks: lines(this.evidenceLinks()),
      chosenOption: this.chosenOption().trim() || null,
      relatedTaskKeys: this.document().workbench.sourceTaskKeys,
      targetProject: this.projectName(),
      initialLane: '1-preparation',
      mode: 'coding',
      taskType: 'feature',
    };
    this.validationError.set(null);
    return this.baseRequest(choice, actor, task, null);
  }

  private baseRequest(
    outcome: DecisionChoice,
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
    };
  }

  private invalid(message: string): null {
    this.validationError.set(message);
    return null;
  }
}

function lines(value: string): string[] {
  return [...new Set(value.split(/\r?\n/).map(line => line.trim()).filter(Boolean))];
}

function createOperationId(): string {
  const random = globalThis.crypto?.randomUUID?.()
    ?? `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
  return `workbench-ui-${random}`;
}
