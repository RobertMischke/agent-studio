import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../../models/task.model';
import { RemoteHostsService, type RemoteHost } from '../../../remote-hosts';
import { TaskCardProviderAuthWaitComponent } from './task-card-provider-auth-wait.component';
import { taskCardNow } from '../task-card/task-card-clock';

describe('TaskCardProviderAuthWaitComponent', () => {
  afterEach(() => taskCardNow.set(Date.now()));

  it('renders the provider probe wait reason on a queued Ready card', () => {
    const hosts = signal<RemoteHost[]>([host()]);
    TestBed.configureTestingModule({
      imports: [TaskCardProviderAuthWaitComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: RemoteHostsService, useValue: { hosts } },
      ],
    });
    const fixture = TestBed.createComponent(TaskCardProviderAuthWaitComponent);
    fixture.componentRef.setInput('task', {
      state: '2-ready',
      cliType: 'claude',
      executionLocation: { state: 'queued-remote', configuredRunnerId: 'runner-a' },
    } as TaskInfo);
    fixture.detectChanges();

    const wait: HTMLElement = fixture.nativeElement.querySelector(
      '[data-testid="task-card-provider-auth-wait"]',
    );
    expect(wait.textContent).toContain('Waiting for Claude Code sign-in on agent-runner-01');
    expect(wait.dataset['provider']).toBe('claude');
  });

  it('keeps the existing quota wait visible for an in-progress task', () => {
    const hosts = signal<RemoteHost[]>([]);
    TestBed.configureTestingModule({
      imports: [TaskCardProviderAuthWaitComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: RemoteHostsService, useValue: { hosts } },
      ],
    });
    const fixture = TestBed.createComponent(TaskCardProviderAuthWaitComponent);
    fixture.componentRef.setInput('task', {
      state: '3-progress',
      quotaWait: {
        cliType: 'codex', startedAt: '2026-08-04T11:00:00Z',
        resetAt: '2026-08-04T11:12:00Z', thresholdMinutes: 30,
        reason: 'Confirmed reset',
      },
    } as TaskInfo);
    taskCardNow.set(Date.parse('2026-08-04T11:00:30Z'));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="task-card-quota-wait"]')?.textContent)
      .toContain('12 min remaining');
  });
});

function host(): RemoteHost {
  return {
    id: 'runner-a', clientId: 'runner-a', name: 'agent-runner-01', role: 'remote',
    address: null, status: 'online', os: 'Linux', lastHeartbeatAt: new Date().toISOString(),
    uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
    capabilityHealth: [
      {
        key: 'cli-execution:claude', category: 'cli-execution', advertisedStatus: 'ready',
        healthState: 'healthy', advertisedAt: new Date().toISOString(),
        freshUntil: new Date(Date.now() + 60_000).toISOString(), isFresh: true,
        consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
      },
      {
        key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: 'unavailable',
        healthState: 'healthy', detail: 'Not logged in', advertisedAt: new Date().toISOString(),
        freshUntil: new Date(Date.now() + 60_000).toISOString(), isFresh: true,
        consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
      },
    ],
  };
}
