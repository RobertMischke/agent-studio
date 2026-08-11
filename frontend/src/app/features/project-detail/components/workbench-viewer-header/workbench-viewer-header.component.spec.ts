import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import type { WorkbenchDocument } from '../../../../models/project-docs.model';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { TaskService } from '../../../../services/task.service';
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
  },
  html: '<h1>Compact viewer header</h1>',
  branch: 'develop',
  revision: '1234567890abcdef',
  workingTreeModified: false,
  fingerprint: 'a'.repeat(64),
};

describe('WorkbenchViewerHeaderComponent', () => {
  it('shows live update truth and offers manual refresh only while disconnected', async () => {
    const statuses = [
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
    ];
    const getReferenceStatuses = vi.fn(() => of(statuses));
    const openTaskKey = vi.fn(() => true);

    await TestBed.configureTestingModule({
      imports: [WorkbenchViewerHeaderComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: TaskService, useValue: { getReferenceStatuses } },
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchViewerHeaderComponent);
    fixture.componentRef.setInput('projectName', 'Agent Studio');
    fixture.componentRef.setInput('document', DOCUMENT);
    fixture.componentRef.setInput('liveConnected', true);
    fixture.componentRef.setInput('connectionStateChangedAtUtc', '2026-08-11T10:00:00Z');
    fixture.componentRef.setInput('lastUpdatedAtUtc', '2026-08-11T10:01:00Z');
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
      items: [{ sourceKey: 'AGT-11' }],
    });
    fixture.detectChanges();

    expect(getReferenceStatuses).toHaveBeenCalledWith(['AGT-11', 'AGT-12']);
    const header = fixture.nativeElement.querySelector(
      '[data-testid="workbench-viewer-header"]',
    ) as HTMLElement;
    expect(header.querySelector('[data-testid="workbench-viewer-title"]')?.textContent)
      .toContain('Compact viewer header');
    expect(header.querySelector('[data-testid="workbench-viewer-open-decisions"]')?.textContent)
      .toContain('3 open');
    expect(
      header.querySelector('[data-testid="workbench-viewer-tasks"]')
        ?.querySelectorAll('app-task-reference-microcard'),
    ).toHaveLength(2);
    expect(header.querySelector('[data-testid="workbench-viewer-refresh"]')).toBeNull();
    expect(header.querySelector('[data-testid="workbench-viewer-as-of"]')).toBeNull();

    const taskLink = header.querySelector(
      '[data-testid="workbench-viewer-task-AGT-11"] a',
    ) as HTMLAnchorElement;
    taskLink.click();
    expect(openTaskKey).toHaveBeenCalledWith('Agent Studio::linked-card');

    const disclosure = header.querySelector('.viewer-head__details') as HTMLDetailsElement;
    (header.querySelector('[data-testid="workbench-viewer-details-trigger"]') as HTMLElement)
      .click();
    fixture.detectChanges();
    const liveStatus = header.querySelector(
      '[data-testid="workbench-viewer-live-status"]',
    ) as HTMLElement;
    expect(liveStatus.dataset['connected']).toBe('true');
    expect(liveStatus.textContent).toContain('Connected since');
    expect(liveStatus.querySelector('[data-testid="workbench-viewer-manual-refresh"]'))
      .toBeNull();

    fixture.componentRef.setInput('liveConnected', false);
    fixture.componentRef.setInput('connectionStateChangedAtUtc', '2026-08-11T10:02:00Z');
    fixture.detectChanges();

    expect(header.querySelector('[data-testid="workbench-viewer-as-of"]')?.textContent)
      .toContain('As of');
    expect(liveStatus.dataset['connected']).toBe('false');
    expect(liveStatus.textContent).toContain('Disconnected since');
    const manualRefresh = liveStatus.querySelector(
      '[data-testid="workbench-viewer-manual-refresh"]',
    ) as HTMLButtonElement;
    expect(manualRefresh).toBeTruthy();

    const refresh = vi.fn();
    fixture.componentInstance.manualRefresh.subscribe(refresh);
    manualRefresh.click();
    expect(refresh).toHaveBeenCalledOnce();
    expect(disclosure.open).toBe(false);

    fixture.destroy();
    http.verify();
  });
});
