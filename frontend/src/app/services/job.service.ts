import { Injectable, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { CreateJobRequest, GroupedJobs, JobDetail, JobInfo, WatchPathEntry, CliExecution, CliOutputLine, RunnerStatus, CliSettings, JobOrderItem } from '../models/job.model';

@Injectable({ providedIn: 'root' })
export class JobService {
  private readonly baseUrl = 'http://localhost:5030/api';

  readonly jobs = signal<JobInfo[]>([]);
  readonly grouped = signal<GroupedJobs>({ preparation: [], ready: [], progress: [], review: [], completed: [] });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly runnerStatus = signal<RunnerStatus>({ projects: {} });

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
        if (err.status === 0) {
          this.error.set('Backend not reachable — is the API running on localhost:5030?');
        } else {
          this.error.set(err.error?.error || err.message || 'Failed to load jobs');
        }
        this.loading.set(false);
      }
    });

    this.http.get<GroupedJobs>(`${this.baseUrl}/jobs/grouped`).subscribe({
      next: (grouped) => this.grouped.set(grouped),
    });
  }

  private withWatchPath(watchPath?: string): { params?: HttpParams } {
    return watchPath
      ? { params: new HttpParams().set('watchPath', watchPath) }
      : {};
  }

  getDetail(jobId: string, watchPath?: string) {
    return this.http.get<JobDetail>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}`, this.withWatchPath(watchPath));
  }

  updateState(jobId: string, state: string, watchPath?: string) {
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/state`, { targetState: state }, this.withWatchPath(watchPath));
  }

  moveJob(jobId: string, targetState: string, watchPath?: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/move`, { targetState }, this.withWatchPath(watchPath));
  }

  getWatchPaths() {
    return this.http.get<WatchPathEntry[]>(`${this.baseUrl}/watch-paths`);
  }

  createJob(req: CreateJobRequest) {
    return this.http.post<{ id: string }>(`${this.baseUrl}/jobs`, req);
  }

  updateJobFile(jobId: string, fileName: string, content: string, watchPath?: string) {
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/files/${encodeURIComponent(fileName)}`, { content }, this.withWatchPath(watchPath));
  }

  reorderJobs(jobs: JobOrderItem[]) {
    return this.http.post(`${this.baseUrl}/jobs/reorder`, { jobs });
  }

  changeProject(jobId: string, targetWatchPath: string, watchPath?: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/change-project`, { targetWatchPath }, this.withWatchPath(watchPath));
  }

  // CLI execution
  startJob(jobId: string, watchPath?: string) {
    return this.http.post<CliExecution>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/start`, {}, this.withWatchPath(watchPath));
  }

  stopJob(jobId: string, watchPath?: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/stop`, {}, this.withWatchPath(watchPath));
  }

  getJobOutput(jobId: string, watchPath?: string) {
    return this.http.get<CliOutputLine[]>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/output`, this.withWatchPath(watchPath));
  }

  // Runner management
  getRunnerStatus() {
    return this.http.get<RunnerStatus>(`${this.baseUrl}/runner/status`);
  }

  setRunnerMode(projectName: string, mode: string) {
    return this.http.put(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/mode`, { mode });
  }

  startRunner(projectName: string) {
    return this.http.post(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/start`, {});
  }

  stopRunner(projectName: string) {
    return this.http.post(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/stop`, {});
  }

  refreshRunnerStatus(): void {
    this.getRunnerStatus().subscribe({
      next: (status) => this.runnerStatus.set(status),
    });
  }

  // CLI settings
  getCliSettings() {
    return this.http.get<CliSettings>(`${this.baseUrl}/settings/cli`);
  }

  setCliPath(path: string) {
    return this.http.put<CliSettings>(`${this.baseUrl}/settings/cli`, { path });
  }

  testCliPath(path: string) {
    return this.http.post<CliSettings>(`${this.baseUrl}/settings/cli/test`, { path });
  }

  setGitHubToken(token: string) {
    return this.http.put<CliSettings>(`${this.baseUrl}/settings/cli/token`, { token });
  }
}
