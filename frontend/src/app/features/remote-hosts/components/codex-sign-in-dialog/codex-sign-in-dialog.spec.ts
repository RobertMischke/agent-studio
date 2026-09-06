import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { TaskServerRunnerCapabilitySnapshot } from '../../models/remote-host.model';
import { CodexSignInDialogService } from '../../services/codex-sign-in-dialog.service';
import { CodexSignInDialogComponent } from './codex-sign-in-dialog';

describe('CodexSignInDialogComponent', () => {
  let http: HttpTestingController;
  let dialog: CodexSignInDialogService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CodexSignInDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    dialog = TestBed.inject(CodexSignInDialogService);
  });

  afterEach(() => {
    dialog.close();
    http.verify();
    document.body.querySelectorAll('.studio-overlay-root').forEach(element => element.remove());
  });

  it('shows the live URL and copyable code while device auth is pending', () => {
    dialog.open(target());
    const fixture = TestBed.createComponent(CodexSignInDialogComponent);
    fixture.detectChanges();
    (document.body.querySelector('[data-testid="codex-sign-in-start"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/management/remote-hosts/runner-01/codex-sign-in').flush({
      handle: 'handle-1', hostId: 'runner-01', provider: 'codex', state: 'pending',
      verificationUrl: 'https://auth.openai.com/codex/device', userCode: 'ABCD-EFGH',
      expiresAt: '2026-09-06T12:15:00Z',
    });
    fixture.detectChanges();

    const url = document.body.querySelector('[data-testid="codex-sign-in-url"]') as HTMLAnchorElement;
    const code = document.body.querySelector('[data-testid="codex-sign-in-code"]') as HTMLButtonElement;
    expect(url.href).toBe('https://auth.openai.com/codex/device');
    expect(code.textContent).toContain('ABCD-EFGH');
    expect(document.body.querySelector('[data-testid="codex-sign-in-status"]')?.getAttribute('data-state'))
      .toBe('pending');

    fixture.destroy();
  });

  it('renders a failed terminal state without exposing any credential value', () => {
    dialog.open(target());
    const fixture = TestBed.createComponent(CodexSignInDialogComponent);
    fixture.detectChanges();
    (document.body.querySelector('[data-testid="codex-sign-in-start"]') as HTMLButtonElement).click();
    http.expectOne('/api/v1/management/remote-hosts/runner-01/codex-sign-in').flush(
      { message: 'The remote Codex process exited before authentication.' },
      { status: 502, statusText: 'Bad Gateway' },
    );
    fixture.detectChanges();

    const status = document.body.querySelector('[data-testid="codex-sign-in-status"]');
    expect(status?.getAttribute('data-state')).toBe('failed');
    expect(status?.textContent).toContain('exited before authentication');
    expect(document.body.textContent).not.toContain('sk-secret-fixture');
    expect(document.body.querySelector('[data-testid="codex-sign-in-start"]')?.textContent).toContain('Try again');

    fixture.destroy();
  });
});

function target() {
  return {
    hostId: 'runner-01',
    hostName: 'runner-berlin',
    sshTarget: 'agent@runner-berlin',
    aliases: ['runner-01', 'runner-berlin'],
  };
}

export function codexSnapshot(advertisedAt: string): TaskServerRunnerCapabilitySnapshot {
  return {
    runnerId: 'runner-01', name: 'runner-berlin', hostId: 'host-01', instanceId: 'coding-1',
    runnerVersion: '1.2.0', protocolVersion: 2, status: 'active', registeredAt: advertisedAt,
    lastSeenAt: advertisedAt, hostAdmission: { hostId: 'host-01', admissionState: 'open' },
    capabilities: [{
      key: 'provider-auth:codex', category: 'provider-auth', advertisedStatus: 'ready',
      healthState: 'healthy', advertisedAt, freshUntil: '2099-01-01T00:00:00Z', isFresh: true,
      consecutiveFailures: 0, signal: 'ok', affectedClaims: [], recoveryHistory: [],
    }],
  };
}
