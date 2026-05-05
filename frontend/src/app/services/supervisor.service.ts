import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { MetaCycleResponse, SupervisorObservation, SupervisorRecentEvents } from '../models/supervisor.model';

/**
 * Read-only observation + four manual intervention endpoints. Polls happen
 * in the consuming component; this service is a thin wrapper.
 */
@Injectable({ providedIn: 'root' })
export class SupervisorService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api/supervisor';

  observe(project: string): Observable<SupervisorObservation> {
    return this.http.get<SupervisorObservation>(
      `${this.baseUrl}/${encodeURIComponent(project)}/observation`
    );
  }

  recentEvents(project: string, max: number = 50): Observable<SupervisorRecentEvents> {
    return this.http.get<SupervisorRecentEvents>(
      `${this.baseUrl}/${encodeURIComponent(project)}/recent-events?max=${max}`
    );
  }

  cancelRun(project: string, jobId: string, reason: string) {
    return this.http.post<{ ok: boolean }>(
      `${this.baseUrl}/${encodeURIComponent(project)}/intervene/cancel-run`,
      { reason, jobId }
    );
  }

  pausePickup(project: string, reason: string, ttlSeconds?: number) {
    return this.http.post<{ ok: boolean }>(
      `${this.baseUrl}/${encodeURIComponent(project)}/intervene/pause-pickup`,
      { reason, ttlSeconds: ttlSeconds ?? null }
    );
  }

  forceFail(project: string, jobId: string, reason: string) {
    return this.http.post<{ ok: boolean }>(
      `${this.baseUrl}/${encodeURIComponent(project)}/intervene/force-fail`,
      { reason, jobId }
    );
  }

  resume(project: string, reason: string) {
    return this.http.post<{ ok: boolean }>(
      `${this.baseUrl}/${encodeURIComponent(project)}/intervene/resume`,
      { reason }
    );
  }

  metaCycle(project: string, max: number = 8): Observable<MetaCycleResponse> {
    return this.http.get<MetaCycleResponse>(
      `${this.baseUrl}/${encodeURIComponent(project)}/meta-cycle?max=${max}`
    );
  }
}
