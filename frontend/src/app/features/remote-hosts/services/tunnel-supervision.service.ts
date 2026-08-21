import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { TunnelSupervisionResponse } from '../models/tunnel-supervision.model';

/**
 * Read-only visibility for the Windows control-plane host's tunnel keeper and
 * watchdog (AGT-2664). Backed by `GET /api/system/tunnel-supervision`, which
 * itself only ever returns a snapshot on a Windows Studio host that has run
 * the guided registration - everywhere else `overall` is `not-configured`
 * and the panel that consumes this service stays hidden.
 */
@Injectable({ providedIn: 'root' })
export class TunnelSupervisionService {
  private readonly http = inject(HttpClient, { optional: true });

  readonly response = signal<TunnelSupervisionResponse | null>(null);
  readonly loading = signal(false);

  refresh(): void {
    if (!this.http) return;
    this.loading.set(true);
    this.http.get<TunnelSupervisionResponse>('/api/system/tunnel-supervision').subscribe({
      next: response => {
        this.response.set(response);
        this.loading.set(false);
      },
      error: () => {
        this.response.set(null);
        this.loading.set(false);
      },
    });
  }
}
