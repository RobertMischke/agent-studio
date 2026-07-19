import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../../models/task.model';
import type { RunRecord } from '../../../../run-timeline';
import { PipelineRunHistoryComponent } from './pipeline-run-history.component';

describe('PipelineRunHistoryComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PipelineRunHistoryComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
  });

  it('keeps a single run informational and does not open a dialog', () => {
    const fixture = createFixture([runRecord(1)], 1, '1 agent run');
    const trigger = historyTrigger(fixture.nativeElement);

    expect(trigger.getAttribute('aria-haspopup')).toBeNull();
    expect(trigger.getAttribute('aria-expanded')).toBeNull();

    trigger.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.open()).toBe(false);
    expect(
      document.body.querySelector('[data-testid="overview-pipeline-agent-run-history"]'),
    ).toBeNull();
  });

  it('opens multiple runs in a dialog and renders their chronological timeline', () => {
    const fixture = createFixture([runRecord(2), runRecord(1)], 2, '2 agent runs');
    const trigger = historyTrigger(fixture.nativeElement);

    expect(trigger.getAttribute('aria-expanded')).toBe('false');
    trigger.click();
    fixture.detectChanges();

    const dialog = document.body.querySelector(
      '[data-testid="overview-pipeline-agent-run-history"]',
    ) as HTMLElement | null;
    expect(dialog).not.toBeNull();
    expect(trigger.getAttribute('aria-expanded')).toBe('true');

    const cards = dialog?.querySelectorAll('[data-testid="run-timeline-card"]');
    expect(cards).toHaveLength(2);
    expect(cards?.[0].textContent).toContain('Prompt #1');
    expect(cards?.[1].textContent).toContain('Prompt #2');
    expect(dialog?.querySelector('[data-testid="run-transition-1-2"]')).not.toBeNull();
  });

  it('exposes the supplied run count in both data and accessible labels', () => {
    const fixture = createFixture(
      [runRecord(1), runRecord(2), runRecord(3), runRecord(4)],
      4,
      '4 agent runs',
    );
    const trigger = historyTrigger(fixture.nativeElement);

    expect(trigger.dataset['runCount']).toBe('4');
    expect(trigger.textContent?.trim()).toBe('4 agent runs');
    expect(trigger.getAttribute('aria-label')).toBe('4 agent runs, open run history');
    expect(trigger.getAttribute('aria-haspopup')).toBe('dialog');
  });
});

function createFixture(runs: RunRecord[], runCount: number, countLabel: string) {
  const fixture = TestBed.createComponent(PipelineRunHistoryComponent);
  fixture.componentRef.setInput('job', taskInfo());
  fixture.componentRef.setInput('runs', runs);
  fixture.componentRef.setInput('runCount', runCount);
  fixture.componentRef.setInput('countLabel', countLabel);
  fixture.detectChanges();
  return fixture;
}

function historyTrigger(root: HTMLElement): HTMLButtonElement {
  return root.querySelector('[data-testid="overview-pipeline-agent-runs"]') as HTMLButtonElement;
}

function taskInfo(): TaskInfo {
  return {
    id: 'task-1',
    taskKey: 'AGT-2062',
    title: 'Pipeline workbench',
    state: '3-progress',
    order: 0,
    agent: 'codex',
    createdAt: '2026-07-10T10:00:00Z',
    watchPath: 'C:\\watch',
    projectName: 'agent-taskboard',
    folderPath: 'C:\\watch\\3-progress\\task-1',
    lastActivity: '2026-07-10T10:00:00Z',
    sessionName: null,
    model: null,
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
  } as TaskInfo;
}

function runRecord(index: number): RunRecord {
  return {
    index,
    intent: index === 1 ? 'start' : 'continue',
    status: 'completed',
    userFollowup: index === 1 ? null : 'Continue with the review feedback.',
    durationSeconds: 30,
    startedAt: `2026-07-10T10:0${index}:00Z`,
    endedAt: `2026-07-10T10:0${index}:30Z`,
    cli: 'codex',
    exitCode: 0,
    inputSessionId: null,
    capturedSessionId: null,
    resumed: index > 1,
    reason: 'completed',
    lineStart: index,
    lineEnd: index + 1,
    headShaBefore: null,
    headShaAfter: null,
    contextRef: null,
    tokenSummary: null,
  };
}
