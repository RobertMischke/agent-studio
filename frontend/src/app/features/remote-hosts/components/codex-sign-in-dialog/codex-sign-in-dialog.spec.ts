import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TaskServerRunnerCapabilitySnapshot } from '../../models/remote-host.model';
import { CodexSignInDialogComponent } from './codex-sign-in-dialog';

describe('CodexSignInDialogComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({
      imports: [CodexSignInDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  it('shows the live URL and code, polls completion, then closes after a fresh OK probe', async () => {
    const fixture = TestBed.createComponent(CodexSignInDialogComponent);
    fixture.componentRef.setInput('hostId', 'agent-runner-01');
    fixture.componentRef.setInput('hostName', 'runner-berlin');
    fixture.componentRef.setInput('sshTarget', 'agent@runner-01');
    fixture.componentRef.setInput('baselineAdvertisedAt', '2026-09-06T11:55:00Z');
    let completed = 0;
    fixture.componentInstance.signedIn.subscribe(() => completed++);
    fixture.detectChanges();

    expect(fixture.componentInstance.phase()).toBe('starting');
    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in').flush({
      handle: 'session-1', host: 'agent-runner-01', provider: 'codex', state: 'pending',
      verificationUrl: 'https://auth.openai.com/codex/device', userCode: 'ABCD-EFGH',
      expiresAt: '2026-09-06T12:15:00Z',
    });
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(fixture.componentInstance.phase()).toBe('pending');
    expect(el.querySelector('[data-testid="codex-sign-in-url"]')?.textContent).toContain('auth.openai.com');
    expect(el.querySelector('[data-testid="codex-sign-in-code"]')?.textContent).toContain('ABCD-EFGH');

    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in/session-1').flush({
      handle: 'session-1', host: 'agent-runner-01', provider: 'codex', state: 'pending',
      detail: 'Waiting for browser approval.', expiresAt: '2026-09-06T12:15:00Z', completedAt: null,
    });
    await vi.advanceTimersByTimeAsync(2_000);
    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in/session-1').flush({
      handle: 'session-1', host: 'agent-runner-01', provider: 'codex', state: 'completed',
      detail: 'Codex sign-in completed.', expiresAt: '2026-09-06T12:15:00Z', completedAt: '2026-09-06T12:01:00Z',
    });
    expect(fixture.componentInstance.phase()).toBe('verifying');

    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/v1/management/remote-hosts').flush([readySnapshot()]);
    expect(completed).toBe(1);
  });

  it('renders a retry state when the host cannot start device auth', () => {
    const fixture = TestBed.createComponent(CodexSignInDialogComponent);
    fixture.componentRef.setInput('hostId', 'agent-runner-01');
    fixture.componentRef.setInput('hostName', 'runner-berlin');
    fixture.componentRef.setInput('sshTarget', 'agent@runner-01');
    fixture.detectChanges();

    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in').flush(
      { message: 'SSH device-auth process could not be started.' },
      { status: 502, statusText: 'Bad Gateway' },
    );
    fixture.detectChanges();

    expect(fixture.componentInstance.phase()).toBe('failed');
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-retry"]')).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('SSH device-auth process could not be started.');
  });
});

function readySnapshot(): TaskServerRunnerCapabilitySnapshot {
  return {
    runnerId: 'agent-runner-01', name: 'runner-berlin', hostId: 'host-berlin', instanceId: 'coding-2',
    runnerVersion: '1.2.0', protocolVersion: 2, status: 'active', registeredAt: '2026-09-06T12:01:01Z',
    lastSeenAt: new Date().toISOString(), hostAdmission: { hostId: 'host-berlin', admissionState: 'open' },
    capabilities: [{
      key: 'cli-execution:codex', category: 'cli-execution', advertisedStatus: 'ready', healthState: 'healthy',
      advertisedAt: '2026-09-06T12:01:02Z', freshUntil: new Date(Date.now() + 120_000).toISOString(),
      isFresh: true, consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
    }, {
      key: 'provider-auth:codex', category: 'provider-auth', advertisedStatus: 'ready', healthState: 'healthy',
      advertisedAt: '2026-09-06T12:01:02Z', freshUntil: new Date(Date.now() + 120_000).toISOString(),
      isFresh: true, consecutiveFailures: 0, signal: 'ok', detail: 'Logged in', affectedClaims: [], recoveryHistory: [],
    }],
  };
}
