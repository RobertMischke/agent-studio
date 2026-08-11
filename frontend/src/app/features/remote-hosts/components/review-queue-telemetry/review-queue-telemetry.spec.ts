import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ReviewQueueTelemetryComponent } from './review-queue-telemetry';

describe('ReviewQueueTelemetryComponent', () => {
  it('renders comparable queue, drain, and duration metrics', async () => {
    await TestBed.configureTestingModule({
      imports: [ReviewQueueTelemetryComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ReviewQueueTelemetryComponent);
    fixture.componentRef.setInput('snapshot', {
      queueDepth: 40,
      activeReviews: 4,
      outstandingReviews: 44,
      completedReviewsInRateWindow: 9,
      drainRatePerHour: 9,
      medianReviewDurationSeconds: 750,
      reviewDurationSampleCount: 21,
      oldestQueuedAt: '2026-08-11T16:00:00Z',
      lastDrainAt: '2026-08-11T17:55:00Z',
      observedAt: '2026-08-11T18:00:00Z',
      rateWindowMinutes: 60,
      durationWindowMinutes: 1440,
      stagnantThresholdMinutes: 30,
      isStagnant: false,
      stagnantSince: null,
    });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelector('[data-testid="auto-review-queue-depth"]')?.textContent)
      .toContain('40');
    expect(root.querySelector('[data-testid="auto-review-queue-drain-rate"]')?.textContent)
      .toContain('9/h');
    expect(root.querySelector('[data-testid="auto-review-queue-median-duration"]')?.textContent)
      .toContain('12.5 min');
  });
});
