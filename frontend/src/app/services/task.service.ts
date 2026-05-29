import { Injectable, signal, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import type {
  CreateJobRequest,
  GroupedJobs,
  JobArtifactsResponse,
  JobDetail,
  JobInfo,
  WatchPathEntry,
  RegistryWorkspaceListItem,
  CliOutputLine,
  RunnerStatus,
  CliSettings,
  JobOrderItem,
  ContextUsageSnapshot,
  CliType,
  ContinueMode,
  ContinueJobResponse,
  ProjectSnapshot,
} from '../models/task.model';
import type { ClaudeSessionResponse } from '../features/claude';
import type { CopilotModelCatalog, CliModelCatalog, CliUsageReport } from '../features/cli';
import type { GitFileChange, GitStatus, JobCommitDetail } from '../features/git';
import type {
  OrchestratorLogResponse,
  OrchestratorSessionResponse,
  OrchestratorChatResponse,
  OrchestratorChatTurn,
} from '../features/orchestrator';
import type {
  ProjectChatScrollResponse,
  ProjectChatSearchResponse,
  ProjectChatTurnResponse,
  ProjectChatStatsResponse,
} from '../features/project-chat';
import type {
  ProjectTokenUsageSummary,
  ProjectTokenHeatmap,
  ProjectExpensiveJobsResponse,
  ProjectJobTokenDetail,
} from '../features/project-token-usage';
import type {
  RunTimeline,
  RunCommitsResponse,
  RunFilesResponse,
  RunDiffResponse,
} from '../features/run-timeline';
import type { JobScreenshotsResponse, WorkspaceScreenshotsResponse } from '../features/screenshots';
import type { AgentWorkSummary, SessionEventsResponse } from '../features/session-events';
import type { RegressionRadarResult } from '../features/regression-radar';
import { ErrorDialogService } from './error-dialog.service';

/** One row in the code-review list endpoint response (see backend `CodeReviewListEntry`). */
export interface CodeReviewListEntry {
  fileName: string;
  verdict: string;
  summary: string;
  model: string;
  cliType: string;
  commit?: string | null;
  runAt: string;
}

/** Reply from `POST /api/jobs/{id}/code-review` (see backend `CodeReviewStepEndpointResponse`). */
export interface CodeReviewRunResponse {
  fileName: string;
  verdict: string;
  summary: string;
  model: string;
  cliType: string;
  commit?: string | null;
  concernTagId?: string | null;
  durationMs: number;
  startedAt: string;
}

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
  '7-archive': 'archive',
};

@Injectable({ providedIn: 'root' })
export class JobService {
  private http = inject(HttpClient);
  private errorDialog = inject(ErrorDialogService);

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
  readonly grouped = signal<GroupedJobs>({
    backlog: [],
    preparation: [],
    orchestratorPrep: [],
    needsHumanReview: [],
    ready: [],
    progress: [],
    failedPickup: [],
    review: [],
    autoReview: [],
    humanReview: [],
    completed: [],
    archive: [],
  });
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly runnerStatus = signal<RunnerStatus>({ projects: {} });

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
        const message =
          err.status === 0
            ? 'Backend not reachable — is the API running on localhost:5030?'
            : err.error?.error || err.message || 'Failed to load jobs';

        this.error.set(message);
        if (!silent) {
          this.errorDialog.show(err, {
            title: 'Failed to load jobs',
            fallbackMessage: 'Failed to load jobs',
            source: 'Dashboard refresh',
          });
        }
        this.loading.set(false);
      },
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
            source: 'Board refresh',
          });
        }
      },
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
  applyOptimisticReorder(
    state: string,
    orderedKeys: { jobId: string; watchPath: string }[],
  ): JobInfo[] | null {
    const lane = STATE_TO_LANE[state];
    if (!lane) return null;
    const current = this.grouped();
    const before = current[lane] ?? [];
    const byKey = new Map(before.map((j) => [`${j.watchPath}::${j.id}`, j]));
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
   * When `insertAt` is provided, the card lands at that 0-based slot in
   * the target lane; otherwise it appends at the bottom. The slot path
   * matches the drag-and-drop drop-position contract: the backend
   * rewrites every sibling's `order` field so the resulting position is
   * stable across silent polls.
   */
  applyOptimisticMove(
    jobId: string,
    watchPath: string,
    targetState: string,
    insertAt?: number,
  ): { fromLane: LaneKey; before: JobInfo[]; toLane: LaneKey; toBefore: JobInfo[] } | null {
    const toLane = STATE_TO_LANE[targetState];
    if (!toLane) return null;
    const current = this.grouped();
    const key = `${watchPath}::${jobId}`;
    let fromLane: LaneKey | null = null;
    let moving: JobInfo | null = null;
    for (const k of Object.keys(current) as LaneKey[]) {
      const found = (current[k] ?? []).find((j) => `${j.watchPath}::${j.id}` === key);
      if (found) {
        fromLane = k;
        moving = found;
        break;
      }
    }
    if (!fromLane || !moving) return null;
    // Same-lane "move" is a no-op at the data layer: the previous shape
    // filtered the card out of `fromLane` and then aliased `toLane` to the
    // already-filtered array, so the card vanished from its lane until the
    // next poll repainted. Bail out here so the column-level drop handler
    // (which used to fall into this path when a card was released over a
    // sibling card instead of a drop-zone) cannot make the card disappear,
    // even if a future caller forgets the sourceState guard.
    if (fromLane === toLane) return null;
    const fromBefore = current[fromLane] ?? [];
    const toBefore = current[toLane] ?? [];
    const next: GroupedJobs = { ...current };
    next[fromLane] = fromBefore.filter((j) => `${j.watchPath}::${j.id}` !== key);
    const movedCard = { ...moving, state: targetState };
    if (typeof insertAt === 'number') {
      const slot = Math.max(0, Math.min(insertAt, toBefore.length));
      next[toLane] = [...toBefore.slice(0, slot), movedCard, ...toBefore.slice(slot)];
    } else {
      next[toLane] = [...toBefore, movedCard];
    }
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
  revertOptimisticMove(snapshot: {
    fromLane: LaneKey;
    before: JobInfo[];
    toLane: LaneKey;
    toBefore: JobInfo[];
  }): void {
    const current = this.grouped();
    const next: GroupedJobs = { ...current };
    next[snapshot.fromLane] = snapshot.before;
    if (snapshot.toLane !== snapshot.fromLane) next[snapshot.toLane] = snapshot.toBefore;
    this.grouped.set(next);
    this.mutationVersion++;
    this.pendingGroupedSuppressUntil = 0;
  }

  private withWatchPath(watchPath?: string): { params?: HttpParams } {
    return watchPath ? { params: new HttpParams().set('watchPath', watchPath) } : {};
  }

  private withWatchPathAndPath(watchPath: string | undefined, path: string): { params: HttpParams } {
    const base = this.withWatchPath(watchPath);
    return { params: (base.params ?? new HttpParams()).set('path', path) };
  }

  getDetail(jobId: string, watchPath?: string) {
    return this.http.get<JobDetail>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}`,
      this.withWatchPath(watchPath),
    );
  }

  updateState(jobId: string, state: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/state`,
      { targetState: state },
      this.withWatchPath(watchPath),
    );
  }

  moveJob(jobId: string, targetState: string, watchPath?: string, targetIndex?: number) {
    const body: { targetState: string; targetIndex?: number } = { targetState };
    if (typeof targetIndex === 'number') body.targetIndex = targetIndex;
    return this.http.post(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/move`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  getWatchPaths() {
    return this.http.get<WatchPathEntry[]>(`${this.baseUrl}/watch-paths`);
  }

  /**
   * F45a / ADR-0042 — list workspaces with their embedded projects.
   * Pass `includeArchived: true` to surface archived projects too
   * (default omits them).
   */
  getRegistryWorkspaces(opts?: { includeArchived?: boolean }) {
    const params = opts?.includeArchived
      ? new HttpParams().set('includeArchived', 'true')
      : undefined;
    return this.http.get<RegistryWorkspaceListItem[]>(`${this.baseUrl}/workspaces`, { params });
  }

  /** F45b — create a workspace. Returns the new record. */
  createRegistryWorkspace(displayName: string, color?: string | null) {
    return this.http.post<{ id: string; displayName: string }>(
      `${this.baseUrl}/workspaces`, { displayName, color: color ?? null });
  }

  /** F45b — patch a workspace (rename / color edit). */
  updateRegistryWorkspace(id: string, patch: { displayName?: string; color?: string | null; clearColor?: boolean }) {
    return this.http.put(`${this.baseUrl}/workspaces/${encodeURIComponent(id)}`, patch);
  }

  /** F45b — reorder a workspace one slot up or down. */
  reorderRegistryWorkspace(id: string, direction: -1 | 1) {
    return this.http.post(`${this.baseUrl}/workspaces/${encodeURIComponent(id)}/reorder`, { direction });
  }

  /**
   * F66 — delete a workspace. Backend refuses the default workspace (409)
   * but auto-rehomes any still-assigned projects onto `ws-default` and
   * returns the list of rehomed project ids so the UI can surface a
   * "moved N project(s) to Default" toast.
   */
  deleteRegistryWorkspace(id: string) {
    return this.http.delete<{ deletedId: string; rehomedProjectIds: string[] }>(
      `${this.baseUrl}/workspaces/${encodeURIComponent(id)}`);
  }

  /** F45b — patch a project record (rename / short-code / color / workspace / archived). */
  updateRegistryProject(projId: string, patch: {
    displayName?: string;
    shortCode?: string;
    color?: string | null;
    clearColor?: boolean;
    workspaceId?: string;
    archived?: boolean;
  }) {
    return this.http.put(`${this.baseUrl}/projects/${encodeURIComponent(projId)}`, patch);
  }

  /**
   * Create a new (empty) workspace. The backend slugs the name into a
   * folder under `{TaskRepository}/projects/{slug}`, materialises the
   * directory, and appends a `WatchPathEntry` to `appsettings.Local.json`.
   * Returns the resolved entry; throws via HttpClient on validation
   * (400) or name collision (409).
   */
  createWorkspace(name: string) {
    return this.http.post<WatchPathEntry>(`${this.baseUrl}/watch-paths`, { name });
  }

  /**
   * Remove a workspace by name. The backend refuses (HTTP 409) when the
   * workspace still contains job folders, returning the live `jobCount`
   * in the error body so the UI can render a clear "still has N jobs"
   * message. The on-disk folder is left in place so a re-create with
   * the same name is reversible.
   */
  deleteWorkspace(name: string) {
    return this.http.delete<{ name: string }>(
      `${this.baseUrl}/watch-paths/${encodeURIComponent(name)}`,
    );
  }

  createJob(req: CreateJobRequest) {
    return this.http.post<{ id: string }>(`${this.baseUrl}/jobs`, req);
  }

  // Tag registry + per-job tag mutation. Backlog-lane spec.
  listTags() {
    return this.http.get<import('../models/task.model').TagRegistryEntry[]>(`${this.baseUrl}/tags`);
  }

  createTag(req: { id?: string; label: string; color?: string; description?: string }) {
    return this.http.post<import('../models/task.model').TagRegistryEntry>(
      `${this.baseUrl}/tags`,
      req,
    );
  }

  deleteTag(id: string) {
    return this.http.delete(`${this.baseUrl}/tags/${encodeURIComponent(id)}`);
  }

  setJobTags(jobId: string, tags: string[], watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/tags`,
      { tags },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * List the code-review-step artifacts for one job. Each entry carries the
   * frontmatter fields (verdict, summary, model, runAt) so the panel can
   * render rows without fetching every MD body.
   */
  listCodeReviews(jobId: string, watchPath?: string) {
    return this.http.get<{ entries: CodeReviewListEntry[] }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/code-review/list`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Run one user-triggered code-review step against the job's most recent
   * commit. Synchronous: the response arrives once the underlying CLI call
   * has finished, so the UI can keep a spinner up for the duration.
   */
  runCodeReview(
    jobId: string,
    body: { model?: string; cliType?: string; commit?: string },
    watchPath?: string,
  ) {
    return this.http.post<CodeReviewRunResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/code-review`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Read one code-review MD body. Used by the panel to expand a row inline.
   */
  readCodeReview(jobId: string, fileName: string, watchPath?: string) {
    return this.http.get<{ fileName: string; content: string }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/code-review/${encodeURIComponent(fileName)}`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Acknowledge or un-acknowledge a review-evidence finding. Append-only:
   * the backend writes a new line into `results/review-evidence.jsonl`
   * with the same `id` and the updated `acknowledged` flag.
   */
  acknowledgeReviewEvidence(
    jobId: string,
    evidenceId: string,
    acknowledged: boolean,
    watchPath?: string,
  ) {
    return this.http.post(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/review-evidence/${encodeURIComponent(evidenceId)}/acknowledge`,
      { acknowledged },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Create a queued follow-up task in the same project, prefilled with the
   * finding's title + body + linked artifacts/file refs. Returns the new
   * job's id so the UI can route the user to the new card.
   */
  createReviewEvidenceFollowup(
    jobId: string,
    evidenceId: string,
    body: { title?: string; targetState?: string },
    watchPath?: string,
  ) {
    return this.http.post<{ jobId: string; targetState: string }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/review-evidence/${encodeURIComponent(evidenceId)}/follow-up`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  setJobTaskType(jobId: string, taskType: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/task-type`,
      { taskType },
      this.withWatchPath(watchPath),
    );
  }

  updateJobFile(jobId: string, fileName: string, content: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/files/${encodeURIComponent(fileName)}`,
      { content },
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Lists every `.md` file in the job root (status.md excluded). Drives the
   * Files tab in the detail view; cheap manifest call so the tab can fetch
   * individual file contents lazily through {@link readJobFile}.
   */
  listJobArtifacts(jobId: string, watchPath?: string) {
    return this.http.get<JobArtifactsResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/artifacts`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Reads one file from the job root. Used by the Files tab to lazily
   * fetch the content of an aspect / note / other markdown card when the
   * user expands it. Returns the body as plain text.
   */
  readJobFile(jobId: string, fileName: string, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    return this.http.get(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/files/${encodeURIComponent(fileName)}`,
      { ...opts, responseType: 'text' },
    );
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
      this.withWatchPath(watchPath),
    );
  }

  changeProject(jobId: string, targetWatchPath: string, watchPath?: string) {
    return this.http.post(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/change-project`,
      { targetWatchPath },
      this.withWatchPath(watchPath),
    );
  }

  deleteJob(jobId: string, watchPath?: string) {
    return this.http.delete(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}`,
      this.withWatchPath(watchPath),
    );
  }

  // Git
  getGitStatus(jobId: string, watchPath?: string) {
    return this.http.get<GitStatus>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/status`,
      this.withWatchPath(watchPath),
    );
  }

  getGitDiff(jobId: string, path: string | null, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    const params = (opts.params as Record<string, string> | undefined) ?? {};
    if (path) params['path'] = path;
    return this.http.get(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/diff`, {
      ...opts,
      params,
      responseType: 'text',
    });
  }

  commitJob(jobId: string, message: string, watchPath?: string) {
    return this.http.post<{ sha?: string }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/commit`,
      { message },
      this.withWatchPath(watchPath),
    );
  }

  generateCommitMessage(jobId: string, watchPath?: string) {
    return this.http.post<{ message: string }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/git/generate-message`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  // Per-task commit snapshot — what the auto-commit recorded on the
  // progress→review transition, plus a live re-derivation of the file list.
  getJobCommit(jobId: string, watchPath?: string) {
    return this.http.get<JobCommitDetail>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/commit`,
      this.withWatchPath(watchPath),
    );
  }

  getJobCommitDiff(jobId: string, path: string | null, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    const params = (opts.params as Record<string, string> | undefined) ?? {};
    if (path) params['path'] = path;
    return this.http.get(`${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/commit/diff`, {
      ...opts,
      params,
      responseType: 'text',
    });
  }

  /**
   * File list for a specific commit in this task's commit chain. Validates
   * server-side that the SHA actually belongs to this job, so the endpoint
   * cannot be coaxed into showing arbitrary repository history.
   */
  getJobCommitFilesBySha(jobId: string, sha: string, watchPath?: string) {
    return this.http.get<{ sha: string; files: GitFileChange[] }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/commits/${encodeURIComponent(sha)}/files`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Diff text for a specific commit in this task's commit chain, optionally
   * scoped to one path. Drives the multi-commit detail view when the user
   * picks any commit other than the latest.
   */
  getJobCommitDiffBySha(jobId: string, sha: string, path: string | null, watchPath?: string) {
    const opts = this.withWatchPath(watchPath);
    const params = (opts.params as Record<string, string> | undefined) ?? {};
    if (path) params['path'] = path;
    return this.http.get<{ diff: string }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/commits/${encodeURIComponent(sha)}/diff`,
      { ...opts, params },
    );
  }

  openInVsCode(jobId: string, watchPath?: string) {
    return this.http.post(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/open-in-vscode`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  getClaudeSessionInfo(jobId: string, watchPath?: string) {
    return this.http.get<ClaudeSessionResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/claude/session-info`,
      this.withWatchPath(watchPath),
    );
  }

  /** Per-job session-event log: start/continue/recovery rows + sessionChain. */
  getSessionEvents(jobId: string, watchPath?: string) {
    return this.http.get<SessionEventsResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/session-events`,
      this.withWatchPath(watchPath),
    );
  }

  /**
   * Per-job derived view of "what the agent actually did" - folded from
   * session-events.jsonl + tool-calls.jsonl. Drives the Overview tab's
   * Agent Work block.
   */
  getAgentWorkSummary(jobId: string, watchPath?: string) {
    return this.http.get<AgentWorkSummary>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/agent-work-summary`,
      this.withWatchPath(watchPath),
    );
  }

  /** Per-job run timeline: ordered list of CLI invocations + aggregates. */
  getRunTimeline(jobId: string, watchPath?: string) {
    return this.http.get<RunTimeline>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs`,
      this.withWatchPath(watchPath),
    );
  }

  /** Commits whose author date falls inside the given run's wall-clock window. */
  getRunCommits(jobId: string, runIndex: number, watchPath?: string) {
    return this.http.get<RunCommitsResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs/${runIndex}/commits`,
      this.withWatchPath(watchPath),
    );
  }

  /** Aggregated file list for one run's SHA range - drives the run git viewer's file tree. */
  getRunFiles(jobId: string, runIndex: number, watchPath?: string) {
    return this.http.get<RunFilesResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs/${runIndex}/files`,
      this.withWatchPath(watchPath),
    );
  }

  /** Unified diff for one path inside a run's SHA range. */
  getRunDiff(jobId: string, runIndex: number, path: string, watchPath?: string) {
    return this.http.get<RunDiffResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/runs/${runIndex}/diff`,
      this.withWatchPathAndPath(watchPath, path),
    );
  }

  getRegressionRadar(jobId: string, watchPath?: string) {
    return this.http.get<RegressionRadarResult>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/regression-radar`,
      this.withWatchPath(watchPath),
    );
  }

  // CLI execution
  startJob(jobId: string, watchPath?: string, model?: string, cliType?: CliType) {
    const body: { model?: string; cliType?: CliType } = {};
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    return this.http.post<ContinueJobResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/start`,
      body,
      this.withWatchPath(watchPath),
    );
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
    return this.http.post(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/stop`,
      {},
      { ...base, params },
    );
  }

  continueJob(
    jobId: string,
    prompt: string,
    watchPath?: string,
    model?: string,
    cliType?: CliType,
    mode?: ContinueMode,
  ) {
    const body: { prompt: string; model?: string; cliType?: CliType; mode?: ContinueMode } = {
      prompt,
    };
    if (model) body.model = model;
    if (cliType) body.cliType = cliType;
    if (mode) body.mode = mode;
    return this.http.post<ContinueJobResponse>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/continue`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  setJobModel(jobId: string, model: string | null, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/model`,
      { model },
      this.withWatchPath(watchPath),
    );
  }

  setJobCliType(jobId: string, cliType: CliType, watchPath?: string, useOwnSession?: boolean) {
    const body: { cliType: CliType; useOwnSession?: boolean } = { cliType };
    if (useOwnSession !== undefined) body.useOwnSession = useOwnSession;
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/cli-type`,
      body,
      this.withWatchPath(watchPath),
    );
  }

  getCliModelCatalog(cliType: CliType, refresh = false) {
    const params = refresh ? new HttpParams().set('refresh', 'true') : undefined;
    return this.http.get<CliModelCatalog>(
      `${this.baseUrl}/cli/${cliType}/models`,
      params ? { params } : {},
    );
  }

  getCliUsageReport() {
    return this.http.get<CliUsageReport>(`${this.baseUrl}/cli/usage`);
  }

  // Cycle 10d: quota / subscription rate-limit reporting moved to
  // QuotaApiService (`features/quota/services/`). Caller migration:
  // `inject(QuotaApiService)` instead of `inject(JobService)` + the
  // method names stay identical.

  setJobTitle(jobId: string, title: string, watchPath?: string) {
    return this.http.put(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/title`,
      { title },
      this.withWatchPath(watchPath),
    );
  }

  getModelCatalog() {
    return this.http.get<CopilotModelCatalog>(`${this.baseUrl}/settings/cli/models`);
  }

  getJobOutput(jobId: string, watchPath?: string) {
    return this.http.get<CliOutputLine[]>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/output`,
      this.withWatchPath(watchPath),
    );
  }

  refreshContextUsage(jobId: string, watchPath?: string) {
    return this.http.post<ContextUsageSnapshot>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/context-usage/refresh`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  regenerateSummary(jobId: string, watchPath?: string) {
    return this.http.post(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/summary/regenerate`,
      {},
      this.withWatchPath(watchPath),
    );
  }

  /**
   * One-shot interim summary while a run is in flight. Calls Haiku
   * against the live cli-output.log and returns the markdown directly;
   * status.md on disk is left untouched so the post-run summary still
   * owns it. Used by the `📊 Interim status` button in the protocol pane.
   */
  requestInterimSummary(jobId: string, watchPath?: string) {
    return this.http.post<{ markdown: string; durationMs: number }>(
      `${this.baseUrl}/jobs/${encodeURIComponent(jobId)}/summary/interim`,
      {},
      this.withWatchPath(watchPath),
    );
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
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-log`,
    );
  }

  /**
   * Read the per-project list of 4-review jobs whose latest CLI output
   * carries an unresolved [[TASK_NEEDS_INPUT]] sentinel. Drives the
   * project-detail banner: when non-empty, the orchestrator owes a
   * decision (or is about to make one in the next tick).
   */
  getReviewDecisionsPending(projectName: string) {
    return this.http.get<{
      project: string;
      items: { jobId: string; title: string; reason: string | null }[];
    }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/review-decisions-pending`);
  }

  /**
   * ADR-0027: read the *live, in-progress* decision sentinel(s) the named
   * project's running job has emitted. Distinct from
   * getReviewDecisionsPending (post-run, scoped to 4-auto-review): this
   * surface fires while the job is still in 3-progress, the moment the
   * agent prints [[TASK_NEEDS_INPUT]] / [[TASK_BLOCKED]] to stdout.
   */
  getRunnerPendingDecisions(projectName: string) {
    return this.http.get<{
      project: string;
      items: {
        jobId: string;
        title: string;
        kind: string;
        reason: string | null;
        detectedAt: string;
      }[];
    }>(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/pending-decisions`);
  }

  /**
   * Cycle 5: single-round-trip per-project snapshot. Returns settings,
   * runner status, orchestrator log tail (last 5), orchestrator session,
   * post-run pending review decisions, and live runner pending decisions
   * in one response. project-detail.refreshAll uses this to replace the
   * 6+ parallel polled GETs that pre-Cycle-5 produced ~42 requests every
   * 10 s of idle on this panel alone. Standalone endpoints stay live for
   * other consumers.
   */
  getProjectSnapshot(projectName: string) {
    return this.http.get<ProjectSnapshot>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/snapshot`,
    );
  }

  repairProjectQueueHealth(projectName: string) {
    return this.http.post<{
      project: string;
      moved: unknown[];
      failed: unknown[];
      queueHealth: ProjectSnapshot['queueHealth'];
    }>(`${this.baseUrl}/projects/${encodeURIComponent(projectName)}/queue-health/repair`, {});
  }

  /** All per-project settings (auto-commit, auto-push, runner mode, orchestrator model). */
  getAllProjectSettings() {
    return this.http.get<
      Record<
        string,
        {
          autoCommit: boolean;
          autoPushStrategy: 'never' | 'on-completed' | 'always-immediate';
          runnerMode: string | null;
          orchestratorModel: string | null;
        }
      >
    >(`${this.baseUrl}/projects/settings`);
  }

  setProjectAutoCommit(projectName: string, enabled: boolean) {
    return this.http.put(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/auto-commit`,
      { enabled },
    );
  }

  setProjectAutoPushStrategy(
    projectName: string,
    strategy: 'never' | 'on-completed' | 'always-immediate',
  ) {
    return this.http.put(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/auto-push-strategy`,
      { strategy },
    );
  }

  setProjectOrchestratorModel(projectName: string, model: string | null) {
    return this.http.put(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/orchestrator-model`,
      { model },
    );
  }

  /** ADR-0026: read the per-project orchestrator-prep autonomy level (0..4). */
  getProjectAutonomyLevel(projectName: string) {
    return this.http.get<{ level: number }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/autonomy`,
    );
  }

  /** ADR-0026: write the per-project orchestrator-prep autonomy level. Server clamps to 0..4. */
  setProjectAutonomyLevel(projectName: string, level: number) {
    return this.http.put<{ level: number }>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/autonomy`,
      { level },
    );
  }

  /**
   * Read the long-lived orchestrator session for a project. Returns
   * `{ project, session: null }` when no session has been booted yet
   * (e.g. boot is still in flight after app start, or boot failed).
   */
  getOrchestratorSession(projectName: string) {
    return this.http.get<OrchestratorSessionResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-session`,
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
      `${this.baseUrl}/runner/global/orchestrator-session`,
    );
  }

  // Cycle 10d: token-aggregate endpoints moved to TokensApiService
  // (`features/tokens/services/`). Caller migration:
  // `inject(TokensApiService)` instead of `inject(JobService)` + the
  // method names stay identical. Methods covered: getTokenSummary,
  // getTokenSummaryAggregate, getTokenSummaryAggregateCached,
  // getAdHocUsage, getWorkspaceTokensTimeline.

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
      { params },
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
    return this.http.get<WorkspaceScreenshotsResponse>(`${this.baseUrl}/workspace/screenshots`, {
      params,
    });
  }

  /**
   * Read the per-project orchestrator chat log: turns between the user
   * and the global orchestrator session, scoped to one project tab.
   * Backed by `<watchPath>/.orchestrator/orchestrator-chat.jsonl`.
   */
  getOrchestratorChat(projectName: string) {
    return this.http.get<OrchestratorChatResponse>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-chat`,
    );
  }

  /**
   * Send a user message to the project's orchestrator chat. The backend
   * resumes the global orchestrator session, persists both user and
   * orchestrator turns, and returns the orchestrator's reply turn.
   */
  sendOrchestratorChat(
    projectName: string,
    body: {
      text: string;
      attachments?: {
        alt: string;
        relativePath: string;
        inlineBase64?: string | null;
        mimeType?: string | null;
      }[];
      navigationContext?: import('../features/orchestrator').ChatNavigationContext | null;
    },
  ) {
    return this.http.post<{ project: string; reply: OrchestratorChatTurn }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-chat`,
      body,
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
      form,
    );
  }

  /**
   * Slice D scroll cursor: returns up to `limit` turns whose `ts` is
   * strictly before / strictly after the anchor. Cursor is the ISO
   * timestamp of the boundary turn already in the list. With no anchor
   * it returns the most recent N (reverse-chronological), which is
   * what the FE wants for the initial load.
   */
  scrollProjectChat(
    projectName: string,
    opts: { before?: string; after?: string; limit?: number },
  ) {
    let params = new HttpParams();
    if (opts.before) params = params.set('before', opts.before);
    if (opts.after) params = params.set('after', opts.after);
    if (opts.limit != null) params = params.set('limit', String(opts.limit));
    return this.http.get<ProjectChatScrollResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/chat/scroll`,
      { params },
    );
  }

  /**
   * BM25-ranked FTS5 search over the per-project chat history. Returns
   * `<b>...</b>`-marked snippets that are HTML-encoded except for the
   * marker tags; the caller renders them as `[innerHTML]` after
   * mapping the markers to `<mark>`.
   */
  searchProjectChat(projectName: string, query: string, limit = 20) {
    const params = new HttpParams().set('q', query).set('limit', String(limit));
    return this.http.get<ProjectChatSearchResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/chat/search`,
      { params },
    );
  }

  /**
   * Per-project chat stats: total message count, oldest / newest ts.
   * Drives the step-load panel's "viewing N of M, going back to …" line.
   */
  getProjectChatStats(projectName: string) {
    return this.http.get<ProjectChatStatsResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/chat/stats`,
    );
  }

  /**
   * Fetch a single chat turn's full body + frontmatter — used after a
   * search-result click to scroll the live list to that turn.
   */
  getProjectChatTurn(projectName: string, turnId: string) {
    return this.http.get<ProjectChatTurnResponse>(
      `${this.baseUrl}/projects/${encodeURIComponent(projectName)}/chat/turn/${encodeURIComponent(turnId)}`,
    );
  }

  /**
   * One-shot Haiku call that turns a free-text prompt into a short
   * imperative English title. Drives the "Generate" button on the
   * Create-task dialog. Returns immediately when the prompt is empty.
   */
  generateTaskTitle(prompt: string) {
    return this.http.post<{ title: string }>(`${this.baseUrl}/title/generate`, { prompt });
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
      { prompt },
    );
  }

  /**
   * Override an orchestrator decision. Appends an intervention entry to
   * the feed, and (when `jobId` is provided) routes `newDirection`
   * through the Continue path as a Steer-mode follow-up.
   */
  overrideOrchestratorEntry(
    projectName: string,
    body: { originalTs: string; jobId: string; newDirection: string },
  ) {
    return this.http.post<{ applied: boolean; error?: string; note?: string }>(
      `${this.baseUrl}/runner/${encodeURIComponent(projectName)}/orchestrator-log/override`,
      body,
    );
  }

  setRunnerMode(projectName: string, mode: string) {
    return this.http.put(`${this.baseUrl}/runner/${encodeURIComponent(projectName)}/mode`, {
      mode,
    });
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
            source: 'Runner status',
          });
        }
      },
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
