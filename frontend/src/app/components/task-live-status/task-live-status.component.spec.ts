import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it, beforeEach } from 'vitest';
import type { TaskInfo } from '../../models/task.model';
import { TaskLiveStatusComponent } from './task-live-status.component';

function task(overrides: Partial<TaskInfo> = {}): TaskInfo {
  const now = Date.now();
  return {
    id: 'AGT-2315',
    taskKey: 'demo::AGT-2315',
    title: 'Show live work',
    state: '4-auto-review',
    order: 1,
    agent: 'codex',
    createdAt: new Date(now - 60_000).toISOString(),
    watchPath: '/workspace',
    projectName: 'demo',
    folderPath: '/workspace/4-auto-review/AGT-2315',
    lastActivity: new Date(now - 20_000).toISOString(),
    sessionName: null,
    model: 'gpt-5.4-mini',
    cliType: 'codex',
    useOwnSession: null,
    lastUsage: null,
    execution: null,
    commit: null,
    ...overrides,
  };
}

describe('TaskLiveStatusComponent', () => {
  let fixture: ComponentFixture<TaskLiveStatusComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TaskLiveStatusComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(TaskLiveStatusComponent);
  });

  it('renders the active step with duration, host, model, CLI and next steps', () => {
    const now = Date.now();
    fixture.componentRef.setInput('task', task({
      executionLocation: {
        state: 'remote-running',
        executionKind: 'remote',
        hostDisplayName: 'agent-runner-01',
        connectionState: 'connected',
        leaseState: 'active',
        trustReason: 'lease',
      },
      liveStatus: {
        attempt: 4,
        activeStep: {
          stepId: 'aspect-tests-and-evidence',
          displayName: 'Tests and evidence',
          kind: 'aspect',
          startedAt: new Date(now - 40_000).toISOString(),
          model: 'gpt-5.4-mini',
          cliType: 'codex',
        },
        nextSteps: [
          { stepId: 'post-code-review-grade', displayName: 'Code-review quality grade' },
          { stepId: 'post-build-test-gate', displayName: 'Build/test gate' },
          { stepId: 'post-integrate-merge', displayName: 'Integrate merge' },
        ],
        latestEventAt: new Date(now - 40_000).toISOString(),
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('active');
    expect(root.textContent).toContain('Review aspect · Tests and evidence');
    expect(root.textContent).toMatch(/running (?:39|40)s/);
    expect(root.textContent).toContain('agent-runner-01');
    expect(root.textContent).toContain('gpt-5.4-mini');
    expect(root.textContent).toContain('via Codex');
    expect(root.textContent).toContain('Code-review quality grade → Build/test gate → Integrate merge');
  });

  it('shows an honest queue position when no step is active', () => {
    fixture.componentRef.setInput('task', task({
      liveStatus: {
        attempt: 2,
        activeStep: null,
        nextSteps: [{ stepId: 'aspect-requirement-fit', displayName: 'Requirement fit' }],
        queue: { kind: 'review', position: 3 },
        latestEventAt: new Date().toISOString(),
      },
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Waiting for review slot · position 3');
    expect(fixture.nativeElement.textContent).toContain('Next');
    expect(fixture.nativeElement.textContent).toContain('Requirement fit');
  });

  it('calls out a possible hang after ten minutes without a step or queue', () => {
    const old = new Date(Date.now() - 12 * 60_000).toISOString();
    fixture.componentRef.setInput('task', task({
      lastActivity: old,
      executionLocation: null,
      liveStatus: {
        attempt: 7,
        activeStep: null,
        nextSteps: [{ stepId: 'post-orchestrator-decision', displayName: 'Final review decision' }],
        queue: null,
        latestEventAt: old,
      },
    }));
    fixture.detectChanges();

    const root = fixture.nativeElement.querySelector('[data-testid="task-live-status"]') as HTMLElement;
    expect(root.dataset['liveTone']).toBe('stalled');
    expect(root.textContent).toMatch(/No activity for (?:11m59s|12m00s) · possible hang/);
  });
});
