import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { GroupedJobs, JobDetail, JobInfo } from '../models/job.model';

@Injectable({ providedIn: 'root' })
export class JobService {
  private readonly baseUrl = 'http://localhost:5030/api';

  readonly jobs = signal<JobInfo[]>([]);
  readonly grouped = signal<GroupedJobs>({ preparation: [], ready: [], progress: [], review: [], completed: [] });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  constructor(private http: HttpClient) {}

  refresh(): void {
    this.loading.set(true);
    this.error.set(null);

    this.http.get<JobInfo[]>(`${this.baseUrl}/jobs`).subscribe({
      next: (jobs) => {
        this.jobs.set(jobs);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err.message || 'Failed to load jobs');
        this.loading.set(false);
      }
    });

    this.http.get<GroupedJobs>(`${this.baseUrl}/jobs/grouped`).subscribe({
      next: (grouped) => this.grouped.set(grouped),
    });
  }

  getDetail(jobId: string) {
    return this.http.get<JobDetail>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}`);
  }

  updateState(jobId: string, state: string) {
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/state`, { targetState: state });
  }

  moveJob(jobId: string, targetState: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/move`, { targetState });
  }

  getWatchPaths() {
    return this.http.get<string[]>(`${this.baseUrl}/watch-paths`);
  }
}
