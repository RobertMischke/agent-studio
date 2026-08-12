import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { AutoReviewQueueAlarmComponent } from './auto-review-queue-alarm';

describe('AutoReviewQueueAlarmComponent', () => {
  it('shows an acute operator alarm when waiting reviews stop draining', async () => {
    await TestBed.configureTestingModule({
      imports: [AutoReviewQueueAlarmComponent],
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    const fixture = TestBed.createComponent(AutoReviewQueueAlarmComponent);
    fixture.detectChanges();
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/v1/reviews/queue/telemetry').flush({
      observedAt: '2026-08-11T22:00:00Z',
      queueDepth: 40,
      waitingDepth: 36,
      activeReviews: 4,
      drainRatePerHour: 0,
      drainWindowMinutes: 60,
      medianReviewDurationSeconds: 1200,
      durationWindowHours: 24,
      durationSampleCount: 12,
      lastDrainAt: '2026-08-11T21:20:00Z',
      oldestWaitingAt: '2026-08-11T20:00:00Z',
      stagnant: true,
      stagnationThresholdMinutes: 30,
      stagnantForMinutes: 40,
    });
    fixture.detectChanges();

    const alarm = fixture.nativeElement.querySelector(
      '[data-testid="auto-review-queue-stagnation-alarm"]',
    ) as HTMLElement;
    expect(alarm.textContent).toContain('has not drained for 40 minutes');
    expect(alarm.textContent).toContain('36 waiting · 4 active');
    fixture.destroy();
    http.verify();
  });
});
