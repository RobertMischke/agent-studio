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

  it('attributes a closed build-profile gate to the project, not the runner', async () => {
    // AGT-2677: reading this as a runner verdict is what sent the operator to
    // restart hosts while the fix was a project setting all along.
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
        code: 'build-profile-gate',
        runnerId: 'agent-runner-01',
        runnerName: 'agent-runner-01',
        reason: 'build profile declared but not yet validated',
        rejectedAtUtc: '2026-08-23T21:00:00Z',
      },
    });
    fixture.detectChanges();

    const alert = fixture.nativeElement.querySelector(
      '[data-testid="remote-dispatch-rejection"]',
    ) as HTMLElement;
    expect(alert.textContent).toContain('Project build profile not validated:');
    expect(alert.textContent).not.toContain('Runner agent-runner-01 rejected');
    expect(alert.textContent).toContain('not yet validated');
  });
});
