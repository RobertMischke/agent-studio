import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import { ConfirmDialogService } from '../../../../services/confirm-dialog.service';
import { NotificationService } from '../../../../services/notification.service';
import {
  implementationStatusFor,
  WorkbenchViewerHeaderComponent,
} from './workbench-viewer-header.component';

const DOCUMENT: WorkbenchDocument = {
  workbench: {
    id: 'viewer-header',
    key: 'AGT-W4',
    title: 'Compact viewer header with a deliberately long title',
    summary: 'Detailed context stays out of the normal header row.',
    status: 'decision-pending',
    phase: 'decision-ready',
    updatedAtUtc: '2026-08-09T10:00:00Z',
    entryPath: 'docs/operations/viewer-header/index.html',
    valid: true,
    error: null,
    sourceTaskKeys: ['AGT-10'],
    relatedTaskKeys: ['AGT-12'],
  },
  html: '<h1>Compact viewer header</h1>',
  branch: 'develop',
  revision: '1234567890abcdef',
  workingTreeModified: false,
  fingerprint: 'a'.repeat(64),
};

describe('WorkbenchViewerHeaderComponent', () => {
  it('derives implementation only between the first started card and all-terminal state', () => {
    const status = (key: string, lane: string | null, exists = true): TaskReferenceStatus => ({
      key,
      exists,
      taskKey: null,
      title: null,
      lane,
      projectId: 'PROJ-2',
      projectName: 'Agent Studio',
      projectColor: null,
      merge: null,
      reviewGrade: null,
    });

    expect(implementationStatusFor('decision-pending', [status('AGT-1', '3-progress')]))
      .toBe('In implementation');
    expect(implementationStatusFor('decided', [
      status('AGT-1', '6-completed'),
      status('AGT-2', '7-archive'),
    ])).toBeNull();
    expect(implementationStatusFor('decided', [
      status('AGT-1', '6-completed'),
      status('AGT-404', null, false),
    ])).toBe('In implementation');
    expect(implementationStatusFor('documented', [status('AGT-1', '3-progress')]))
      .toBeNull();
  });

  it('keeps the head compact and creates a linked Dossier refresh card from its confirmation', async () => {
    const statuses = [
      {
        key: 'AGT-10',
        exists: true,
        taskKey: 'Agent Studio::source-card',
        title: 'Source implementation card',
        lane: '5-human-review',
        projectId: 'PROJ-2',
        projectName: 'Agent Studio',
        projectColor: null,
        merge: null,
        reviewGrade: null,
      },
      {
        key: 'AGT-11',
        exists: true,
        taskKey: 'Agent Studio::linked-card',
        title: 'Linked implementation card',
        lane: '3-progress',
        projectId: 'PROJ-2',
        projectName: 'Agent Studio',
        projectColor: null,
        merge: null,
        reviewGrade: null,
      },
      {
        key: 'AGT-12',
        exists: true,
        taskKey: 'Agent Studio::legacy-card',
        title: 'Legacy descriptor card',
        lane: '2-ready',
        projectId: 'PROJ-2',
        projectName: 'Agent Studio',
        projectColor: null,
        merge: null,
        reviewGrade: null,
      },
      {
        key: 'AGT-14',
        exists: true,
        taskKey: 'Agent Studio::refresh-viewer-header',
        title: 'Refresh: Compact viewer header with a deliberately long title',
        lane: '1-preparation',
        projectId: 'PROJ-2',
        projectName: 'Agent Studio',
        projectColor: null,
        merge: null,
        reviewGrade: null,
      },
    ];
    const getReferenceStatuses = vi.fn(() => of(statuses));
    const getWatchPaths = vi.fn(() => of([
      { name: 'Agent Studio', path: '/projects/agent-studio' },
    ]));
    const createJob = vi.fn(() => of({ id: 'refresh-viewer-header' }));
    const setTaskReferences = vi.fn(() => of({
      references: {
        dependsOn: [],
        relatedTo: [],
        blockedBy: [],
        supersedes: [],
        workbenches: ['AGT-W4'],
      },
      warnings: [],
    }));
    const refresh = vi.fn();
    const openTaskKey = vi.fn(() => true);

    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerHeaderComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: TaskService,
          useValue: {
            createJob,
            getReferenceStatuses,
            getWatchPaths,
            refresh,
            setTaskReferences,
          },
        },
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerHeaderComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.componentRef.setInput('decisionPoints', [
      { id: 'route', kind: 'single', label: 'Route', options: [], commentLabel: null },
      { id: 'density', kind: 'single', label: 'Density', options: [], commentLabel: null },
      { id: 'proof', kind: 'confirm', label: 'Proof', options: [], commentLabel: null },
    ]);
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Agent%20Studio/workbenches/AGT-W4/references').flush({
      projectName: 'Agent Studio',
      workbenchKey: 'AGT-W4',
      workbenchId: 'viewer-header',
      legacyTaskKeys: ['AGT-12'],
      items: [
        {
          sourceKey: 'AGT-11',
          sourceJobId: 'linked-card',
          sourceTitle: 'Linked implementation card',
          sourceState: '3-progress',
          sourceWatchPath: '/projects/agent-studio',
          kind: 'workbenches',
        },
      ],
    });
    fixture.detectChanges();

    expect(getReferenceStatuses).toHaveBeenCalledWith(['AGT-11', 'AGT-12', 'AGT-10']);
    const header = fixture.nativeElement.querySelector('[data-testid="workbench-viewer-header"]');
    expect(header.querySelector('[data-testid="workbench-viewer-title"]').textContent).toContain(
      'Compact viewer header',
    );
    expect(
      header.querySelector('[data-testid="workbench-viewer-open-decisions"]').textContent,
    ).toContain('3 open');
    expect(
      header
        .querySelector('[data-testid="workbench-viewer-tasks"]')
        .querySelectorAll('app-task-reference-microcard'),
    ).toHaveLength(3);
    expect(
      header.querySelector('[data-testid="workbench-viewer-implementation-status"]').textContent,
    ).toContain('In implementation');
    expect(header.querySelector('[data-testid="workbench-viewer-task-AGT-10"]')).toBeTruthy();

    const taskLink = header.querySelector(
      '[data-testid="workbench-viewer-task-AGT-11"] a',
    ) as HTMLAnchorElement;
    taskLink.click();
    expect(openTaskKey).toHaveBeenCalledWith('Agent Studio::linked-card');

    const popover = document.querySelector(
      '[data-testid="workbench-viewer-details-popover"]',
    ) as HTMLElement;
    const disclosure = header.querySelector('.viewer-head__details') as HTMLDetailsElement;
    expect(disclosure.open).toBe(false);
    (
      header.querySelector('[data-testid="workbench-viewer-details-trigger"]') as HTMLElement
    ).click();
    fixture.detectChanges();
    expect(disclosure.open).toBe(true);
    expect(popover.textContent).toContain(DOCUMENT.workbench.summary);
    expect(popover.textContent).toContain('docs/operations/viewer-header/index.html');
    expect(popover.querySelector('[data-testid="workbench-decision-panel"]')).toBeTruthy();

    (
      header.querySelector('[data-testid="workbench-viewer-refresh"]') as HTMLButtonElement
    ).click();
    fixture.detectChanges();
    const confirm = TestBed.inject(ConfirmDialogService);
    expect(confirm.active()).toEqual(expect.objectContaining({
      title: 'Create Dossier refresh card?',
      detail: expect.stringContaining('Refresh: Compact viewer header'),
      confirmLabel: 'Create card',
      kind: 'primary',
    }));

    confirm.accept();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(createJob).toHaveBeenCalledWith(expect.objectContaining({
      title: 'Refresh: Compact viewer header with a deliberately long title',
      watchPath: '/projects/agent-studio',
      targetState: '1-preparation',
      taskType: 'chore',
      mode: 'coding',
      promptMarkdown: expect.stringMatching(
        /Dossier path: `docs\/operations\/viewer-header\/index\.html`[\s\S]*Dossier key: `AGT-W4`[\s\S]*Update the document against reality[\s\S]*Do not add automatic document self-modification/,
      ),
    }));
    expect(setTaskReferences).toHaveBeenCalledWith(
      'refresh-viewer-header',
      {
        dependsOn: [],
        relatedTo: [],
        blockedBy: [],
        supersedes: [],
        workbenches: ['AGT-W4'],
      },
      '/projects/agent-studio',
    );
    expect(refresh).toHaveBeenCalled();

    http.expectOne('/api/projects/Agent%20Studio/workbenches/AGT-W4/references').flush({
      projectName: 'Agent Studio',
      workbenchKey: 'AGT-W4',
      workbenchId: 'viewer-header',
      legacyTaskKeys: ['AGT-12'],
      items: [
        { sourceKey: 'AGT-11' },
        { sourceKey: 'AGT-14' },
      ],
    });
    fixture.detectChanges();

    expect(getReferenceStatuses).toHaveBeenLastCalledWith(['AGT-11', 'AGT-14', 'AGT-12', 'AGT-10']);
    expect(header.querySelector('[data-testid="workbench-viewer-task-AGT-14"]')).toBeTruthy();
    expect(TestBed.inject(NotificationService).notifications()[0]).toEqual(expect.objectContaining({
      kind: 'success',
      title: 'Refresh card created',
    }));

    fixture.destroy();
    http.verify();
  });
});
