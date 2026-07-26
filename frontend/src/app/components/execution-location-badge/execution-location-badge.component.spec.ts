import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import type { TaskExecutionLocation, TaskExecutionState } from '../../models/task.model';
import { ExecutionLocationBadgeComponent } from './execution-location-badge.component';

function execution(state: TaskExecutionState, overrides: Partial<TaskExecutionLocation> = {}): TaskExecutionLocation {
  const remote = state.startsWith('remote') || state === 'queued-remote';
  return {
    state,
    executionKind: remote ? 'remote' : state === 'local-running' ? 'local' : 'none',
    runnerId: remote ? 'agent-runner-01' : 'stable@local',
    clientId: remote ? 'agent-runner-01' : 'stable@local',
    hostDisplayName: remote ? 'Runner 01' : 'Local',
    configuredRunnerId: 'agent-runner-01',
    startedAt: '2026-07-12T10:00:00Z',
    lastHeartbeat: '2026-07-12T10:02:00Z',
    lastActivityAt: '2026-07-12T10:02:03Z',
    processId: 4321,
    sessionId: 'safe-session',
    branch: 'task/AGT-2158',
    worktreePath: '/worktrees/AGT-2158',
    connectionState: state === 'remote-disconnected' ? 'disconnected' : 'connected',
    leaseState: remote ? 'active' : 'local-process',
    trustReason: 'A fenced runtime claim owns this execution.',
    ...overrides,
  };
}

describe('ExecutionLocationBadgeComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ExecutionLocationBadgeComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  for (const [state, label] of [
    ['local-running', 'Local'],
    ['remote-running', 'Host · agent-runner-01'],
    ['remote-disconnected', 'Host · agent-runner-01'],
    ['queued-remote', 'Host · agent-runner-01'],
    ['recovering', 'Recovering'],
  ] as const) {
    it(`renders ${state}`, () => {
      const fixture = TestBed.createComponent(ExecutionLocationBadgeComponent);
      fixture.componentRef.setInput('execution', execution(state));
      fixture.detectChanges();
      const badge = fixture.nativeElement.querySelector('[data-testid="execution-location-badge"]') as HTMLElement;
      expect(badge.textContent).toContain(label);
      expect(badge.dataset['executionState']).toBe(state);
    });
  }

  it('hides no-active-execution', () => {
    const fixture = TestBed.createComponent(ExecutionLocationBadgeComponent);
    fixture.componentRef.setInput('execution', execution('no-active-execution'));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="execution-location-badge"]')).toBeNull();
  });

  it('includes routing, activity, provenance, identity, and trust details', () => {
    const fixture = TestBed.createComponent(ExecutionLocationBadgeComponent);
    fixture.componentRef.setInput('execution', execution('remote-running'));
    fixture.detectChanges();
    const tooltip = fixture.componentInstance.tooltip();
    expect(tooltip).toContain('Actual host: Host · agent-runner-01');
    expect(tooltip).toContain('Configured host: Host · agent-runner-01');
    expect(tooltip).toContain('Last heartbeat:');
    expect(tooltip).toContain('Branch: task/AGT-2158');
    expect(tooltip).toContain('Worktree: /worktrees/AGT-2158');
    expect(tooltip).toContain('Trusted because: A fenced runtime claim owns this execution.');
  });

  it('keeps historical disconnected attribution quiet', () => {
    const fixture = TestBed.createComponent(ExecutionLocationBadgeComponent);
    fixture.componentRef.setInput('execution', execution('remote-disconnected', { historical: true }));
    fixture.detectChanges();
    const badge = fixture.nativeElement.querySelector('[data-testid="execution-location-badge"]') as HTMLElement;
    expect(badge.classList.contains('execution-location--history')).toBe(true);
    expect(badge.classList.contains('execution-location--acute')).toBe(false);
  });
});
