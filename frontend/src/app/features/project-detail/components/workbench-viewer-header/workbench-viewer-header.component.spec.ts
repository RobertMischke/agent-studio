import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import type { TaskInfo } from '../../../../models/task.model';
import { WorkbenchViewerHeaderComponent } from './workbench-viewer-header.component';

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
    sourceTaskKeys: [],
    relatedTaskKeys: ['AGT-12'],
    openDecisionCount: 3,
  },
  html: '<h1>Compact viewer header</h1>',
  branch: 'develop',
  revision: '1234567890abcdef',
  workingTreeModified: false,
  fingerprint: 'a'.repeat(64),
};

describe('WorkbenchViewerHeaderComponent', () => {
  it('derives linked cards, keeps the normal head to one row, and moves details into a popover', async () => {
    const jobs = signal([{
      id: 'linked-card',
      key: 'AGT-11',
      taskKey: 'Agent Studio::linked-card',
      title: 'Linked implementation card',
      state: '3-progress',
      projectName: 'Agent Studio',
      references: {
        dependsOn: [], relatedTo: [], blockedBy: [], supersedes: [], workbenches: ['agt-w4'],
      },
    }] as unknown as TaskInfo[]);
    const statuses = [
      {
        key: 'AGT-11', exists: true, taskKey: 'Agent Studio::linked-card',
        title: 'Linked implementation card', lane: '3-progress', projectId: 'PROJ-2',
        projectName: 'Agent Studio', projectColor: null, merge: null, reviewGrade: null,
      },
      {
        key: 'AGT-12', exists: true, taskKey: 'Agent Studio::legacy-card',
        title: 'Legacy descriptor card', lane: '2-ready', projectId: 'PROJ-2',
        projectName: 'Agent Studio', projectColor: null, merge: null, reviewGrade: null,
      },
    ];
    const getReferenceStatuses = vi.fn(() => of(statuses));
    const openTaskKey = vi.fn(() => true);

    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerHeaderComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: { jobs, getReferenceStatuses } },
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerHeaderComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.componentRef.setInput('pageContext', {
      projectName: 'Agent Studio', relPath: 'operations/viewer-header/index.html',
      title: DOCUMENT.workbench.title, pageType: 'workbench', excerpt: DOCUMENT.workbench.summary,
    });
    fixture.detectChanges();

    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/projects/Agent%20Studio/wiki/home').flush({ sections: [] });
    fixture.detectChanges();

    expect(getReferenceStatuses).toHaveBeenCalledWith(['AGT-11', 'AGT-12']);
    const header = fixture.nativeElement.querySelector('[data-testid="workbench-viewer-header"]');
    expect(header.textContent).toContain('Compact viewer header');
    expect(header.textContent).toContain('3 open');
    expect(header.textContent).not.toContain(DOCUMENT.workbench.summary);
    expect(header.querySelectorAll('app-task-reference-microcard')).toHaveLength(2);

    const taskLink = header.querySelector('[data-testid="workbench-viewer-task-AGT-11"] a') as HTMLAnchorElement;
    taskLink.click();
    expect(openTaskKey).toHaveBeenCalledWith('Agent Studio::linked-card');

    const popover = document.querySelector('[data-testid="workbench-viewer-details-popover"]') as HTMLElement;
    expect(popover.hidden).toBe(true);
    (header.querySelector('[data-testid="workbench-viewer-details-trigger"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(popover.hidden).toBe(false);
    expect(popover.textContent).toContain(DOCUMENT.workbench.summary);
    expect(popover.textContent).toContain('docs/operations/viewer-header/index.html');
    expect(popover.querySelector('[data-testid="workbench-decision-panel"]')).toBeTruthy();

    fixture.destroy();
    http.verify();
  });
});
