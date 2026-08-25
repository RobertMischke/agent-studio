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

  it('shows a loud build-profile gate banner for ready cards excluded before claim', async () => {
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
      availableSlots: 0,
      thresholdMinutes: 30,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: null,
      hasRejections: true,
      buildProfileGateBlockedCount: 25,
      oldestEnteredLaneAt: '2026-08-18T09:00:00Z',
      observedAt: '2026-08-23T09:00:00Z',
      items: Array.from({ length: 25 }, (_, index) => ({
        taskKey: `QS-${index + 1}`,
        taskId: `task-${index + 1}`,
        projectName: 'Quality Studio',
        title: 'Blocked card',
        enteredLaneAt: '2026-08-18T09:00:00Z',
        buildProfileGateBlocked: true,
        lastRejection: {
          code: 'build-profile-gate',
          runnerId: 'build-profile-gate',
          runnerName: 'Build profile gate',
          reason: 'build profile declared but not yet validated (no green dry-run)',
          rejectedAtUtc: '2026-08-18T09:00:00Z',
        },
      })),
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('25 ready cards are not claimable: build profile not validated');
    expect(banner.textContent).toContain('build-profile-gate');
    fixture.destroy();
    http.verify();
  });
});
