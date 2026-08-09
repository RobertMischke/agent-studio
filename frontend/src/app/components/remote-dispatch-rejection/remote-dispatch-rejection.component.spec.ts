import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { RemoteDispatchRejectionComponent } from './remote-dispatch-rejection.component';

describe('RemoteDispatchRejectionComponent', () => {
  it('shows the runner and reason without requiring a tooltip', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteDispatchRejectionComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteDispatchRejectionComponent);
    fixture.componentRef.setInput('execution', {
      state: 'queued-remote',
      executionKind: 'none',
      connectionState: 'queued',
      leaseState: 'none',
      trustReason: 'Queued for a remote Runner.',
      lastRejection: {
        code: 'repository-url-missing',
        runnerId: 'agent-runner-01',
        runnerName: 'agent-runner-01',
        reason: 'project has no repositoryUrl',
        rejectedAtUtc: '2026-08-08T10:00:00Z',
      },
    });
    fixture.detectChanges();

    const alert = fixture.nativeElement.querySelector(
      '[data-testid="remote-dispatch-rejection"]',
    ) as HTMLElement;
    expect(alert.textContent).toContain('Runner agent-runner-01 rejected:');
    expect(alert.textContent).toContain('project has no repositoryUrl');
  });
});
