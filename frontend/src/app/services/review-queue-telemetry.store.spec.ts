import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { describe, expect, it } from 'vitest';
import { ReviewQueueTelemetryStore } from './review-queue-telemetry.store';

describe('ReviewQueueTelemetryStore', () => {
  it('coalesces concurrent refreshes onto one queue request', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
    });
    const store = TestBed.inject(ReviewQueueTelemetryStore);
    const http = TestBed.inject(HttpTestingController);

    store.refresh();
    store.refresh();

    const request = http.expectOne('/api/v1/reviews/queue/telemetry');
    request.flush({
      observedAt: '2026-08-14T12:00:00Z',
      queueDepth: 12,
      waitingDepth: 12,
      activeReviews: 0,
      drainRatePerHour: 0,
      drainWindowMinutes: 60,
      medianReviewDurationSeconds: null,
      durationWindowHours: 24,
      durationSampleCount: 0,
      lastDrainAt: null,
      oldestWaitingAt: '2026-08-14T11:30:00Z',
      stagnant: true,
      stagnationThresholdMinutes: 30,
      stagnantForMinutes: 30,
    });

    expect(store.loading()).toBe(false);
    expect(store.snapshot()?.waitingDepth).toBe(12);
    http.verify();
  });
});
