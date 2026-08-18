import { HttpClient } from '@angular/common/http';
import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type {
  WindowsTunnelRegisterRequest,
  WindowsTunnelRegistrationResponse,
  WindowsTunnelStatus,
} from '../models/windows-tunnel.model';

/** Polls and drives `GET/POST /api/v1/management/windows-tunnel/*` (AGT-2664). */
@Injectable({ providedIn: 'root' })
export class WindowsTunnelStatusService implements OnDestroy {
  private static readonly RefreshMs = 30_000;
  private readonly http = inject(HttpClient, { optional: true });
  private timer: ReturnType<typeof setInterval> | null = null;

  private readonly _status = signal<WindowsTunnelStatus | null>(null);
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);

  readonly status = computed(() => this._status());
  readonly loading = computed(() => this._loading());
  readonly error = computed(() => this._error());

  start(): void {
    if (this.timer || !this.http) return;
    this.refresh();
    this.timer = setInterval(() => this.refresh(), WindowsTunnelStatusService.RefreshMs);
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
    this._loading.set(true);
    this.http.get<WindowsTunnelStatus>('/api/v1/management/windows-tunnel/status').subscribe({
      next: status => {
        this._status.set(status);
        this._error.set(null);
        this._loading.set(false);
      },
      error: () => {
        this._error.set('Could not reach the Windows tunnel status probe.');
        this._loading.set(false);
      },
    });
  }

  register(request: WindowsTunnelRegisterRequest): Observable<WindowsTunnelRegistrationResponse> {
    if (!this.http) throw new Error('Windows tunnel registration requires the Studio HTTP client.');
    return this.http.post<WindowsTunnelRegistrationResponse>(
      '/api/v1/management/windows-tunnel/register',
      request,
    );
  }
}
