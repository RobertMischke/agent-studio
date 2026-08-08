import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import {
  WorkbenchDecisionAnswer,
  WorkbenchDecisionPoint,
  WorkbenchDocument,
} from '../../../../models/project-docs.model';
import { WorkbenchDecisionPanelComponent } from './workbench-decision-panel';

const POINTS: WorkbenchDecisionPoint[] = [{
  id: 'routing-owner',
  kind: 'single',
  label: 'Where should routing run?',
  options: [
    { id: 'task-api', label: 'Task API recommendation' },
    { id: 'studio-only', label: 'Studio default only' },
  ],
  commentEnabled: true,
  commentLabel: 'Optional note',
}];

const ANSWERS: WorkbenchDecisionAnswer[] = [{
  decisionId: 'routing-owner',
  kind: 'single',
  selectedOptions: [{ id: 'task-api', label: 'Task API recommendation' }],
  comment: 'Keep the recommendation editable.',
}];

const DOCUMENT: WorkbenchDocument = {
  workbench: {
    id: 'routing-policy',
    title: 'Routing policy',
    summary: 'Choose the durable routing direction.',
    status: 'active',
    phase: 'decision-ready',
    updatedAtUtc: '2026-07-26T10:00:00Z',
    entryPath: 'docs/operations/routing-policy/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: ['AGT-2300'],
    lifecycleState: 'review-requested',
    decision: null,
    decisionStage: null,
  },
  html: '<h1>Routing policy</h1>',
  branch: 'develop',
  revision: 'a'.repeat(40),
  workingTreeModified: false,
  fingerprint: 'b'.repeat(64),
};

describe('WorkbenchDecisionPanelComponent', () => {
  let fixture: ComponentFixture<WorkbenchDecisionPanelComponent>;
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchDecisionPanelComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(WorkbenchDecisionPanelComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.componentRef.setInput('decisionPoints', POINTS);
    fixture.componentRef.setInput('answers', ANSWERS);
    fixture.detectChanges();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('prefills a compact card confirmation from inline answers and persists both', () => {
    click('workbench-decision-prepare-card');
    const confirmation = fixture.nativeElement.querySelector(
      '[data-testid="workbench-feature-card-confirmation"]') as HTMLElement;
    expect(confirmation.textContent).toContain('Task API recommendation');
    expect((confirmation.querySelector('[data-testid="workbench-decision-goal"]') as HTMLTextAreaElement).value)
      .toContain('Keep the recommendation editable.');

    click('workbench-decision-submit');
    const prepare = http.expectOne(
      '/api/projects/Agent%20Studio/workbenches/routing-policy/decisions/prepare');
    expect(prepare.request.body).toEqual(expect.objectContaining({
      outcome: 'feature-spawn',
      expectedRevision: 'a'.repeat(40),
      expectedFingerprint: 'b'.repeat(64),
      actor: 'Operator',
      answers: ANSWERS,
      task: expect.objectContaining({
        title: 'Implement Routing policy',
        chosenOption: expect.stringContaining('Task API recommendation'),
        relatedTaskKeys: ['AGT-2300'],
        initialLane: '1-preparation',
        mode: 'coding',
        taskType: 'feature',
      }),
    }));
    const operationId = prepare.request.body.operationId;
    prepare.flush({
      success: true,
      errorCode: null,
      error: null,
      workbenchId: 'routing-policy',
      operationId,
      outcome: 'feature-spawn',
      decisionStage: 'prepared',
      revision: 'c'.repeat(40),
      fingerprint: 'd'.repeat(64),
      spawnedTaskKeys: [],
      idempotent: false,
    });

    const confirm = http.expectOne(
      '/api/projects/Agent%20Studio/workbenches/routing-policy/decisions/confirm');
    expect(confirm.request.body).toEqual(expect.objectContaining({
      operationId,
      confirmed: true,
      answers: ANSWERS,
      expectedRevision: 'c'.repeat(40),
      expectedFingerprint: 'd'.repeat(64),
    }));
    confirm.flush({
      success: true,
      errorCode: null,
      error: null,
      workbenchId: 'routing-policy',
      operationId,
      outcome: 'feature-spawn',
      decisionStage: 'succeeded',
      revision: 'e'.repeat(40),
      fingerprint: 'f'.repeat(64),
      spawnedTaskKeys: [],
      idempotent: false,
    });
  });

  it('renders compact audit provenance for a persisted archive after reload', () => {
    fixture.componentRef.setInput('document', {
      ...DOCUMENT,
      workbench: {
        ...DOCUMENT.workbench,
        status: 'archived',
        lifecycleState: 'done',
        decisionStage: 'archived',
        decision: {
          outcome: 'archive',
          state: 'succeeded',
          operationId: 'workbench-ui-existing',
          sourceRevision: 'a'.repeat(40),
          sourceFingerprint: 'b'.repeat(64),
          preparedAt: '2026-07-26T10:00:00Z',
          preparedBy: 'Robert',
          confirmedAt: '2026-07-26T10:01:00Z',
          confirmedBy: 'Robert',
          decidedAt: '2026-07-26T10:01:00Z',
          reason: 'The experiment disproved the direction.',
          failure: null,
          answers: ANSWERS,
          taskDraft: null,
          spawnedTaskKeys: [],
        },
      },
    });
    fixture.detectChanges();

    const receipt = fixture.nativeElement.querySelector('[data-testid="workbench-decision-receipt"]');
    expect(fixture.nativeElement.textContent).toContain('Decision by Robert');
    expect(receipt.textContent).toContain('1 selected option');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-decision-submit"]')).toBeNull();
  });

  function click(testId: string): void {
    (fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
    fixture.detectChanges();
  }
});
