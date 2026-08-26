import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { RemoteQueueStarvationBannerComponent } from './remote-queue-starvation-banner';

describe('RemoteQueueStarvationBannerComponent', () => {
  it('names a provider limit and its automatic recovery without calling it stalled', async () => {
    await TestBed.configureTestingModule({
      imports: [RemoteQueueStarvationBannerComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(RemoteQueueStarvationBannerComponent);
    fixture.componentRef.setInput('projects', ['Agent Studio']);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/runner/queue-starvation').flush({
      active: true,
      waitingTaskCount: 2,
      availableSlots: 3,
      thresholdMinutes: 30,
      claimProgressStalled: true,
      lastSuccessfulClaimAt: '2026-08-23T20:00:00Z',
      hasRejections: true,
      buildProfileGateBlockedTaskCount: 0,
      providerLimitedTaskCount: 2,
      state: 'limited',
      providerLimitReason: "Required capability 'provider-auth:claude' is advertised as limited. claude: limited until 2026-08-24T00:20:00Z",
      oldestEnteredLaneAt: '2026-08-23T20:01:00Z',
      observedAt: '2026-08-23T22:00:00Z',
      items: [{
        taskKey: 'AGT-1',
        taskId: 'one',
        projectName: 'Agent Studio',
        title: 'One',
        enteredLaneAt: '2026-08-23T20:01:00Z',
        blockReasonCode: 'provider-limited',
      }],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('Claude claims paused: provider limit');
    expect(banner.textContent).toContain('1 card is waiting without escalation');
    expect(banner.textContent).toContain('Other CLI types remain eligible');
    expect(banner.textContent).toContain('probed automatically');
    expect(banner.textContent).not.toContain('stalled');
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
