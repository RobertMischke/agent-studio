import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';

export interface ReviewQueueTelemetry {
  observedAt: string;
  queueDepth: number;
  waitingDepth: number;
  activeReviews: number;
  drainRatePerHour: number;
  drainWindowMinutes: number;
  medianReviewDurationSeconds: number | null;
  durationWindowHours: number;
  durationSampleCount: number;
  lastDrainAt: string | null;
  oldestWaitingAt: string | null;
  stagnant: boolean;
  stagnationThresholdMinutes: number;
  stagnantForMinutes: number;
}

@Injectable({ providedIn: 'root' })
export class ReviewQueueTelemetryStore {
  private readonly http = inject(HttpClient);

  readonly snapshot = signal<ReviewQueueTelemetry | null>(null);
  readonly loading = signal(false);

  refresh(): void {
    this.loading.set(true);
    this.http.get<ReviewQueueTelemetry>('/api/v1/reviews/queue/telemetry').subscribe({
      next: snapshot => {
        this.snapshot.set(snapshot);
        this.loading.set(false);
      },
      error: () => {
        this.snapshot.set(null);
        this.loading.set(false);
      },
    });
  }
}
