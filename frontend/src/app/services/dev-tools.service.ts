import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface DevToolsFlags {
  updateStableEnabled: boolean;
  deleteE2EJobsEnabled: boolean;
}

export interface E2EJob {
  jobKey: string;
  id: string;
  title: string;
  state: string;
  projectName: string;
  watchPath: string;
}

export interface DeleteE2EReport {
  deletedCount: number;
  failedCount: number;
  deleted: string[];
  failed: string[];
}

/**
 * Reads the per-checkout DevTools flags from /api/environment and exposes
 * tiny wrappers for the two dev-only routes. The flags drive whether the
 * header renders the Update-Stable and Delete-E2E buttons; off by default,
 * opt-in via appsettings.Local.json.
 */
@Injectable({ providedIn: 'root' })
export class DevToolsService {
  readonly flags = signal<DevToolsFlags>({ updateStableEnabled: false, deleteE2EJobsEnabled: false });

  constructor(private http: HttpClient) {}

  loadFlags(): void {
    this.http.get<{ devTools?: DevToolsFlags }>('/api/environment').subscribe({
      next: (env) => {
        const dt = env.devTools;
        if (dt) this.flags.set({
          updateStableEnabled: !!dt.updateStableEnabled,
          deleteE2EJobsEnabled: !!dt.deleteE2EJobsEnabled,
        });
      },
      error: () => { /* leave defaults */ }
    });
  }

  listE2EJobs(): Observable<E2EJob[]> {
    return this.http.get<E2EJob[]>('/api/devtools/e2e-jobs');
  }

  deleteE2EJobs(jobKeys: string[]): Observable<DeleteE2EReport> {
    return this.http.post<DeleteE2EReport>('/api/devtools/e2e-jobs/delete', { jobKeys });
  }
}
