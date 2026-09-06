import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';
import type { CodexSignInResponse, ProviderAuthBadge } from '../../models/provider-auth.model';
import { ProviderAuthStatusService } from '../../services/provider-auth-status.service';
import { CodexSignInDialogComponent } from './codex-sign-in-dialog';

const PENDING: CodexSignInResponse = {
  handle: 'session-01', runnerId: 'runner-01', host: 'agent@runner-01', state: 'pending',
  detail: 'Complete sign-in in the browser.', requestedAt: '2026-09-06T12:00:00Z',
  expiresAt: '2026-09-06T12:15:00Z', verificationUrl: 'https://auth.openai.com/codex/device',
  userCode: 'ABCD-EFGH', completedAt: null,
};

describe('CodexSignInDialogComponent', () => {
  let auth: StubProviderAuthStatus;

  beforeEach(() => {
    auth = new StubProviderAuthStatus();
    TestBed.configureTestingModule({
      imports: [CodexSignInDialogComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ProviderAuthStatusService, useValue: auth },
      ],
    });
  });

  it('shows the URL and copyable code while pending, then offers a retry after failure', () => {
    const fixture = mount();
    fixture.componentInstance.start();
    expect(fixture.componentInstance.phase()).toBe('starting');

    auth.started.next(PENDING);
    fixture.detectChanges();
    expect(fixture.componentInstance.phase()).toBe('pending');
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-url"]')?.textContent)
      .toContain(PENDING.verificationUrl);
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-code"]')?.textContent)
      .toContain(PENDING.userCode);

    auth.watched.next({ ...PENDING, state: 'failed', detail: 'The device code expired.' });
    fixture.detectChanges();
    expect(fixture.componentInstance.phase()).toBe('failed');
    expect(fixture.nativeElement.querySelector('[data-testid="codex-sign-in-start"]')?.textContent)
      .toContain('Try again');
    expect(fixture.nativeElement.textContent).toContain('The device code expired.');
  });

  it('waits for a fresh OK runner capability before closing after host verification', () => {
    const fixture = mount();
    let completed = 0;
    fixture.componentInstance.completed.subscribe(() => completed++);
    fixture.componentInstance.start();
    auth.started.next(PENDING);
    auth.watched.next({ ...PENDING, state: 'completed', verificationUrl: null, userCode: null });
    expect(fixture.componentInstance.phase()).toBe('verifying');
    expect(completed).toBe(0);

    auth.probed.next({ state: 'ok' } as ProviderAuthBadge);
    expect(completed).toBe(1);
  });

  function mount() {
    const fixture = TestBed.createComponent(CodexSignInDialogComponent);
    fixture.componentRef.setInput('runnerId', 'runner-01');
    fixture.componentRef.setInput('hostName', 'runner-berlin');
    fixture.componentRef.setInput('initialSshTarget', 'agent@runner-01');
    fixture.componentRef.setInput('aliases', ['runner-01', 'runner-berlin']);
    fixture.detectChanges();
    return fixture;
  }
});

class StubProviderAuthStatus {
  readonly statuses = signal<ProviderAuthBadge[]>([]);
  readonly started = new Subject<CodexSignInResponse>();
  readonly watched = new Subject<CodexSignInResponse>();
  readonly probed = new Subject<ProviderAuthBadge>();

  startCodexSignIn() { return this.started.asObservable(); }
  watchCodexSignIn() { return this.watched.asObservable(); }
  waitForFreshProbe() { return this.probed.asObservable(); }
}
