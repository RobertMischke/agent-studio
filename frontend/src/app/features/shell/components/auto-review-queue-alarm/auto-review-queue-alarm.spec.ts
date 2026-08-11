import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { AutoReviewQueueTelemetryStore } from '../../../../services/auto-review-queue-telemetry.store';
import { AutoReviewQueueAlarmComponent } from './auto-review-queue-alarm';

describe('AutoReviewQueueAlarmComponent', () => {
  it('renders only an acute stagnant queue', async () => {
    await TestBed.configureTestingModule({
      imports: [AutoReviewQueueAlarmComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const store = TestBed.inject(AutoReviewQueueTelemetryStore);
    store.status.set({
      queueDepth: 9,
      activeReviews: 2,
      outstandingReviews: 11,
      completedReviewsInRateWindow: 0,
      drainRatePerHour: 0,
      medianReviewDurationSeconds: 720,
      reviewDurationSampleCount: 4,
      oldestQueuedAt: '2026-08-11T16:00:00Z',
      lastDrainAt: '2026-08-11T16:30:00Z',
      observedAt: '2026-08-11T18:00:00Z',
      rateWindowMinutes: 60,
      durationWindowMinutes: 1440,
      stagnantThresholdMinutes: 30,
      isStagnant: true,
      stagnantSince: '2026-08-11T16:30:00Z',
    });

    const fixture = TestBed.createComponent(AutoReviewQueueAlarmComponent);
    fixture.detectChanges();
    const banner = (fixture.nativeElement as HTMLElement).querySelector(
      '[data-testid="auto-review-queue-stagnation-banner"]',
    );

    expect(banner?.textContent).toContain('has not drained for 30 minutes');
    expect(banner?.textContent).toContain('9 reviews are waiting');
    fixture.destroy();
  });
});
