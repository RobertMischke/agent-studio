import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { RemoteQueueStarvationBannerComponent } from './remote-queue-starvation-banner';

describe('RemoteQueueStarvationBannerComponent', () => {
  it('describes stalled claim progress without claiming a rejection exists', async () => {
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
      claimProgressWindowMinutes: 5,
      claimProgressStalled: true,
      lastSuccessfulClaimAt: '2026-08-08T09:54:00Z',
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
    expect(banner.textContent).toContain('No successful claim was recorded within the 5-minute progress window');
    expect(banner.textContent).not.toContain('rejection');
    fixture.destroy();
    http.verify();
  });

  it('shows rejection guidance only when a visible affected task has one', async () => {
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
      claimProgressWindowMinutes: 5,
      claimProgressStalled: false,
      lastSuccessfulClaimAt: '2026-08-08T09:59:00Z',
      oldestEnteredLaneAt: '2026-08-08T09:00:00Z',
      observedAt: '2026-08-08T10:00:00Z',
      items: [{
        taskKey: 'AGT-1',
        taskId: 'one',
        projectName: 'Demo',
        title: 'One',
        enteredLaneAt: '2026-08-08T09:00:00Z',
        lastRejection: {
          code: 'repository-url-missing',
          runnerId: 'runner-01',
          runnerName: 'Runner 01',
          reason: 'repository URL is missing',
          rejectedAtUtc: '2026-08-08T09:58:00Z',
        },
      }],
    });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector(
      '[data-testid="remote-queue-starvation-banner"]',
    ) as HTMLElement;
    expect(banner.textContent).toContain('At least one affected task has a recorded claim rejection');
    expect(banner.textContent).not.toContain('No successful claim');
    fixture.destroy();
    http.verify();
  });
});
