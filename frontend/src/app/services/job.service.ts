import { Injectable, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { CreateJobRequest, GroupedJobs, JobDetail, JobInfo, WatchPathEntry, CliExecution, CliOutputLine, RunnerStatus, CliSettings, JobOrderItem, ContextUsageSnapshot, CopilotModelCatalog, CliModelCatalog, CliType, CliUsageReport, QuotaReport, QuotaSnapshot, GitStatus, ClaudeSessionResponse, JobCommitDetail, SessionEventsResponse } from '../models/job.model';
import { ErrorDialogService } from './error-dialog.service';

@Injectable({ providedIn: 'root' })
export class JobService {
  private readonly baseUrl = '/api';
  private liveUpdateTimer: ReturnType<typeof setInterval> | null = null;

  readonly jobs = signal<JobInfo[]>([]);
  readonly grouped = signal<GroupedJobs>({ preparation: [], ready: [], progress: [], review: [], completed: [], archive: [] });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly runnerStatus = signal<RunnerStatus>({ projects: {} });
  constructor(private http: HttpClient, private errorDialog: ErrorDialogService) {}

  refresh(silent = false): void {
    if (!silent) {
      this.loading.set(true);
      this.error.set(null);
    }

    this.http.get<JobInfo[]>(`${this.baseUrl}/jobs`).subscribe({
      next: (jobs) => {
        this.jobs.set(jobs);
        if (silent) {
          this.error.set(null);
        }
        this.loading.set(false);
      },
      error: (err) => {
        const message = err.status === 0
          ? 'Backend not reachable — is the API running on localhost:5030?'
          : err.error?.error || err.message || 'Failed to load jobs';

        this.error.set(message);
        if (!silent) {
          this.errorDialog.show(err, {
            title: 'Failed to load jobs',
            fallbackMessage: 'Failed to load jobs',
            source: 'Dashboard refresh'
          });
        }
        this.loading.set(false);
      }
    });

    this.http.get<GroupedJobs>(`${this.baseUrl}/jobs/grouped`).subscribe({
      next: (grouped) => {
        this.grouped.set(grouped);
      },
      error: (err) => {
        if (!silent) {
          this.errorDialog.show(err, {
            title: 'Failed to load board columns',
            fallbackMessage: 'Failed to load board columns',
            source: 'Board refresh'
          });
        }
      }
    });

    this.refreshRunnerStatus(silent);
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

  // Git
  getGitStatus(jobId: string, watchPath?: string) {
    return this.http.get<GitStatus>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/status`, this.withWatchPath(watchPath));
  }

  getGitDiff(jobId: string, path: string | null, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    const params = (opts.params as Record<string, string> | undefined) ?? {};
    if (path) params['path'] = path;
    return this.http.get(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/diff`, { ...opts, params, responseType: 'text' });
  }

  commitJob(jobId: string, message: string, watchPath?: string) {
    return this.http.post<{ sha?: string }>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/commit`, { message }, this.withWatchPath(watchPath));
  }

  generateCommitMessage(jobId: string, watchPath?: string) {
    return this.http.post<{ message: string }>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/generate-message`, {}, this.withWatchPath(watchPath));
  }

  // Per-task commit snapshot — what the auto-commit recorded on the
  // progress→review transition, plus a live re-derivation of the file list.
  getJobCommit(jobId: string, watchPath?: string) {
    return this.http.get<JobCommitDetail>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/commit`, this.withWatchPath(watchPath));
  }

  getJobCommitDiff(jobId: string, path: string | null, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    const params = (opts.params as Record<string, string> | undefined) ?? {};
    if (path) params['path'] = path;
    return this.http.get(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/commit/diff`, { ...opts, params, responseType: 'text' });
  }

  openInVsCode(jobId: string, watchPath?: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/open-in-vscode`, {}, this.withWatchPath(watchPath));
  }

  getClaudeSessionInfo(jobId: string, watchPath?: string) {
    return this.http.get<ClaudeSessionResponse>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/claude/session-info`, this.withWatchPath(watchPath));
  }

  /** Per-job session-event log: start/continue/recovery rows + sessionChain. */
  getSessionEvents(jobId: string, watchPath?: string) {
    return this.http.get<SessionEventsResponse>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/session-events`, this.withWatchPath(watchPath));
  }

  // CLI execution
  startJob(jobId: string, watchPath?: string, model?: string, cliType?: CliType) {
    const body: { model?: string; cliType?: CliType } = {};
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    return this.http.post<CliExecution>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/start`, body, this.withWatchPath(watchPath));
  }

  stopJob(jobId: string, watchPath?: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/stop`, {}, this.withWatchPath(watchPath));
  }

  continueJob(jobId: string, prompt: string, watchPath?: string, model?: string, cliType?: CliType) {
    const body: { prompt: string; model?: string; cliType?: CliType } = { prompt };
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    return this.http.post<CliExecution>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/continue`, body, this.withWatchPath(watchPath));
  }

  setJobModel(jobId: string, model: string | null, watchPath?: string) {
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/model`, { model }, this.withWatchPath(watchPath));
  }

  setJobCliType(jobId: string, cliType: CliType, watchPath?: string, useOwnSession?: boolean) {
    const body: { cliType: CliType; useOwnSession?: boolean } = { cliType };
    if (useOwnSession !== undefined) body.useOwnSession = useOwnSession;
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/cli-type`, body, this.withWatchPath(watchPath));
  }

  getCliModelCatalog(cliType: CliType, refresh = false) {
    const params = refresh ? new HttpParams().set('refresh', 'true') : undefined;
    return this.http.get<CliModelCatalog>(`${this.baseUrl}/cli/${cliType}/models`, params ? { params } : {});
  }

  getCliUsageReport() {
    return this.http.get<CliUsageReport>(`${this.baseUrl}/cli/usage`);
  }

  // Quota / subscription rate-limit reporting.
  // GET returns the cached snapshot immediately and triggers a background refresh
  // for stale entries. The POST variants force a synchronous re-probe (slow — each
  // call spawns a CLI in a PTY for several seconds).
  getQuotaReport() {
    return this.http.get<QuotaReport>(`${this.baseUrl}/cli/quota`);
  }

  refreshQuotaAll() {
    return this.http.post<QuotaReport>(`${this.baseUrl}/cli/quota/refresh`, {});
  }

  refreshQuotaForCli(cliType: CliType) {
    return this.http.post<QuotaSnapshot>(`${this.baseUrl}/cli/quota/refresh/${cliType}`, {});
  }

  setJobTitle(jobId: string, title: string, watchPath?: string) {
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/title`, { title }, this.withWatchPath(watchPath));
  }

  getModelCatalog() {
    return this.http.get<CopilotModelCatalog>(`${this.baseUrl}/settings/cli/models`);
  }

  getJobOutput(jobId: string, watchPath?: string) {
    return this.http.get<CliOutputLine[]>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/output`, this.withWatchPath(watchPath));
  }

  refreshContextUsage(jobId: string, watchPath?: string) {
    return this.http.post<ContextUsageSnapshot>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/context-usage/refresh`, {}, this.withWatchPath(watchPath));
  }

  regenerateSummary(jobId: string, watchPath?: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/summary/regenerate`, {}, this.withWatchPath(watchPath));
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

  refreshRunnerStatus(silent = false): void {
    this.getRunnerStatus().subscribe({
      next: (status) => this.runnerStatus.set(status),
      error: (err) => {
        if (!silent) {
          this.errorDialog.show(err, {
            title: 'Failed to load runner status',
            fallbackMessage: 'Failed to load runner status',
            source: 'Runner status'
          });
        }
      }
    });
  }

  startLiveUpdates(intervalMs = 2000): void {
    if (this.liveUpdateTimer) {
      return;
    }

    this.liveUpdateTimer = setInterval(() => {
      if (typeof document !== 'undefined' && document.hidden) {
        return;
      }

      this.refresh(true);
    }, intervalMs);
  }

  stopLiveUpdates(): void {
    if (!this.liveUpdateTimer) {
      return;
    }

    clearInterval(this.liveUpdateTimer);
    this.liveUpdateTimer = null;
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
