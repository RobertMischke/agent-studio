import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { QuotaReport, QuotaSnapshot } from '../models/quota.model';

/**
 * Cycle 10d API client for the per-CLI quota / rate-limit endpoints.
 * Lifted out of the TaskService god-service per ADR-0034 so the per-feature
 * HTTP surface is owned by the feature folder.
 *
 * Wraps `/api/cli/quota` (cached + force-refresh) and the per-CLI
 * usage caps endpoints under `/api/cli/quota/caps`.
 */
@Injectable({ providedIn: 'root' })
export class QuotaApiService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  /**
   * Cached snapshot of every CLI's quota windows. Returns immediately
   * with the on-disk snapshot; the backend re-probes stale entries
   * in the background. Use this for the strip + donut surfaces.
   */
  getQuotaReport() {
    return this.http.get<QuotaReport>(`${this.baseUrl}/cli/quota`);
  }

  /**
   * Force a synchronous re-probe of every CLI. Spawns fresh PTYs; takes
   * several seconds. Use this for the strip-level "refresh all" button.
   */
  refreshQuotaAll() {
    return this.http.post<QuotaReport>(`${this.baseUrl}/cli/quota/refresh`, {});
  }

  /**
   * Force a synchronous re-probe of one CLI's quota. Spawns a fresh
   * PTY; takes several seconds. Use this for the per-CLI "↻" button.
   */
  refreshQuotaForCli(cliType: string) {
    return this.http.post<QuotaSnapshot>(
      `${this.baseUrl}/cli/quota/refresh/${cliType}`,
      {},
    );
  }

  /**
   * Per-CLI usage-cap configuration (admin panel). The map shape is
   * `{ defaultCapPct, caps: { [cliType]: { [windowKey]: capPct } } }`.
   */
  getQuotaCaps() {
    return this.http.get<{
      defaultCapPct: number;
      caps: Record<string, Record<string, number>>;
    }>(`${this.baseUrl}/cli/quota/caps`);
  }

  /**
   * Update one cap. The runner blocks pickup and stops in-flight runs
   * when usage crosses these caps so the user keeps a buffer for
   * ad-hoc work outside the orchestrator.
   */
  setQuotaCap(cliType: string, windowLabel: string, capPct: number) {
    return this.http.put<{
      defaultCapPct: number;
      caps: Record<string, Record<string, number>>;
    }>(`${this.baseUrl}/cli/quota/caps`, { cliType, windowLabel, capPct });
  }
}
