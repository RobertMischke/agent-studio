import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import {
  WorkbenchDecisionPoint,
  WorkbenchDecisionResponse,
  WorkbenchDocument,
} from '../../../../models/project-docs.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchDecisionPanelComponent } from './workbench-decision-panel';

const POINTS: WorkbenchDecisionPoint[] = [{
  id: 'route',
  kind: 'single',
  label: 'Choose the route',
  options: [
    { id: 'direct', label: 'Direct path' },
    { id: 'queue', label: 'Queue first' },
  ],
  commentLabel: 'Optional note',
}];

const RESPONSES: WorkbenchDecisionResponse[] = [{
  decisionId: 'route',
  kind: 'single',
  selectedOptionIds: ['direct'],
  comment: 'Keep the boundary explicit.',
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
  pages: [],
  branch: 'develop',
  revision: 'a'.repeat(40),
  workingTreeModified: false,
  fingerprint: 'b'.repeat(64),
};

describe('WorkbenchDecisionPanelComponent', () => {
  let fixture: ComponentFixture<WorkbenchDecisionPanelComponent>;
  let http: HttpTestingController;
  const createJob = vi.fn(() => of({ id: 'implement-routing-policy' }));
  const getDetailByProject = vi.fn(() => of({
    info: {
      id: 'implement-routing-policy',
      key: 'AGT-2400',
      displayKey: 'AGT-2400',
      taskKey: 'PROJ-001::AGT-2400',
      title: 'Implement Routing policy',
      state: '1-preparation',
    },
  }));
  const getReferenceStatuses = vi.fn(() => of([]));
  const getWatchPaths = vi.fn(() => of([{ name: 'Agent Studio', path: '/tasks/agent-studio' }]));
  const refresh = vi.fn();

  beforeEach(async () => {
    createJob.mockClear();
    getDetailByProject.mockClear();
    getReferenceStatuses.mockClear();
    getWatchPaths.mockClear();
    refresh.mockClear();
    await TestBed.configureTestingModule({
      imports: [WorkbenchDecisionPanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: TaskService,
          useValue: { createJob, getDetailByProject, getReferenceStatuses, getWatchPaths, refresh },
        },
        {
          provide: TaskReferenceNavigationService,
          useValue: { openTaskKey: vi.fn(() => true) },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(WorkbenchDecisionPanelComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.componentRef.setInput('decisionPoints', POINTS);
    fixture.componentRef.setInput('responses', RESPONSES);
    fixture.detectChanges();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('prefills a compact feature proposal from inline responses and records the created card', () => {
    click('workbench-decision-prepare');

    const prepare = http.expectOne(
      '/api/projects/Agent%20Studio/workbenches/routing-policy/decisions/prepare');
    expect(prepare.request.body).toEqual(expect.objectContaining({
      outcome: 'feature-spawn',
      expectedRevision: 'a'.repeat(40),
      expectedFingerprint: 'b'.repeat(64),
      actor: 'Operator',
      responses: RESPONSES,
      task: expect.objectContaining({
        title: 'Implement Routing policy',
        chosenOption: 'Choose the route: Direct path. Note: Keep the boundary explicit.',
        relatedTaskKeys: ['AGT-2300'],
        initialLane: '1-preparation',
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
      fingerprint: 'b'.repeat(64),
      spawnedTaskKeys: [],
      responses: RESPONSES,
      idempotent: false,
    });
    fixture.detectChanges();

    const confirmation = fixture.nativeElement.querySelector(
      '[data-testid="workbench-decision-feature-confirmation"]');
    expect(confirmation.textContent).toContain('Direct path');
    expect((confirmation.querySelector('[data-testid="workbench-decision-goal"]') as HTMLTextAreaElement).value)
      .toContain('Recorded decisions');

    click('workbench-decision-confirm');
    expect(createJob).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Implement Routing policy',
      watchPath: '/tasks/agent-studio',
      targetState: '1-preparation',
      taskType: 'feature',
    }));
    expect(getDetailByProject).toHaveBeenCalledWith('implement-routing-policy', 'Agent Studio');
    expect(refresh).toHaveBeenCalled();

    const confirm = http.expectOne(
      '/api/projects/Agent%20Studio/workbenches/routing-policy/decisions/confirm');
    expect(confirm.request.body).toEqual(expect.objectContaining({
      operationId,
      expectedRevision: 'c'.repeat(40),
      expectedFingerprint: 'b'.repeat(64),
      responses: RESPONSES,
      spawnedTaskKeys: ['AGT-2400'],
      confirmed: true,
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
      spawnedTaskKeys: ['AGT-2400'],
      responses: RESPONSES,
      idempotent: false,
    });
  });

  it('renders a persisted archive decision after reload with neutral Decision wording', () => {
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
          spawnedTaskKeys: [],
          responses: RESPONSES,
          taskDraft: null,
        },
      },
    });
    fixture.detectChanges();

    const receipt = fixture.nativeElement.querySelector('[data-testid="workbench-decision-receipt"]');
    expect(receipt.textContent).toContain('Archived');
    expect(receipt.textContent).toContain('The experiment disproved the direction.');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-decision-confirm"]')).toBeNull();
  });

  function click(testId: string): void {
    (fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) as HTMLButtonElement).click();
    fixture.detectChanges();
  }
});
