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
  /**
   * The panel renders in the local host card and in the "Set up agent host"
   * tunnel step at the same time, against this one root-provided service.
   * Reference-count the panels so closing the dialog releases only its own
   * claim instead of freezing the card's panel on the last polled status.
   */
  private panels = 0;

  private readonly _status = signal<WindowsTunnelStatus | null>(null);
  private readonly _loading = signal(false);
  private readonly _error = signal<string | null>(null);

  readonly status = computed(() => this._status());
  readonly loading = computed(() => this._loading());
  readonly error = computed(() => this._error());

  start(): void {
    if (!this.http) return;
    this.panels += 1;
    if (this.timer) return;
    this.refresh();
    this.timer = setInterval(() => this.refresh(), WindowsTunnelStatusService.RefreshMs);
  }

  stop(): void {
    this.panels = Math.max(0, this.panels - 1);
    if (this.panels > 0) return;
    this.stopNow();
  }

  ngOnDestroy(): void {
    this.panels = 0;
    this.stopNow();
  }

  private stopNow(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
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
