import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { LocalCliHealthSnapshot } from './cli-repair-note';

/**
 * Reads local CLI install health from the backend (AGT-2673). Read-only: the
 * repair itself is the backend's decision, this only makes it visible.
 */
@Injectable({ providedIn: 'root' })
export class HostCliHealthService {
  private readonly http = inject(HttpClient, { optional: true });

  readonly snapshot = signal<LocalCliHealthSnapshot | null>(null);

  refresh(): void {
    if (!this.http) return;
    this.http.get<LocalCliHealthSnapshot>('/api/v1/host-health/cli').subscribe({
      // Host health is a background surface: a failed poll leaves the last
      // known snapshot in place rather than blanking the bar.
      next: snapshot => this.snapshot.set(snapshot ?? null),
      error: () => undefined,
    });
  }
}
