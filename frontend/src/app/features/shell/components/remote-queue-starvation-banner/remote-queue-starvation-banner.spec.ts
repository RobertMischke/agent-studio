import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { RemoteQueueStarvationBannerComponent } from './remote-queue-starvation-banner';

describe('RemoteQueueStarvationBannerComponent', () => {
  it('shows waiting cards and free capacity from the watchdog', async () => {
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
    fixture.destroy();
    http.verify();
  });
});
