import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { RemoteQueueStarvationBannerComponent } from './remote-queue-starvation-banner';

describe('RemoteQueueStarvationBannerComponent', () => {
  it('describes stalled claim progress without inventing a rejection', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteQueueStarvationBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteQueueStarvationBannerComponent);
    fixture.componentRef.setInput('projects', ['Demo']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      active: true,
      waitingTaskCount: 2,
      availableSlots: 8,
      thresholdMinutes: 30,
      claimProgressStalled: true,
      lastSuccessfulClaimAt: '2026-08-08T09:29:00Z',
      hasRejections: false,
      oldestEnteredLaneAt: '2026-08-08T09:00:00Z',
      observedAt: '2026-08-08T10:00:00Z',
      items: [
        { taskKey: 'AGT-1', taskId: 'one', projectName: 'Demo', title: 'One', enteredLaneAt: '2026-08-08T09:00:00Z' },
        { taskKey: 'OTH-1', taskId: 'other', projectName: 'Other', title: 'Other', enteredLaneAt: '2026-08-08T09:00:00Z' },
      ],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('1 task is waiting despite free Runner capacity');
    expect(banner.textContent).toContain('8 slots are available');
    expect(banner.textContent).toContain('No successful claim has been recorded for at least 30 minutes');
    expect(banner.textContent).not.toContain('rejection');
    fixture.destroy();
    http.verify();
  });

  it('shows rejection guidance only when a visible task has rejection evidence', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteQueueStarvationBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteQueueStarvationBannerComponent);
    fixture.componentRef.setInput('projects', ['Demo']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      active: true,
      waitingTaskCount: 1,
      availableSlots: 1,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: '2026-08-08T09:59:00Z',
      hasRejections: true,
      oldestEnteredLaneAt: '2026-08-08T09:56:00Z',
      observedAt: '2026-08-08T10:00:00Z',
      items: [{
        taskKey: 'AGT-1',
        taskId: 'one',
        projectName: 'Demo',
        title: 'One',
        enteredLaneAt: '2026-08-08T09:56:00Z',
        lastRejection: {
          code: 'dispatch-transition-failed',
          runnerId: 'runner-01',
          runnerName: 'Runner 01',
          reason: 'claim move refused',
          rejectedAtUtc: '2026-08-08T09:57:00Z',
        },
      }],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('1 task is waiting despite free Runner capacity');
    expect(banner.textContent).toContain('Open a task to inspect its latest rejection');
    expect(banner.textContent).not.toContain('No successful claim');
    fixture.destroy();
    http.verify();
  });

  it('names the build-profile gate and counts only the cards it can see', async () => {
    // AGT-2677: a shut build-profile gate is a configuration problem, so it
    // outranks the generic "inspect the rejection" wording, and the count
    // stays the sum of the visible rows.
    await TestBed.configureTestingModule({
      imports: [RemoteQueueStarvationBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteQueueStarvationBannerComponent);
    fixture.componentRef.setInput('projects', ['Quality Studio']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    const gated = (taskKey: string, projectName: string) => ({
      taskKey,
      taskId: taskKey,
      projectName,
      title: taskKey,
      enteredLaneAt: '2026-08-18T08:00:00Z',
      buildProfileGateReason: 'build profile declared but not yet validated (no green dry-run)',
      lastRejection: {
        code: 'build-profile-gate',
        runnerId: 'runner-01',
        runnerName: 'agent-runner-01',
        reason: 'project build profile blocks auto-pickup',
        rejectedAtUtc: '2026-08-18T08:01:00Z',
      },
    });
    http.expectOne('/api/runner/queue-starvation').flush({
      active: true,
      waitingTaskCount: 3,
      availableSlots: 4,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: '2026-08-23T09:59:00Z',
      hasRejections: true,
      buildProfileBlockedCount: 3,
      buildProfileBlockedProjects: ['Other Project', 'Quality Studio'],
      oldestEnteredLaneAt: '2026-08-18T08:00:00Z',
      observedAt: '2026-08-23T10:00:00Z',
      items: [
        gated('QS-1', 'Quality Studio'),
        gated('QS-2', 'Quality Studio'),
        gated('OTH-1', 'Other Project'),
      ],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('2 tasks are waiting despite free Runner capacity');
    const gate = banner.querySelector('[data-testid="notice-bar-build-profile-gate"]') as HTMLElement;
    expect(gate.textContent).toContain('2 ready cards are not claimable: build profile not validated');
    expect(gate.textContent).toContain('(Quality Studio)');
    expect(gate.textContent).not.toContain('Other Project');
    expect(banner.textContent).not.toContain('Open a task to inspect its latest rejection');
    fixture.destroy();
    http.verify();
  });

  it('keeps the generic wording when the gate is open on every visible card', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteQueueStarvationBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteQueueStarvationBannerComponent);
    fixture.componentRef.setInput('projects', ['Demo']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      active: true,
      waitingTaskCount: 2,
      availableSlots: 1,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: '2026-08-23T09:59:00Z',
      hasRejections: true,
      // The server saw a gated card in a project this operator cannot see.
      buildProfileBlockedCount: 1,
      buildProfileBlockedProjects: ['Hidden'],
      oldestEnteredLaneAt: '2026-08-23T09:56:00Z',
      observedAt: '2026-08-23T10:00:00Z',
      items: [{
        taskKey: 'AGT-1',
        taskId: 'one',
        projectName: 'Demo',
        title: 'One',
        enteredLaneAt: '2026-08-23T09:56:00Z',
        buildProfileGateReason: null,
        lastRejection: {
          code: 'dispatch-transition-failed',
          runnerId: 'runner-01',
          runnerName: 'Runner 01',
          reason: 'claim move refused',
          rejectedAtUtc: '2026-08-23T09:57:00Z',
        },
      }],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.querySelector('[data-testid="notice-bar-build-profile-gate"]')).toBeNull();
    expect(banner.textContent).toContain('Open a task to inspect its latest rejection');
    fixture.destroy();
    http.verify();
  });
});
