import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import type {
  WorkbenchOverview,
  WorkbenchOverviewItem,
  WorkbenchStatus,
} from '../../../../models/project-docs.model';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { WorkbenchOverviewComponent } from './workbench-overview.component';

function item(
  id: string,
  status: WorkbenchStatus,
  openDecisionCount = 0,
  relatedTaskKeys: string[] = [],
): WorkbenchOverviewItem {
  return {
    projectName: id.startsWith('other') ? 'Other' : 'Demo',
    projectShortCode: id.startsWith('other') ? 'OTH' : 'DEM',
    projectColor: '#a78bfa',
    workbench: {
      id,
      title: id,
      summary: `${id} summary`,
      status,
      phase: 'testing',
      updatedAtUtc: '2026-08-09T10:00:00Z',
      entryPath: `docs/${id}/index.html`,
      valid: true,
      error: null,
      sourceTaskKeys: [],
      relatedTaskKeys,
      openDecisionCount,
    },
  };
}

function overview(items: WorkbenchOverviewItem[], projectName: string | null = null): WorkbenchOverview {
  return {
    projectName,
    count: items.length,
    currentCount: items.filter(entry => ['active', 'decision-pending', 'decided'].includes(entry.workbench.status)).length,
    historyCount: items.filter(entry => ['archived', 'documented'].includes(entry.workbench.status)).length,
    items,
  };
}

function taskStatus(key: string, lane: string): TaskReferenceStatus {
  return {
    key,
    exists: true,
    taskKey: `task-${key}`,
    title: `${key} title`,
    lane,
    projectId: 'project-demo',
    projectName: 'Demo',
    projectColor: '#a78bfa',
    merge: null,
    reviewGrade: null,
  };
}

async function configure(): Promise<ReturnType<typeof vi.fn>> {
  const openTaskKey = vi.fn(() => true);
  await TestBed.configureTestingModule({
    imports: [WorkbenchOverviewComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      { provide: TaskReferenceNavigationService, useValue: { openTaskKey } },
    ],
  }).compileComponents();
  return openTaskKey;
}

describe('WorkbenchOverviewComponent', () => {
  it('renders calm lifecycle rows and hydrates linked cards in one stable batch', async () => {
    const openTaskKey = await configure();
    const fixture = TestBed.createComponent(WorkbenchOverviewComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const pending = item('pending', 'decision-pending', 3, ['AGT-11', 'agt-10']);
    pending.workbench.documentation = {
      eligible: false,
      totalCount: 2,
      terminalCount: 0,
      openCount: 2,
      missingCount: 0,
      references: [{ key: 'AGT-10', exists: true, terminal: false, lane: '3-progress' }],
    };
    const active = item('active', 'active', 0, ['AGT-12']);
    const decided = item('decided', 'decided', 0, ['agt-13']);
    decided.workbench.documentation = {
      eligible: false,
      totalCount: 1,
      terminalCount: 0,
      openCount: 1,
      missingCount: 0,
      references: [{ key: 'AGT-13', exists: true, terminal: false, lane: '5-human-review' }],
    };
    const documented = item('documented', 'documented');
    documented.workbench.documentation = {
      eligible: true,
      totalCount: 1,
      terminalCount: 1,
      openCount: 0,
      missingCount: 0,
      references: [{ key: 'AGT-9', exists: true, terminal: true, lane: '6-completed' }],
    };
    http.expectOne('/api/workbenches').flush(overview([
      pending,
      active,
      decided,
      item('discarded', 'archived'),
      documented,
    ]));
    fixture.detectChanges();

    const referenceRequest = http.expectOne('/api/tasks/reference-status');
    expect(referenceRequest.request.body).toEqual({ keys: ['AGT-10', 'AGT-11', 'AGT-12', 'AGT-13'] });
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-task-loading-Demo-pending-AGT-10"]'))
      .not.toBeNull();
    referenceRequest.flush({ items: [
      taskStatus('AGT-10', '3-progress'),
      taskStatus('AGT-12', '2-ready'),
      taskStatus('AGT-13', '5-human-review'),
    ] });
    fixture.detectChanges();

    const pendingRow = fixture.nativeElement.querySelector('[data-testid="workbench-overview-item-Demo-pending"]');
    const decidedRow = fixture.nativeElement.querySelector('[data-testid="workbench-overview-item-Demo-decided"]');
    expect(pendingRow?.textContent).toContain('Decision pending');
    expect(pendingRow?.textContent).toContain('3 open decisions');
    expect(decidedRow?.textContent).toContain('Accepted / In progress');
    expect(decidedRow?.textContent).not.toContain('Decision pending');
    expect(decidedRow?.textContent).not.toContain('Tracking');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-current-count"]')?.textContent)
      .toContain('Current 3');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-current-section-count"]')?.textContent)
      .toContain('2 Dossiers');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-history-count"]')?.textContent)
      .toContain('History 2');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-history-section-count"]')?.textContent)
      .toContain('2 Dossiers');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-project-Demo-pending"]')?.textContent)
      .toContain('DEM');

    const activeTask = fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-task-Demo-pending-AGT-10"]',
    ) as HTMLElement;
    expect(activeTask.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('active');
    const activeLink = activeTask.querySelector('a') as HTMLAnchorElement;
    expect(activeLink.getAttribute('href')).toBe('#task:AGT-10');
    activeLink.click();
    expect(openTaskKey).toHaveBeenCalledWith('task-AGT-10');
    const ghostTask = fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-task-Demo-pending-AGT-11"]',
    ) as HTMLElement;
    expect(ghostTask.querySelector('[data-tone]')?.getAttribute('data-tone')).toBe('ghost');
    expect(ghostTask.querySelector('a')).toBeNull();
    expect(fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-task-Demo-active-AGT-12"] [data-tone]',
    )?.getAttribute('data-tone')).toBe('queued');
    expect(fixture.nativeElement.querySelector(
      '[data-testid="workbench-overview-task-Demo-decided-AGT-13"] [data-tone]',
    )?.getAttribute('data-tone')).toBe('waiting');

    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-list"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')).toBeNull();
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-toggle"]') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-list"]')?.textContent)
      .toContain('Discarded');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')?.textContent)
      .toContain('Documented');

    let opened: WorkbenchOverviewItem | null = null;
    fixture.componentInstance.openWorkbench.subscribe(value => opened = value);
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-full-Demo-pending"]') as HTMLButtonElement).click();
    expect(opened).toEqual(pending);

    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-open-Demo-pending"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-inline-Demo-pending"]'))
      .not.toBeNull();
    http.expectOne('/api/projects/Demo/workbenches/pending').flush({
      workbench: { ...pending.workbench, relatedTaskKeys: [], documentation: null },
      html: '<section data-decision-id="route" data-decision-kind="single"><strong>Choose route</strong><span data-option-id="direct">Direct</span></section>',
      branch: 'develop',
      revision: 'abc123',
      workingTreeModified: false,
      fingerprint: 'fingerprint',
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-viewer"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-viewer-open-wiki"]')).toBeNull();
    http.verify();
  });

  it('refreshes a project-scoped queue after a matching live event', async () => {
    vi.useFakeTimers();
    try {
      await configure();
      const fixture = TestBed.createComponent(WorkbenchOverviewComponent);
      fixture.componentRef.setInput('projectName', 'Demo');
      fixture.detectChanges();
      const http = TestBed.inject(HttpTestingController);
      http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
        .flush(overview([item('active', 'active')], 'Demo'));

      TestBed.inject(JobsHubClient).workbenchEvent.set({
        type: 'created',
        projectName: 'Demo',
        workbenchId: 'new-item',
        workbench: null,
        previousStatus: null,
        occurredAtUtc: new Date().toISOString(),
      });
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(80);
      http.expectOne(request => request.url === '/api/workbenches' && request.params.get('project') === 'Demo')
        .flush(overview([item('new-item', 'active'), item('active', 'active')], 'Demo'));
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).toContain('new-item');
      http.verify();
    } finally {
      vi.useRealTimers();
    }
  });
});
