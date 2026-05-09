import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../utils/visible-interval';

/**
 * Live status snapshot of the multi-aspect auto-review tick. Surfaced
 * in the kanban 4-auto-review lane header so the user sees that the
 * orchestrator is alive and forming opinions instead of silently
 * waving jobs through. The store polls the backend at the
 * orchestrator's tick cadence (default 30s); the lane header reads
 * the signal directly.
 *
 * Shape mirrors the backend's `AutoReviewStatusView` record. A null
 * `lastTickAt` means the orchestrator has not completed a tick since
 * the backend started.
 */
export interface AutoReviewStatusView {
  lastTickAt: string | null;
  accept: number;
  reissue: number;
  escalate: number;
  aspectsRun: number;
  currentJob: string | null;
  currentProject: string | null;
}

@Injectable({ providedIn: 'root' })
export class AutoReviewStatusStore {
  private readonly http = inject(HttpClient);
  private timer: VisibleIntervalHandle | null = null;
  private subscribers = 0;

  readonly status = signal<AutoReviewStatusView | null>(null);

  /**
   * Increase the subscriber count. The first subscriber starts the
   * polling timer; subsequent subscribers piggy-back. Components must
   * call `release()` in their teardown to keep the count balanced.
   */
  subscribe(intervalMs = 30_000): void {
    this.subscribers++;
    if (this.subscribers === 1) {
      this.refresh();
      this.timer = setVisibleInterval(() => this.refresh(), intervalMs);
    }
  }

  release(): void {
    if (this.subscribers > 0) this.subscribers--;
    if (this.subscribers === 0 && this.timer !== null) {
      clearVisibleInterval(this.timer);
      this.timer = null;
    }
  }

  refresh(): void {
    this.http.get<AutoReviewStatusView>('/api/auto-review/status').subscribe({
      next: v => this.status.set(v),
      error: () => { /* keep last known status; the lane header handles null */ }
    });
  }
}
