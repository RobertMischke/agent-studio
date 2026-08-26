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
    http.expectOne('/api/runner/status').flush({ projects: {}, capabilities: [] });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('1 task is stalled despite free Runner capacity');
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
    http.expectOne('/api/runner/status').flush({ projects: {}, capabilities: [] });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('1 task is stalled despite free Runner capacity');
    expect(banner.textContent).toContain('Open a task to inspect its latest rejection');
    expect(banner.textContent).not.toContain('No successful claim');
    fixture.destroy();
    http.verify();
  });

  it('shows the build-profile gate as the primary starvation reason', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteQueueStarvationBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteQueueStarvationBannerComponent);
    fixture.componentRef.setInput('projects', ['Quality Studio']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      active: true,
      waitingTaskCount: 25,
      availableSlots: 4,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: '2026-08-23T08:00:00Z',
      hasRejections: true,
      buildProfileGateBlockedTaskCount: 25,
      oldestEnteredLaneAt: '2026-08-18T08:00:00Z',
      observedAt: '2026-08-23T08:01:00Z',
      items: Array.from({ length: 25 }, (_, index) => ({
        taskKey: `QS-${index + 1}`,
        taskId: `quality-${index + 1}`,
        projectName: 'Quality Studio',
        title: `Quality task ${index + 1}`,
        enteredLaneAt: '2026-08-18T08:00:00Z',
        blockReasonCode: 'build-profile-gate',
        blockReason: 'build profile revalidation pending; grace runs exhausted',
      })),
    });
    http.expectOne('/api/runner/status').flush({ projects: {}, capabilities: [] });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('25 ready cards not claimable: build profile not validated');
    expect(banner.textContent).toContain('Revalidate the project build profile');
    expect(banner.textContent).toContain('4 Runner slots are available');
    fixture.destroy();
    http.verify();
  });

  it('names a limited CLI and excludes its cards from the stalled count', async () => {
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
      availableSlots: 2,
      thresholdMinutes: 30,
      claimProgressStalled: true,
      lastSuccessfulClaimAt: '2026-08-23T20:00:00Z',
      hasRejections: false,
      buildProfileGateBlockedTaskCount: 0,
      oldestEnteredLaneAt: '2026-08-23T20:00:00Z',
      observedAt: '2026-08-23T22:05:00Z',
      items: [
        { taskKey: 'AGT-1', taskId: 'claude', projectName: 'Demo', title: 'Claude card', cliType: 'claude', enteredLaneAt: '2026-08-23T20:00:00Z' },
        { taskKey: 'AGT-2', taskId: 'codex', projectName: 'Demo', title: 'Codex card', cliType: 'codex', enteredLaneAt: '2026-08-23T20:01:00Z' },
      ],
    });
    http.expectOne('/api/runner/status').flush({
      projects: {},
      capabilities: [{
        cliType: 'claude',
        status: 'limited',
        detectedAt: '2026-08-23T22:00:00Z',
        limitedUntil: '2026-08-23T22:20:00Z',
        reason: "You've hit your session limit",
        probeInFlight: false,
        consecutiveLimits: 1,
      }],
    });
    fixture.detectChanges();

    const limit = fixture.nativeElement.querySelector(
      '[data-testid="provider-limit-banner"]',
    ) as HTMLElement;
    expect(limit.textContent).toContain('Claude: limited until');
    expect(limit.textContent).toContain('Other CLIs remain eligible');
    const stalled = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(stalled.textContent).toContain('1 task is stalled');
    fixture.destroy();
    http.verify();
  });

  it('shows the circuit-breaker halt reason from runner status', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteQueueStarvationBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteQueueStarvationBannerComponent);
    fixture.componentRef.setInput('projects', ['Agent Studio']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      active: false,
      waitingTaskCount: 0,
      availableSlots: 2,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: null,
      hasRejections: false,
      buildProfileGateBlockedTaskCount: 0,
      oldestEnteredLaneAt: null,
      observedAt: '2026-08-24T00:00:00Z',
      items: [],
    });
    http.expectOne('/api/runner/status').flush({
      projects: {
        'Agent Studio': {
          projectName: 'Agent Studio',
          mode: 'manual',
          activeJobId: null,
          activeExecution: null,
          queuedJobIds: [],
          modeSource: 'circuit-breaker',
          modeChangedAt: '2026-08-23T22:07:00Z',
          modeReason: 'pickup paused: infra breaker (circuit-breaker), 3 failures cliType=claude',
          breakerFailureCount: 3,
          breakerCliType: 'claude',
        },
      },
      capabilities: [],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="infra-breaker-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('Pickup paused: infra breaker, 3 failures cliType=claude at');
    expect(banner.textContent).toContain('Agent Studio');
    fixture.destroy();
    http.verify();
  });
});
