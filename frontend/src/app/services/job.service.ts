import { Injectable, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { CreateJobRequest, GroupedJobs, JobDetail, JobInfo, WatchPathEntry, CliExecution, CliOutputLine, RunnerStatus, CliSettings, JobOrderItem, ContextUsageSnapshot, CopilotModelCatalog, CliModelCatalog, CliType, CliUsageReport, QuotaReport, QuotaSnapshot, GitStatus, ClaudeSessionResponse, JobCommitDetail, SessionEventsResponse, ContinueMode, ContinueJobResponse, OrchestratorLogResponse, TokenSummary, TokenSummaryAggregate, TokenTimeline, OrchestratorSessionResponse, OrchestratorChatResponse, OrchestratorChatTurn, RunTimeline, RunCommitsResponse, RunFilesResponse, RunDiffResponse, RoadmapIntakeCandidate, RoadmapIntakeResponse, RoadmapIntakeConfirmResponse, JobScreenshotsResponse, WorkspaceScreenshotsResponse, ProjectTokenUsageSummary, ProjectTokenHeatmap, ProjectExpensiveJobsResponse, ProjectJobTokenDetail } from '../models/job.model';
import { ErrorDialogService } from './error-dialog.service';

type LaneKey = keyof GroupedJobs;
// ADR-0025: state strings use the new seven-lane order.
// ADR-0026: 1a-orchestrator-prep + 1b-needs-human-review join the catalog.
// ADR-0029: 3a-failed-pickup joins the catalog so optimistic reorders/moves
// targeting the loud-not-archived lane keep the same fast-path treatment as
// every other lane.
const STATE_TO_LANE: Record<string, LaneKey> = {
  '0-backlog': 'backlog',
  '1-preparation': 'preparation',
  '1a-orchestrator-prep': 'orchestratorPrep',
  '1b-needs-human-review': 'needsHumanReview',
  '2-ready': 'ready',
  '3-progress': 'progress',
  '3a-failed-pickup': 'failedPickup',
  '4-auto-review': 'autoReview',
  '5-human-review': 'humanReview',
  '6-completed': 'completed',
  '7-archive': 'archive'
};

@Injectable({ providedIn: 'root' })
export class JobService {
  private readonly baseUrl = '/api';
  private liveUpdateTimer: ReturnType<typeof setInterval> | null = null;

  // Eventual-consistency layer for drag/drop. The user-visible reorder
  // happens locally before the backend confirms (the previous round-trip
  // wait felt laggy and made consecutive drags clobber each other when a
  // silent poll resolved between drops). Three guards work together:
  //
  // - `mutationVersion` bumps on every optimistic edit. Silent polls
  //   captured the version at request start; if it has changed by the
  //   time the response arrives, we discard the response.
  // - `pendingPersistCount` counts in-flight POSTs (reorder/move). While
  //   non-zero, silent polls are rejected unconditionally — the backend
  //   has not yet seen the user's last action, so anything it returns is
  //   stale relative to the optimistic UI.
  // - `pendingGroupedSuppressUntil` extends the rejection window past the
  //   last POST response so the on-disk rewrite (`job.json` files) has
  //   time to materialise into the next /api/jobs/grouped snapshot.
  private mutationVersion = 0;
  private pendingPersistCount = 0;
  private pendingGroupedSuppressUntil = 0;
  private static readonly OPTIMISTIC_GRACE_MS = 1500;

  /** Caller invokes when sending a reorder/move POST, then again when the POST resolves. */
  beginOptimisticPersist(): void {
    this.pendingPersistCount++;
  }
  endOptimisticPersist(): void {
    if (this.pendingPersistCount > 0) this.pendingPersistCount--;
    this.pendingGroupedSuppressUntil = Date.now() + JobService.OPTIMISTIC_GRACE_MS;
  }

  readonly jobs = signal<JobInfo[]>([]);
  readonly grouped = signal<GroupedJobs>({ backlog: [], preparation: [], orchestratorPrep: [], needsHumanReview: [], ready: [], progress: [], failedPickup: [], review: [], autoReview: [], humanReview: [], completed: [], archive: [] });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly runnerStatus = signal<RunnerStatus>({ projects: {} });
  constructor(private http: HttpClient, private errorDialog: ErrorDialogService) {}

  refresh(silent = false): void {
    if (!silent) {
      this.loading.set(true);
      this.error.set(null);
    }

    const versionAtStart = this.mutationVersion;
    const acceptOptimisticTarget = () => {
      if (!silent) return true;
      if (this.mutationVersion !== versionAtStart) return false;
      if (this.pendingPersistCount > 0) return false;
      if (Date.now() < this.pendingGroupedSuppressUntil) return false;
      return true;
    };

    this.http.get<JobInfo[]>(`${this.baseUrl}/jobs`).subscribe({
      next: (jobs) => {
        if (acceptOptimisticTarget()) {
          this.jobs.set(jobs);
        }
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
        if (acceptOptimisticTarget()) {
          this.grouped.set(grouped);
        }
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

  /**
   * Apply a within-lane reorder to the local `grouped` signal immediately,
   * before the backend confirms. Idempotent: missing keys are dropped and
   * the existing lane order is preserved for any job not present in the
   * supplied list. Returns the previous lane snapshot so callers can roll
   * back on a failed POST.
   */
  applyOptimisticReorder(state: string, orderedKeys: { jobId: string; watchPath: string }[]): JobInfo[] | null {
    const lane = STATE_TO_LANE[state];
    if (!lane) return null;
    const current = this.grouped();
    const before = current[lane] ?? [];
    const byKey = new Map(before.map(j => [`${j.watchPath}::${j.id}`, j]));
    const reordered: JobInfo[] = [];
    const seen = new Set<string>();
    for (const k of orderedKeys) {
      const key = `${k.watchPath}::${k.jobId}`;
      const job = byKey.get(key);
      if (job && !seen.has(key)) {
        reordered.push(job);
        seen.add(key);
      }
    }
    // Preserve any lane members the caller didn't mention (defensive — e2e
    // happens in narrow trios, but production lanes can drift).
    for (const j of before) {
      const key = `${j.watchPath}::${j.id}`;
      if (!seen.has(key)) reordered.push(j);
    }
    this.grouped.set({ ...current, [lane]: reordered });
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = Date.now() + JobService.OPTIMISTIC_GRACE_MS;
    return before;
  }

  /**
   * Apply a cross-lane move to the local `grouped` signal immediately.
   * The card lands at the bottom of the target lane (matches the backend
   * convention that newly assigned `order` values are appended).
   */
  applyOptimisticMove(jobId: string, watchPath: string, targetState: string): { fromLane: LaneKey; before: JobInfo[]; toLane: LaneKey; toBefore: JobInfo[] } | null {
    const toLane = STATE_TO_LANE[targetState];
    if (!toLane) return null;
    const current = this.grouped();
    const key = `${watchPath}::${jobId}`;
    let fromLane: LaneKey | null = null;
    let moving: JobInfo | null = null;
    for (const k of Object.keys(current) as LaneKey[]) {
      const found = (current[k] ?? []).find(j => `${j.watchPath}::${j.id}` === key);
      if (found) { fromLane = k; moving = found; break; }
    }
    if (!fromLane || !moving) return null;
    const fromBefore = current[fromLane] ?? [];
    const toBefore = current[toLane] ?? [];
    const next: GroupedJobs = { ...current };
    next[fromLane] = fromBefore.filter(j => `${j.watchPath}::${j.id}` !== key);
    next[toLane] = fromLane === toLane ? next[toLane] : [...toBefore, { ...moving, state: targetState }];
    this.grouped.set(next);
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = Date.now() + JobService.OPTIMISTIC_GRACE_MS;
    return { fromLane, before: fromBefore, toLane, toBefore };
  }

  /** Roll back a failed optimistic reorder to the captured snapshot. */
  revertOptimisticReorder(state: string, before: JobInfo[]): void {
    const lane = STATE_TO_LANE[state];
    if (!lane) return;
    const current = this.grouped();
    this.grouped.set({ ...current, [lane]: before });
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = 0;
  }

  /** Roll back a failed optimistic cross-lane move. */
  revertOptimisticMove(snapshot: { fromLane: LaneKey; before: JobInfo[]; toLane: LaneKey; toBefore: JobInfo[] }): void {
    const current = this.grouped();
    const next: GroupedJobs = { ...current };
    next[snapshot.fromLane] = snapshot.before;
    if (snapshot.toLane !== snapshot.fromLane) next[snapshot.toLane] = snapshot.toBefore;
    this.grouped.set(next);
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = 0;
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

  // Tag registry + per-job tag mutation. Backlog-lane spec.
  listTags() {
    return this.http.get<import('../models/job.model').TagRegistryEntry[]>(`${this.baseUrl}/tags`);
  }

  createTag(req: { id?: string; label: string; color?: string; description?: string }) {
    return this.http.post<import('../models/job.model').TagRegistryEntry>(`${this.baseUrl}/tags`, req);
  }

  deleteTag(id: string) {
    return this.http.delete(`${this.baseUrl}/tags/${encodeURIComponent(id)}`);
  }

  setJobTags(jobId: string, tags: string[], watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/tags`,
      { tags },
      this.withWatchPath(watchPath));
  }

  setJobTaskType(jobId: string, taskType: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/task-type`,
      { taskType },
      this.withWatchPath(watchPath));
  }

  updateJobFile(jobId: string, fileName: string, content: string, watchPath?: string) {
    return this.http.put(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/files/${encodeURIComponent(fileName)}`, { content }, this.withWatchPath(watchPath));
  }

  reorderJobs(jobs: JobOrderItem[]) {
    return this.http.post(`${this.baseUrl}/jobs/reorder`, { jobs });
  }

  /**
   * "Do Next" from the detail view: ask the backend to atomically promote
   * this job to the head of its project's ready queue. Single round-trip,
   * no client-side knowledge of sibling jobs required.
   */
  moveJobToTop(jobId: string, watchPath?: string) {
    return this.http.post<{ position: number }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/move-to-top`,
      null,
      this.withWatchPath(watchPath)
    );
  }

  changeProject(jobId: string, targetWatchPath: string, watchPath?: string) {
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/change-project`, { targetWatchPath }, this.withWatchPath(watchPath));
  }

  deleteJob(jobId: string, watchPath?: string) {
    return this.http.delete(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}`, this.withWatchPath(watchPath));
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

  /** Per-job run timeline: ordered list of CLI invocations + aggregates. */
  getRunTimeline(jobId: string, watchPath?: string) {
    return this.http.get<RunTimeline>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs`, this.withWatchPath(watchPath));
  }

  /** Commits whose author date falls inside the given run's wall-clock window. */
  getRunCommits(jobId: string, runIndex: number, watchPath?: string) {
    return this.http.get<RunCommitsResponse>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs/${runIndex}/commits`, this.withWatchPath(watchPath));
  }

  /** Aggregated file list for one run's SHA range - drives the run git viewer's file tree. */
  getRunFiles(jobId: string, runIndex: number, watchPath?: string) {
    return this.http.get<RunFilesResponse>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs/${runIndex}/files`, this.withWatchPath(watchPath));
  }

  /** Unified diff for one path inside a run's SHA range. */
  getRunDiff(jobId: string, runIndex: number, path: string, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    const params = (opts.params as any) ?? {};
    return this.http.get<RunDiffResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs/${runIndex}/diff`,
      { ...opts, params: { ...params, path } }
    );
  }

  // CLI execution
  startJob(jobId: string, watchPath?: string, model?: string, cliType?: CliType) {
    const body: { model?: string; cliType?: CliType } = {};
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    return this.http.post<ContinueJobResponse>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/start`, body, this.withWatchPath(watchPath));
  }

  /**
   * Pause the running CLI for this job. The optional `reason` flows into
   * the backend's RunStatusClassifier:
   *   - 'user'     (default) - explicit Pause button. Resulting status is
   *                  'stopped'; the UI may show a small toast.
   *   - 'followup' - Pause-and-Send: the UI will immediately call
   *                  continueJob afterwards, so applyExecutionState should
   *                  not pop a modal for the in-flight 'stopped' frame.
   * The string is forwarded as ?reason=...; the backend coerces unknown
   * values to 'user'.
   */
  stopJob(jobId: string, watchPath?: string, reason: 'user' | 'followup' = 'user') {
    const base = this.withWatchPath(watchPath);
    const params = (base.params ?? new HttpParams()).set('reason', reason);
    return this.http.post(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/stop`, {}, { ...base, params });
  }

  continueJob(jobId: string, prompt: string, watchPath?: string, model?: string, cliType?: CliType, mode?: ContinueMode) {
    const body: { prompt: string; model?: string; cliType?: CliType; mode?: ContinueMode } = { prompt };
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    if (mode) body.mode = mode;
    return this.http.post<ContinueJobResponse>(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/continue`, body, this.withWatchPath(watchPath));
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

  // ── Quota caps: per-CLI per-window usage ceilings. The runner blocks
  // pickup and stops in-flight runs when usage crosses these caps so the
  // user keeps a buffer for ad-hoc work outside the orchestrator.
  getQuotaCaps() {
    return this.http.get<{ defaultCapPct: number; caps: Record<string, Record<string, number>> }>(
      `${this.baseUrl}/cli/quota/caps`
    );
  }

  setQuotaCap(cliType: CliType, windowLabel: string, capPct: number) {
    return this.http.put<{ defaultCapPct: number; caps: Record<string, Record<string, number>> }>(
      `${this.baseUrl}/cli/quota/caps`,
      { cliType, windowLabel, capPct }
    );
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

  /**
   * Read the orchestrator's chronological feed for one project: decisions
   * made, actions taken (queued follow-ups, watchdog kills, recovery
   * fallbacks), and eventually user interventions. Backed by
   * `<watchPath>/.orchestrator/orchestrator.jsonl`.
   */
  getOrchestratorLog(projectName: string) {
    return this.http.get<OrchestratorLogResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-log`
    );
  }

  /**
   * Read the per-project list of 4-review jobs whose latest CLI output
   * carries an unresolved [[TASK_NEEDS_INPUT]] sentinel. Drives the
   * project-detail banner: when non-empty, the orchestrator owes a
   * decision (or is about to make one in the next tick).
   */
  getReviewDecisionsPending(projectName: string) {
    return this.http.get<{ project: string; items: { jobId: string; title: string; reason: string | null }[] }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/review-decisions-pending`
    );
  }

  /**
   * ADR-0027: read the *live, in-progress* decision sentinel(s) the named
   * project's running job has emitted. Distinct from
   * getReviewDecisionsPending (post-run, scoped to 4-auto-review): this
   * surface fires while the job is still in 3-progress, the moment the
   * agent prints [[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]] to stdout.
   */
  getRunnerPendingDecisions(projectName: string) {
    return this.http.get<{ project: string; items: { jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }[] }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/pending-decisions`
    );
  }

  /** All per-project settings (auto-commit, runner mode, orchestrator model). */
  getAllProjectSettings() {
    return this.http.get<{ [project: string]: { autoCommit: boolean; runnerMode: string | null; orchestratorModel: string | null } }>(
      `${this.baseUrl}/projects/settings`
    );
  }

  setProjectAutoCommit(projectName: string, enabled: boolean) {
    return this.http.put(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/auto-commit`, { enabled });
  }

  setProjectOrchestratorModel(projectName: string, model: string | null) {
    return this.http.put(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/orchestrator-model`, { model });
  }

  /** ADR-0026: read the per-project orchestrator-prep autonomy level (0..4). */
  getProjectAutonomyLevel(projectName: string) {
    return this.http.get<{ level: number }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/autonomy`);
  }

  /** ADR-0026: write the per-project orchestrator-prep autonomy level. Server clamps to 0..4. */
  setProjectAutonomyLevel(projectName: string, level: number) {
    return this.http.put<{ level: number }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/autonomy`,
      { level }
    );
  }

  /**
   * Read the long-lived orchestrator session for a project. Returns
   * `{ project, session: null }` when no session has been booted yet
   * (e.g. boot is still in flight after app start, or boot failed).
   */
  getOrchestratorSession(projectName: string) {
    return this.http.get<OrchestratorSessionResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-session`
    );
  }

  /**
   * Read the singleton global orchestrator session. The wire shape mirrors
   * the per-project endpoint (`{ project, session }`) so the UI can reuse
   * the same renderer; `project` comes back as the literal string
   * "(global)" so the user sees that this is the cross-project session.
   */
  getGlobalOrchestratorSession() {
    return this.http.get<OrchestratorSessionResponse>(
      `${this.baseUrl}/runner/global/orchestrator-session`
    );
  }

  /**
   * Per-project token rollup. Returns total amounts, per-model
   * breakdown, and a theoretical API-cost estimate. The cost is a
   * comparison against Anthropic's published API rates, not the user's
   * actual bill - CLI subscriptions are billed separately. The
   * frontend renders the disclaimer prominently.
   */
  getTokenSummary(projectName: string) {
    return this.http.get<TokenSummary>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/token-summary`
    );
  }

  /**
   * Workspace-wide token aggregate. Forces a fresh scan across all
   * watched projects and writes the result to the on-disk cache, so the
   * next call to {@link getTokenSummaryAggregateCached} can return it
   * instantly. Cheap (reads JSONL files only); safe to poll.
   */
  getTokenSummaryAggregate() {
    return this.http.get<TokenSummaryAggregate>(
      `${this.baseUrl}/runner/token-summary-aggregate`
    );
  }

  /**
   * Cache-only read of the workspace-wide aggregate. Returns immediately
   * with the on-disk snapshot without re-scanning the orchestrator logs.
   * The status-bar usage modal calls this on first paint so the user
   * sees real numbers before the live aggregator finishes; 204 No Content
   * means there is no cached snapshot yet.
   */
  getTokenSummaryAggregateCached() {
    return this.http.get<TokenSummaryAggregate>(
      `${this.baseUrl}/runner/token-summary-aggregate/cached`,
      { observe: 'response' }
    );
  }

  /**
   * Workspace-wide token timeline: one cell per (project, time-bucket).
   * `windowHours` accepts {1, 6, 24, 168}; `bucketMinutes` accepts
   * {5, 15, 60}. Out-of-range values are silently snapped to the
   * defaults by the backend.
   */
  getWorkspaceTokensTimeline(windowHours: number, bucketMinutes: number) {
    let params = new HttpParams()
      .set('windowHours', String(windowHours))
      .set('bucketMinutes', String(bucketMinutes));
    return this.http.get<TokenTimeline>(
      `${this.baseUrl}/workspace/tokens/timeline`,
      { params }
    );
  }

  /**
   * Project Token Usage panel (slice 8 of the quality-system mockup).
   * Lifetime + last-24h totals plus the Job / Supporting / Orchestrator
   * category split.
   */
  getProjectTokenUsageSummary(projectName: string) {
    return this.http.get<ProjectTokenUsageSummary>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/summary`,
    );
  }

  /**
   * Per-job × per-day heatmap. `days` accepts up to 90; the backend
   * silently snaps out-of-range values to {1..90}.
   */
  getProjectTokenUsageHeatmap(projectName: string, days = 30) {
    const params = new HttpParams().set('days', String(days));
    return this.http.get<ProjectTokenHeatmap>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/heatmap`,
      { params },
    );
  }

  /**
   * Top N jobs by total tokens for the panel's "expensive jobs" list.
   * `limit` defaults to 10, capped at 50.
   */
  getProjectExpensiveJobs(projectName: string, limit = 10) {
    const params = new HttpParams().set('limit', String(limit));
    return this.http.get<ProjectExpensiveJobsResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/expensive`,
      { params },
    );
  }

  /**
   * Per-run breakdown for one job: every orchestrator call attributed to
   * the job, with deltas vs. the previous call. Drives the drill-down.
   */
  getProjectJobTokenDetail(projectName: string, jobId: string) {
    return this.http.get<ProjectJobTokenDetail>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/token-usage/job/${encodeURIComponent(jobId)}`,
    );
  }

  /**
   * Per-job screenshot listing. Walks `<job>/results/` (recursive),
   * captioned by spec/folder, with pass/fail status from the
   * Playwright harvest index when available. Drives the protocol
   * pane's screenshot strip.
   */
  getJobScreenshots(jobId: string, watchPath?: string | null) {
    let params = new HttpParams();
    if (watchPath) params = params.set('watchPath', watchPath);
    return this.http.get<JobScreenshotsResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/screenshots`,
      { params }
    );
  }

  /**
   * Workspace-wide visual evidence reel: every `<job>/results/`
   * screenshot whose mtime falls inside the requested window,
   * newest-first. Optionally narrowed to a single project name.
   */
  getWorkspaceScreenshots(windowHours: number, projectFilter?: string | null) {
    let params = new HttpParams().set('windowHours', String(windowHours));
    if (projectFilter) params = params.set('projectFilter', projectFilter);
    return this.http.get<WorkspaceScreenshotsResponse>(
      `${this.baseUrl}/workspace/screenshots`,
      { params }
    );
  }

  /**
   * Read the per-project orchestrator chat log: turns between the user
   * and the global orchestrator session, scoped to one project tab.
   * Backed by `<watchPath>/.orchestrator/orchestrator-chat.jsonl`.
   */
  getOrchestratorChat(projectName: string) {
    return this.http.get<OrchestratorChatResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-chat`
    );
  }

  /**
   * Send a user message to the project's orchestrator chat. The backend
   * resumes the global orchestrator session, persists both user and
   * orchestrator turns, and returns the orchestrator's reply turn.
   */
  sendOrchestratorChat(
    projectName: string,
    body: { text: string; attachments?: { alt: string; relativePath: string }[] }
  ) {
    return this.http.post<{ project: string; reply: OrchestratorChatTurn }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-chat`,
      body
    );
  }

  /**
   * Upload one image to the project's chat-attachments folder so the
   * subsequent `sendOrchestratorChat` call can reference it by its
   * relative path. Multipart/form-data with `file` field.
   */
  uploadOrchestratorChatAttachment(projectName: string, file: File) {
    const form = new FormData();
    form.append('file', file, file.name || 'image.png');
    return this.http.post<{ fileName: string; relativePath: string; url: string }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-chat/attachments`,
      form
    );
  }

  /**
   * Run the roadmap-intake splitter against a free-text dump. Returns a
   * candidate list with no side effects; the user reviews them before
   * confirming.
   */
  splitRoadmapIntake(text: string, watchPath: string) {
    return this.http.post<RoadmapIntakeResponse>(
      `${this.baseUrl}/roadmap/intake`,
      { text, watchPath }
    );
  }

  /**
   * Materialise reviewed roadmap-intake candidates as job folders in
   * <c>1-preparation</c>. Intake never lands jobs in <c>2-ready</c>.
   */
  confirmRoadmapIntake(watchPath: string, candidates: RoadmapIntakeCandidate[]) {
    return this.http.post<RoadmapIntakeConfirmResponse>(
      `${this.baseUrl}/roadmap/intake/confirm`,
      { watchPath, candidates }
    );
  }

  /**
   * One-shot Haiku call that turns a free-text prompt into a short
   * imperative English title. Drives the "Generate" button on the
   * Create-task dialog. Returns immediately when the prompt is empty.
   */
  generateTaskTitle(prompt: string) {
    return this.http.post<{ title: string }>(
      `${this.baseUrl}/title/generate`,
      { prompt }
    );
  }

  /**
   * One-shot Haiku call that returns three artefacts derived from the
   * user's free-text prompt: a refined prompt, a one-line intent, and
   * up to five topical tags. Drives the "Enhance" button on the
   * Create-task dialog. Pure preview - no side effects.
   */
  enhancePrompt(prompt: string) {
    return this.http.post<{ refinedPrompt: string; intent: string; tags: string[] }>(
      `${this.baseUrl}/prompt/enhance`,
      { prompt }
    );
  }

  /**
   * Override an orchestrator decision. Appends an intervention entry to
   * the feed, and (when `jobId` is provided) routes `newDirection`
   * through the Continue path as a Steer-mode follow-up.
   */
  overrideOrchestratorEntry(
    projectName: string,
    body: { originalTs: string; jobId: string; newDirection: string }
  ) {
    return this.http.post<{ applied: boolean; error?: string; note?: string }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-log/override`,
      body
    );
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
