import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RemoteHost, TaskServerRunnerCapabilitySnapshot } from '../../models/remote-host.model';
import { CodexSignInDialogComponent } from './codex-sign-in-dialog';

const HOST: RemoteHost = {
  id: 'agent-runner-01', name: 'Berlin runner', role: 'remote', address: 'ssh://agent@runner-01',
  clientId: 'agent-runner-01', status: 'online', os: 'Linux', lastHeartbeatAt: new Date().toISOString(),
  uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
};

describe('CodexSignInDialogComponent', () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  it('shows the live URL and copyable code, then renders a terminal failure', async () => {
    const fixture = mount();
    const http = TestBed.inject(HttpTestingController);
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-starting"]')).toBeTruthy();

    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in').flush(prompt());
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-url"]')?.textContent)
      .toContain('auth.openai.com/codex/device');
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-code"]')?.textContent)
      .toContain('ABCD-EFGH');

    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in/session-1')
      .flush({ ...status('failed'), detail: 'The operator declined the browser request.' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-failed"]')?.textContent)
      .toContain('declined');
    fixture.destroy();
  });

  it('closes through signedIn only after a fresh OK provider probe', async () => {
    const fixture = mount();
    const http = TestBed.inject(HttpTestingController);
    let completed = false;
    fixture.componentInstance.signedIn.subscribe(() => { completed = true; });

    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in').flush(prompt());
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/v1/management/remote-hosts/agent-runner-01/codex-sign-in/session-1')
      .flush(status('completed'));
    fixture.detectChanges();
    expect(fixture.componentInstance.phase()).toBe('verifying');
    expect(completed).toBe(false);

    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/v1/management/remote-hosts').flush([readySnapshot()]);
    expect(completed).toBe(true);
    fixture.destroy();
  });
});

function mount() {
  TestBed.configureTestingModule({
    imports: [CodexSignInDialogComponent],
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  });
  const fixture = TestBed.createComponent(CodexSignInDialogComponent);
  fixture.componentRef.setInput('host', HOST);
  fixture.detectChanges();
  return fixture;
}

function prompt() {
  return {
    handle: 'session-1', hostId: HOST.id, state: 'pending',
    verificationUrl: 'https://auth.openai.com/codex/device', userCode: 'ABCD-EFGH',
    startedAt: '2026-09-06T12:00:00Z', expiresAt: '2026-09-06T12:15:00Z',
  };
}

function status(state: 'completed' | 'failed') {
  return {
    handle: 'session-1', hostId: HOST.id, state,
    detail: 'Codex sign-in completed.', startedAt: '2026-09-06T12:00:00Z',
    expiresAt: '2026-09-06T12:15:00Z', completedAt: '2026-09-06T12:01:00Z',
    probeRefreshTriggered: true,
  };
}

function readySnapshot(): TaskServerRunnerCapabilitySnapshot {
  const now = new Date().toISOString();
  return {
    runnerId: HOST.id, name: HOST.name, hostId: HOST.id, instanceId: 'coding-1', runnerVersion: '1',
    protocolVersion: 3, status: 'active', registeredAt: now, lastSeenAt: now,
    hostAdmission: { hostId: HOST.id, admissionState: 'open' },
    capabilities: [{
      key: 'provider-auth:codex', category: 'provider-auth', advertisedStatus: 'ready', healthState: 'healthy',
      advertisedAt: now, freshUntil: new Date(Date.now() + 120_000).toISOString(), isFresh: true,
      consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [], signal: 'ok',
    }],
  };
}
