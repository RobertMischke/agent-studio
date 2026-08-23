import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ProjectCycleTimePanelComponent } from './project-cycle-time-panel.component';
import type { ProjectCycleTimeResponse } from '../../models/project-cycle-time.model';

/**
 * Renders the panel against a flushed HTTP response and asserts the calm
 * surface: window selector, summary line, composition bar segments in lane
 * order, the stage table with highlighted rows, and the sortable drill-down.
 */
describe('ProjectCycleTimePanelComponent', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [ProjectCycleTimePanelComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(ProjectCycleTimePanelComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    const http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, http };
  }

  it('requests the 7d window by default and renders aggregates, bar, and tasks', async () => {
    const { fixture, http } = await mount();
    const request = http.expectOne(r => r.url === '/api/projects/Demo/cycle-time');
    expect(request.request.params.get('window')).toBe('7d');
    request.flush(response());
    fixture.detectChanges();
    await fixture.whenStable();

    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[data-testid="cycle-time-summary"]')?.textContent).toContain('2 tasks completed');
    expect(root.querySelector('[data-testid="cycle-time-window-7d"]')?.getAttribute('aria-pressed')).toBe('true');

    const segments = Array.from(root.querySelectorAll('[data-testid="cycle-time-bar"] .cyc__segment'))
      .map(el => el.getAttribute('data-stage'));
    expect(segments).toEqual(['queueWait', 'coding', 'testGate', 'integration', 'humanReview']);

    const stageRows = Array.from(root.querySelectorAll('[data-testid="cycle-time-stages"] tbody:not(.cyc__rollups) tr'))
      .map(el => el.getAttribute('data-stage'));
    expect(stageRows).toEqual(['queueWait', 'coding', 'testGate', 'integration', 'humanReview']);
    const gateRow = root.querySelector('[data-testid="cycle-time-stages"] tr[data-stage="testGate"]');
    expect(gateRow?.classList.contains('cyc__row--highlighted')).toBe(true);
    expect(gateRow?.textContent).toContain('10m');

    const rollups = Array.from(root.querySelectorAll('[data-testid="cycle-time-stages"] .cyc__rollups tr'))
      .map(el => el.getAttribute('data-stage'));
    expect(rollups).toEqual(['reviewRun', 'leadTime', 'cycleTime']);

    const keys = () => Array.from(root.querySelectorAll('[data-testid="cycle-time-tasks"] tbody tr'))
      .map(el => el.getAttribute('data-task-key'));
    expect(keys()).toEqual(['DEM-2', 'DEM-1']);

    (root.querySelector('[data-testid="cycle-time-sort-testGate"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    expect(keys()).toEqual(['DEM-1', 'DEM-2']);
    expect(root.querySelector('[data-testid="cycle-time-outcomes"]')?.textContent).toContain('Merged 1');
    http.verify();
  });

  it('switches the window, emits openTask for a drill-down row, and shows load errors', async () => {
    const { fixture, http } = await mount();
    http.expectOne(r => r.url === '/api/projects/Demo/cycle-time').flush(response());
    fixture.detectChanges();
    await fixture.whenStable();

    const opened: { jobId: string; watchPath: string }[] = [];
    fixture.componentInstance.openTask.subscribe(value => opened.push(value));
    const root: HTMLElement = fixture.nativeElement;
    (root.querySelector('[data-testid="cycle-time-open-DEM-1"]') as HTMLButtonElement).click();
    expect(opened).toEqual([{ jobId: 'dem-1', watchPath: 'C:/tasks/demo' }]);

    (root.querySelector('[data-testid="cycle-time-window-30d"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();
    const second = http.expectOne(r => r.url === '/api/projects/Demo/cycle-time');
    expect(second.request.params.get('window')).toBe('30d');
    second.flush({ error: 'boom' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(root.querySelector('[data-testid="cycle-time-error"]')?.textContent).toContain('boom');
    http.verify();
  });
});

function response(): ProjectCycleTimeResponse {
  const base = {
    terminalState: '7-archive',
    watchPath: 'C:/tasks/demo',
    createdAt: '2026-08-18T00:00:00Z',
    firstClaimedAt: '2026-08-18T00:10:00Z',
    completionSource: 'ledger',
    reviewRounds: 1,
    bounceRounds: 0,
    integrationAttempts: 1,
    integrationStage: 'pre-human-review',
    dataGaps: [] as string[],
  };
  return {
    project: 'Demo',
    projectId: 'PROJ-001',
    shortCode: 'DEM',
    window: '7d',
    capturedAt: '2026-08-22T12:00:00Z',
    since: '2026-08-15T12:00:00Z',
    coverage: {
      tasksInProject: 5,
      tasksTerminal: 4,
      tasksInWindow: 2,
      excludedNoCompletionTimestamp: 1,
      excludedInFlight: 1,
      excludedEpics: 0,
      tasksWithoutLedger: 0,
      tasksWithLaneEntryCompletion: 0,
    },
    aggregates: [
      agg('preparation', 'Preparation', 'stage', 0, null),
      agg('queueWait', 'Queue wait', 'stage', 2, 300),
      agg('coding', 'Coding run', 'stage', 2, 1800),
      agg('reviewWait', 'Post-processing wait', 'stage', 0, null),
      agg('testGate', 'Build/test gate', 'stage', 2, 600, true),
      agg('reviewOther', 'Review aspects and decision', 'stage', 0, null),
      agg('integration', 'Integration', 'stage', 1, 120, true),
      agg('humanReview', 'Human review', 'stage', 2, 7200),
      agg('unattributed', 'Unattributed', 'stage', 0, null),
      agg('reviewRun', 'Review run', 'rollup', 2, 720),
      agg('leadTime', 'Lead time', 'rollup', 2, 10_000),
      agg('cycleTime', 'Cycle time', 'rollup', 2, 9700),
      agg('codingRuns', 'Coding runs', 'count', 2, 1, false, 'count'),
      agg('reviewRounds', 'Review rounds', 'count', 2, 1, false, 'count'),
      agg('bounceRounds', 'Bounce rounds', 'count', 2, 0, false, 'count'),
      agg('integrationAttempts', 'Integration attempts', 'count', 2, 1, false, 'count'),
    ],
    integrationOutcomes: [{ outcome: 'Merged', count: 1 }, { outcome: 'none', count: 1 }],
    tasks: [
      {
        ...base,
        taskId: 'dem-2',
        taskKey: 'DEM-2',
        title: 'Second task',
        completedAt: '2026-08-21T10:00:00Z',
        stages: { preparation: 0, queueWait: 300, coding: 1800, reviewWait: 0, testGate: 300, reviewOther: 0, integration: 0, humanReview: 7200, unattributed: 0 },
        reviewRunSeconds: 300,
        leadTimeSeconds: 9600,
        cycleTimeSeconds: 9300,
        codingRuns: 1,
        integrationOutcome: null,
      },
      {
        ...base,
        taskId: 'dem-1',
        taskKey: 'DEM-1',
        title: 'First task',
        completedAt: '2026-08-20T10:00:00Z',
        stages: { preparation: 0, queueWait: 300, coding: 1800, reviewWait: 0, testGate: 900, reviewOther: 0, integration: 120, humanReview: 7200, unattributed: 0 },
        reviewRunSeconds: 1020,
        leadTimeSeconds: 10_320,
        cycleTimeSeconds: 10_000,
        codingRuns: 1,
        integrationOutcome: 'Merged',
      },
    ],
  };
}

function agg(
  stage: string,
  label: string,
  kind: 'stage' | 'rollup' | 'count',
  count: number,
  p50: number | null,
  highlighted = false,
  unit: 'seconds' | 'count' = 'seconds',
) {
  return { stage, label, kind, unit, highlighted, count, p50, p90: p50, max: p50, mean: p50, total: (p50 ?? 0) * count };
}
