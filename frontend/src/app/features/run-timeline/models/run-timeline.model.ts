/**
 * Cycle 9 run-timeline feature models. Lifted out of `models/job.model.ts`
 * per ADR-0034. Re-exported from the legacy file.
 *
 * RunTimeline is the per-job ordered list of CLI invocations between
 * user inputs (start / continue / recovery / restart). Backend lives
 * in backend/Services/Runner/RunTimeline.cs. lineStart / lineEnd are
 * 1-based indices into cli-output.log so the drill-down activity-log
 * filter does not have to re-derive the boundaries.
 */

export interface RunRecord {
  index: number;
  intent: string; // 'start' | 'continue' | 'recovery' | 'restart'
  startedAt: string;
  endedAt: string | null;
  status: string; // 'running' | 'completed' | 'failed' | 'cancelled' | 'unknown'
  cli: string | null;
  exitCode: number | null;
  durationSeconds: number | null;
  inputSessionId: string | null;
  capturedSessionId: string | null;
  resumed: boolean;
  reason: string | null;
  userFollowup: string | null;
  lineStart: number | null;
  lineEnd: number | null;
  /** HEAD SHA captured immediately before the run's CLI started, or null when the project has no repo / git was unavailable. */
  headShaBefore: string | null;
  /** HEAD SHA after the run finished. Equal to headShaBefore when the agent did not commit. */
  headShaAfter: string | null;
  /**
   * Relative path (under the job folder) to the captured context this run
   * was started with. Non-null means the "Show passed context" affordance is
   * offered; the full text is fetched on demand from
   * `/api/tasks/{id}/runs/{index}/context`, never inlined in the polled list.
   */
  contextRef: string | null;
}

/** Response of `GET /api/tasks/{id}/runs/{index}/context`. `context` is null when nothing was captured for the run. */
export interface RunContextResponse {
  runIndex: number;
  context: string | null;
  note?: string;
}

export interface RunTimeline {
  runCount: number;
  firstStartedAt: string | null;
  lastActivityAt: string | null;
  hasActiveRun: boolean;
  runs: RunRecord[];
}

export interface RunCommitInfo {
  sha: string;
  shortSha: string;
  authorDateUtc: string;
  author: string;
  subject: string;
  filesChanged: number;
  added: number;
  removed: number;
}

export interface RunCommitsResponse {
  runIndex: number;
  startedAt: string;
  endedAt: string | null;
  headShaBefore: string | null;
  headShaAfter: string | null;
  /** 'sha-range' (deterministic) | 'wall-clock' (fallback for older runs without captured SHAs). */
  source: 'sha-range' | 'wall-clock';
  commits: RunCommitInfo[];
}

/**
 * One row in the per-run aggregated file list. `status` is the
 * single-letter git diff filter (A/M/D/R/C). The +/- counts are the
 * combined numstat across every commit in the run that touched this
 * path. Used by the Run Git Viewer's file tree.
 */
export interface RunFileChange {
  status: string;
  path: string;
  added: number;
  removed: number;
}

export interface RunFilesResponse {
  runIndex: number;
  headShaBefore: string | null;
  headShaAfter: string | null;
  files: RunFileChange[];
  note?: string;
}

export interface RunDiffResponse {
  diff: string;
  note?: string;
}
