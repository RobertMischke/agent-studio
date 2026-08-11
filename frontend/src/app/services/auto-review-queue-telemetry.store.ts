import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import {
  clearVisibleInterval,
  setVisibleInterval,
  type VisibleIntervalHandle,
} from '../utils/visible-interval';

export interface AutoReviewQueueTelemetrySnapshot {
  queueDepth: number;
  activeReviews: number;
  outstandingReviews: number;
  completedReviewsInRateWindow: number;
  drainRatePerHour: number;
  medianReviewDurationSeconds: number | null;
  reviewDurationSampleCount: number;
  oldestQueuedAt: string | null;
  lastDrainAt: string | null;
  observedAt: string;
  rateWindowMinutes: number;
  durationWindowMinutes: number;
  stagnantThresholdMinutes: number;
  isStagnant: boolean;
  stagnantSince: string | null;
}

/** Shared polling store for the authority-owned Review Plane queue metric. */
@Injectable({ providedIn: 'root' })
export class AutoReviewQueueTelemetryStore {
  private readonly http = inject(HttpClient);
  private timer: VisibleIntervalHandle | null = null;
  private subscribers = 0;

  readonly status = signal<AutoReviewQueueTelemetrySnapshot | null>(null);
  readonly unavailable = signal(false);

  subscribe(intervalMs = 30_000): void {
    this.subscribers++;
    if (this.subscribers !== 1) return;
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), intervalMs);
  }

  release(): void {
    if (this.subscribers > 0) this.subscribers--;
    if (this.subscribers !== 0 || this.timer === null) return;
    clearVisibleInterval(this.timer);
    this.timer = null;
  }

  refresh(): void {
    this.http.get<AutoReviewQueueTelemetrySnapshot>(
      '/api/v1/management/auto-review-queue',
    ).subscribe({
      next: snapshot => {
        this.status.set(snapshot);
        this.unavailable.set(false);
      },
      error: () => this.unavailable.set(true),
    });
  }
}
