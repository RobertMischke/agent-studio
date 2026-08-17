import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

export interface ReviewQueueSnapshot {
  queueDepth: number;
  activeJobs: number;
  isStagnant: boolean;
  stagnantSince: string | null;
  stagnantThresholdMinutes: number;
  drainRatePerMinute: number;
  medianReviewDurationMs: number | null;
  throughputWindowMinutes: number;
  observedAt: string;
}

/**
 * Fetches the auto-review post-processing queue snapshot from the backend.
 * Used by the status bar to show the review plane's active / waiting counts
 * and raise the ATTENTION state when cards are waiting but nothing drains.
 */
@Injectable({ providedIn: 'root' })
export class ReviewQueueService {
  private readonly http = inject(HttpClient, { optional: true });

  readonly snapshot = signal<ReviewQueueSnapshot | null>(null);
  readonly loading = signal(false);

  refresh(): void {
    if (!this.http) return;
    this.loading.set(true);
    this.http.get<ReviewQueueSnapshot>('/api/runner/auto-review-queue').subscribe({
      next: data => {
        this.snapshot.set(data);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
      },
    });
  }
}
