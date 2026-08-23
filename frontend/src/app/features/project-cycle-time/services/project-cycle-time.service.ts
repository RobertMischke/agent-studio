import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import type {
  CycleTimeWindow,
  ProjectCycleTimeResponse,
  ProjectCycleTimeTaskResponse,
} from '../models/project-cycle-time.model';

/**
 * Reads the per-project cycle-time projection. One GET per (project, window);
 * the backend memoises per-task rows, so re-requests are cheap. Per-task
 * transition lists are fetched on demand through the per-task endpoint so the
 * list payload stays bounded.
 */
@Injectable({ providedIn: 'root' })
export class ProjectCycleTimeService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/api';

  load(projectName: string, window: CycleTimeWindow, includeTransitions = false): Observable<ProjectCycleTimeResponse> {
    let params = new HttpParams().set('window', window);
    if (includeTransitions) params = params.set('detail', 'transitions');
    return this.http.get<ProjectCycleTimeResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/cycle-time`,
      { params },
    );
  }

  loadTask(projectName: string, taskKey: string): Observable<ProjectCycleTimeTaskResponse> {
    return this.http.get<ProjectCycleTimeTaskResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/cycle-time/tasks/${encodeURIComponent(taskKey)}`,
    );
  }
}
