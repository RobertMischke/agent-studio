import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { RemoteQueueStarvationBannerComponent } from './remote-queue-starvation-banner';

describe('RemoteQueueStarvationBannerComponent', () => {
  it('names a provider limit instead of reporting queue starvation', async () => {
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
      signal: 'limited',
      waitingTaskCount: 1,
      availableSlots: 4,
      thresholdMinutes: 30,
      claimProgressStalled: true,
      lastSuccessfulClaimAt: null,
      hasRejections: false,
      oldestEnteredLaneAt: null,
      observedAt: '2026-08-24T00:00:00Z',
      items: [],
      providerLimits: [{
        cliType: 'claude',
        observedAt: '2026-08-23T22:00:00Z',
        limitedUntil: '2026-08-24T00:20:00Z',
        reason: 'claude: limited until reset',
        resetTimeReported: true,
      }],
      pickupPauses: [],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('Claude claims limited until');
    expect(banner.textContent).toContain('resume automatically');
    expect(banner.textContent).toContain('Codex and other CLI claims remain eligible');
    expect(banner.textContent).not.toContain('waiting despite free Runner capacity');
    fixture.destroy();
    http.verify();
  });

  it('shows the reason for an infrastructure-breaker pickup pause', async () => {
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
      signal: 'paused',
      waitingTaskCount: 0,
      availableSlots: 0,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: null,
      hasRejections: false,
      oldestEnteredLaneAt: null,
      observedAt: '2026-08-24T00:00:00Z',
      items: [],
      providerLimits: [],
      pickupPauses: [{
        projectName: 'Demo',
        reason: 'pickup paused: infra breaker, 3 failures cliType=claude at 2026-08-23T22:10:00Z',
        pausedAt: '2026-08-23T22:10:00Z',
        autoResumeAt: null,
      }],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('Pickup paused: infra breaker');
    expect(banner.textContent).toContain('3 failures cliType=claude');
    expect(banner.textContent).toContain('requires an operator resume');
    fixture.destroy();
    http.verify();
  });

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
});
