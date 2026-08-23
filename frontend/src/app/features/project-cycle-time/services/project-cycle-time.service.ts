import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import type { CycleTimeWindow, ProjectCycleTimeResponse } from '../models/project-cycle-time.model';

/**
 * Reads the per-project cycle-time projection. One GET per (project, window);
 * the backend memoises per-task rows, so re-requests are cheap.
 */
@Injectable({ providedIn: 'root' })
export class ProjectCycleTimeService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  load(projectName: string, window: CycleTimeWindow): Observable<ProjectCycleTimeResponse> {
    const params = new HttpParams().set('window', window);
    return this.http.get<ProjectCycleTimeResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/cycle-time`,
      { params },
    );
  }
}
