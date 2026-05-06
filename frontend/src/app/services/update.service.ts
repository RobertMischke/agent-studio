import { computed, effect, inject, Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { TriggerRequest, TriggerResponse, UpdateHistoryEntry, UpdateStatus } from '../models/update-service.model';

/**
 * Talks to the standalone UpdateService (default port 5039). The endpoint
 * is *not* proxied through ng-serve: the whole point of the separate
 * process is that it stays reachable while the main backend (port 5031)
 * restarts. We therefore hit it via an absolute URL that matches the
 * caller's hostname and a configurable port.
 *
 * Polling cadence is dynamic: 30 s when idle, 2 s when an update is in
 * flight. The faster cadence kicks in automatically the moment a status
 * read reports `isRunning=true` and the moment a trigger response says
 * the orchestrator is on a non-idle phase.
 */
@Injectable({ providedIn: 'root' })
export class UpdateClientService {
  private readonly http = inject(HttpClient);

  /** Resolves the base URL for the UpdateService (host of the FE, port 5039). */
  private readonly baseUrl = (() => {
    if (typeof window === 'undefined') return 'http://127.0.0.1:5039';
    const url = new URL(window.location.href);
    return `${url.protocol}//${url.hostname}:5039`;
  })();

  /** Last status snapshot from /update/status; null until first poll completes. */
  readonly status = signal<UpdateStatus | null>(null);

  /** True when the UpdateService itself was unreachable on the last poll. */
  readonly serviceUnreachable = signal(false);

  /** Convenience: any phase that means the orchestrator is mid-update. */
  readonly isRunning = computed(() => this.status()?.isRunning ?? false);

  /** Convenience: how far behind origin/main we are; 0 when unknown / synced. */
  readonly behindBy = computed(() => this.status()?.behindBy ?? 0);

  /**
   * UI feature gate: while an update is running, mutations should be
   * blocked at the call site (job create / move / mode change) so the
   * banner and the actual behaviour stay aligned.
   */
  readonly mutationsBlocked = computed(() => this.isRunning());

  private pollTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Start polling immediately; refresh cadence whenever phase changes.
    this.scheduleNextPoll(0);
    effect(() => {
      // touch isRunning so the effect re-fires on phase transitions
      void this.isRunning();
    });
  }

  /** Manually trigger an update. Resolves with the trigger response. */
  async trigger(reason: string | null = null, force = false): Promise<TriggerResponse> {
    const body: TriggerRequest = { reason: reason ?? undefined, force };
    const resp = await firstValueFrom(
      this.http.post<TriggerResponse>(`${this.baseUrl}/update/trigger`, body)
    );
    // Bump cadence and refresh now so the banner reflects the new phase
    // before the next normal poll fires.
    this.refreshNow();
    return resp;
  }

  /** Force an immediate status refresh (used after a trigger). */
  async refreshNow(): Promise<void> {
    await this.pollOnce();
    this.scheduleNextPoll(this.isRunning() ? 2_000 : 30_000);
  }

  /** Latest history entries; never auto-polled to keep traffic small. */
  async readHistory(max = 20): Promise<UpdateHistoryEntry[]> {
    return await firstValueFrom(
      this.http.get<UpdateHistoryEntry[]>(`${this.baseUrl}/update/history?max=${max}`)
    );
  }

  // ─── internals ──────────────────────────────────────────────────────────

  private scheduleNextPoll(delayMs: number): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = setTimeout(() => {
      this.pollOnce()
        .catch(() => { /* status() already reflects the failure */ })
        .finally(() => this.scheduleNextPoll(this.isRunning() ? 2_000 : 30_000));
    }, delayMs);
  }

  private async pollOnce(): Promise<void> {
    try {
      const next = await firstValueFrom(this.http.get<UpdateStatus>(`${this.baseUrl}/update/status`));
      this.status.set(next);
      this.serviceUnreachable.set(false);
    } catch {
      // Don't blank out the last-known status; a transient failure is more
      // useful as "stale snapshot + unreachable flag" than as a null UI.
      this.serviceUnreachable.set(true);
    }
  }
}
