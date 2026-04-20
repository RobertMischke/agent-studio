import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { CreateJobRequest, GroupedJobs, JobDetail, JobInfo, WatchPathEntry, CliExecution, CliOutputLine, RunnerStatus, CliSettings } from '../models/job.model';

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
    return this.http.get<WatchPathEntry[]>(`${this.baseUrl}/watch-paths`);
  }

  createJob(req: CreateJobRequest) {
    return this.http.post<{ id: string }>(`${this.baseUrl}/jobs`, req);
  }

  updateJobFile(jobId: string, fileName: string, content: string) {
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/files/${encodeURIComponent(fileName)}`, { content });
  }

  reorderJobs(jobIds: string[]) {
    return this.http.post(`${this.baseUrl}/jobs/reorder`, { jobIds });
  }

  changeProject(jobId: string, targetWatchPath: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/change-project`, { targetWatchPath });
  }

  // CLI execution
  startJob(jobId: string) {
    return this.http.post<CliExecution>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/start`, {});
  }

  stopJob(jobId: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/stop`, {});
  }

  getJobOutput(jobId: string) {
    return this.http.get<CliOutputLine[]>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/output`);
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
