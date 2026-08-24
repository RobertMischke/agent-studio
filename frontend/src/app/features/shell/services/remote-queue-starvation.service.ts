import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { RemoteDispatchRejection } from '../../../models/task.model';

export interface RemoteQueueStarvationItem {
  taskKey: string;
  taskId: string;
  projectName: string;
  title: string;
  enteredLaneAt: string;
  lastRejection?: RemoteDispatchRejection | null;
  /** True when the card is unclaimable only because its build profile is not validated. */
  buildProfileGateBlocked?: boolean;
}

/** One project whose build-profile gate is refusing auto-pickup (AGT-2677). */
export interface BuildProfileGateBlockage {
  projectName: string;
  readyTaskCount: number;
  gateCode: string;
  gateReason: string;
  buildProfileStatus: string;
}

export interface RemoteQueueStarvationSnapshot {
  active: boolean;
  waitingTaskCount: number;
  availableSlots: number;
  thresholdMinutes: number;
  claimProgressStalled: boolean;
  lastSuccessfulClaimAt: string | null;
  hasRejections: boolean;
  oldestEnteredLaneAt: string | null;
  observedAt: string;
  items: RemoteQueueStarvationItem[];
  gateBlockedTaskCount?: number;
  gateBlockedProjects?: BuildProfileGateBlockage[];
}

const POLL_INTERVAL_MS = 15_000;

/**
 * Single owner of the `/api/runner/queue-starvation` snapshot.
 *
 * Two banners read the same snapshot - the queue alarm and the build-profile
 * gate alarm (AGT-2677) - and the endpoint recomputes the whole ready-queue
 * projection per call, so each banner polling on its own would double that work
 * for one payload. Polling is refcounted: the first banner to attach starts the
 * timer, the last to detach stops it, and a later attach reads the signal that
 * is already there instead of firing an extra request.
 */
@Injectable({ providedIn: 'root' })
export class RemoteQueueStarvationService {
  private readonly http = inject(HttpClient);
  private timer: ReturnType<typeof setInterval> | null = null;
  private attached = 0;

  readonly snapshot = signal<RemoteQueueStarvationSnapshot | null>(null);

  /** Attach a consumer. Returns the detach callback for `ngOnDestroy`. */
  attach(): () => void {
    this.attached += 1;
    if (this.attached === 1) {
      this.refresh();
      this.timer = setInterval(() => this.refresh(), POLL_INTERVAL_MS);
    }
    let detached = false;
    return () => {
      if (detached) return;
      detached = true;
      this.attached -= 1;
      if (this.attached > 0 || !this.timer) return;
      clearInterval(this.timer);
      this.timer = null;
    };
  }

  private refresh(): void {
    this.http.get<RemoteQueueStarvationSnapshot>('/api/runner/queue-starvation').subscribe({
      next: snapshot => this.snapshot.set(snapshot),
      // A failed poll must not blank an alarm the operator is acting on; the
      // next tick either restores it or the backend really has nothing to say.
      error: () => this.snapshot.set(null),
    });
  }
}
