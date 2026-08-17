import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy, inject, signal } from '@angular/core';
import type { WindowsTunnelSupervisionStatus } from '../models/windows-tunnel-supervision.model';

/**
 * Polls the Windows tunnel supervision status (AGT-2664) so the Execution
 * Hosts panel can show the keeper/watchdog Scheduled Tasks alongside the
 * per-host Task Server route. Mirrors {@link ProviderAuthStatusService}'s
 * start/stop/refresh shape.
 */
@Injectable({ providedIn: 'root' })
export class WindowsTunnelSupervisionService implements OnDestroy {
  private static readonly RefreshMs = 60_000;
  private readonly http = inject(HttpClient, { optional: true });
  private timer: ReturnType<typeof setInterval> | null = null;

  readonly status = signal<WindowsTunnelSupervisionStatus | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  start(): void {
    if (this.timer || !this.http) return;
    this.refresh();
    this.timer = setInterval(() => this.refresh(), WindowsTunnelSupervisionService.RefreshMs);
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
  }

  ngOnDestroy(): void {
    this.stop();
  }

  refresh(): void {
    if (!this.http) return;
    this.loading.set(true);
    this.http.get<WindowsTunnelSupervisionStatus>('/api/v1/windows-tunnel-supervision/status').subscribe({
      next: status => {
        this.status.set(status);
        this.error.set(null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not reach the Windows tunnel supervision status endpoint.');
        this.loading.set(false);
      },
    });
  }
}
