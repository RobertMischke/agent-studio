/**
 * Cycle 9 git feature models. Lifted out of the kitchen-sink
 * `models/job.model.ts` per ADR-0034 + the architecture review. The
 * legacy file re-exports these so existing imports keep working;
 * new code should import from `features/git/models/git.model`
 * directly so the feature boundary stays visible.
 */

export interface GitFileChange {
  status: string;
  path: string;
  added: number;
  removed: number;
}

export interface GitStatus {
  isRepo: boolean;
  branch: string | null;
  filesChanged: number;
  totalAdded: number;
  totalRemoved: number;
  files: GitFileChange[];
  error: string | null;
}

export interface GitProjectSummary {
  projectName: string;
  rootPath: string;
  isRepo: boolean;
  branch: string | null;
  filesChanged: number;
  totalAdded: number;
  totalRemoved: number;
}

/**
 * Repository hygiene snapshot. Mirrors backend `GitHygieneStatus`.
 *
 * Used by:
 *  - the project header dirty/unpushed badge (project-level fields only),
 *  - the job-detail review/completed hygiene strip (with the `job` overlay).
 *
 * Fetched from `GET /api/git/hygiene?project=<name>` (project) and
 * `GET /api/jobs/{id}/git/hygiene` (job). Both endpoints cache server-side
 * for ~3 s.
 */
export interface GitHygieneStatus {
  projectName: string;
  repoRoot: string | null;
  isRepo: boolean;
  branch: string | null;
  upstream: string | null;
  hasUpstream: boolean;
  ahead: number;
  behind: number;
  isDirty: boolean;
  stagedCount: number;
  unstagedCount: number;
  untrackedCount: number;
  lastCommitSha: string | null;
  lastCommitShortSha: string | null;
  lastCommitSubject: string | null;
  lastCommitAtUtc: string | null;
  job: JobHygieneContext | null;
  error: string | null;
}

export interface JobHygieneContext {
  jobId: string;
  state: string;
  jobInfoCommitPresent: boolean;
  stampedCommitSha: string | null;
  acceptedTaskUncommitted: boolean;
  commitUnpushed: boolean;
}

export interface JobCommitInfo {
  sha: string;
  shortSha: string;
  message: string;
  filesChanged: number;
  files: string[];
  at: string;
}

export interface JobCommitDetail {
  commit: JobCommitInfo | null;
  files: GitFileChange[];
}
