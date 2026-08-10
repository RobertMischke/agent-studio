import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type {
  WorkbenchOverview,
  WorkbenchOverviewItem,
  WorkbenchStatus,
} from '../../../../models/project-docs.model';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { WorkbenchOverviewComponent } from './workbench-overview.component';

function item(id: string, status: WorkbenchStatus, openDecisionCount = 0): WorkbenchOverviewItem {
  return {
    projectName: id.startsWith('other') ? 'Other' : 'Demo',
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
      openDecisionCount,
    },
  };
}

function overview(items: WorkbenchOverviewItem[], projectName: string | null = null): WorkbenchOverview {
  return {
    projectName,
    count: items.length,
    currentCount: items.filter(entry => ['active', 'decision-pending'].includes(entry.workbench.status)).length,
    historyCount: items.filter(entry => ['archived', 'decided'].includes(entry.workbench.status)).length,
    items,
  };
}

describe('WorkbenchOverviewComponent', () => {
  it('renders the priority queue and keeps discarded and completed history separate', async () => {
    await TestBed.configureTestingModule({
      imports: [WorkbenchOverviewComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(WorkbenchOverviewComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const pending = item('pending', 'decision-pending', 3);
    http.expectOne('/api/workbenches').flush(overview([
      pending,
      item('active', 'active'),
      item('discarded', 'archived'),
      item('completed', 'decided'),
    ]));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-decision-pending"]')?.textContent)
      .toContain('3 open');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-active"]')?.textContent)
      .toContain('active');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-list"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')).toBeNull();

    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-toggle"]') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-toggle"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-discarded-list"]')?.textContent)
      .toContain('discarded');
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-completed-list"]')?.textContent)
      .toContain('completed');

    let opened: WorkbenchOverviewItem | null = null;
    fixture.componentInstance.openWorkbench.subscribe(value => opened = value);
    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-full-Demo-pending"]') as HTMLButtonElement).click();
    expect(opened).toEqual(pending);

    (fixture.nativeElement.querySelector('[data-testid="workbench-overview-open-Demo-pending"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="workbench-overview-inline-Demo-pending"]'))
      .not.toBeNull();
    http.expectOne('/api/projects/Demo/workbenches/pending').flush({
      workbench: pending.workbench,
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
      await TestBed.configureTestingModule({
        imports: [WorkbenchOverviewComponent],
        providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
      }).compileComponents();
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
