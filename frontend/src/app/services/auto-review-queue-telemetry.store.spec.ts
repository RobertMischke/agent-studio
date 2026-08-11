import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AutoReviewQueueTelemetryStore } from './auto-review-queue-telemetry.store';

describe('AutoReviewQueueTelemetryStore', () => {
  it('loads the authority-owned Review Plane metric', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const store = TestBed.inject(AutoReviewQueueTelemetryStore);
    const http = TestBed.inject(HttpTestingController);

    store.refresh();
    http.expectOne('/api/v1/management/auto-review-queue').flush({
      queueDepth: 12,
      activeReviews: 4,
      outstandingReviews: 16,
      completedReviewsInRateWindow: 8,
      drainRatePerHour: 8,
      medianReviewDurationSeconds: 600,
      reviewDurationSampleCount: 15,
      oldestQueuedAt: '2026-08-11T16:00:00Z',
      lastDrainAt: '2026-08-11T17:55:00Z',
      observedAt: '2026-08-11T18:00:00Z',
      rateWindowMinutes: 60,
      durationWindowMinutes: 1440,
      stagnantThresholdMinutes: 30,
      isStagnant: false,
      stagnantSince: null,
    });

    expect(store.status()?.queueDepth).toBe(12);
    expect(store.status()?.drainRatePerHour).toBe(8);
    expect(store.unavailable()).toBe(false);
  });
});
