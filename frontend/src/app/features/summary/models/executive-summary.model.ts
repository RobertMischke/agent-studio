/**
 * Executive-summary feature models. Mirror the backend
 * `ExecutiveSummary` record graph (backend/Services/Runner/
 * ExecutiveSummary.cs) and `docs/system/schemas/executive-summary.schema.json`.
 *
 * Returned by `GET /api/workspace/summary?windowHours=N`. The summary is
 * read-only: every row references a record on disk (a job id, a commit
 * sha, a decision journal line) that the consumer can verify.
 */

export interface ExecutiveSummaryJobMove {
  jobId: string;
  fromState: string;
  toState: string;
  at: string;
}

export interface ExecutiveSummaryCommit {
  sha: string;
  shortSha: string;
  subject: string;
  author: string;
  at: string;
}

export interface ExecutiveSummaryProject {
  project: string;
  jobsMoved: ExecutiveSummaryJobMove[];
  decisionsMade: number;
  advisoriesRaised: number;
  commits: ExecutiveSummaryCommit[];
}

export interface ExecutiveSummaryCrash {
  at: string;
  kind: string;
  path: string;
  summary: string | null;
}

/** `severity` is one of Info | Warn | High | Critical. */
export interface ExecutiveSummaryDecision {
  project: string;
  decisionId: string;
  at: string;
  severity: string;
  title: string;
  jobId: string | null;
}

export interface ExecutiveSummaryOpenDecision {
  project: string;
  jobId: string;
  title: string;
  createdAt: string;
}

export interface ExecutiveSummaryResponse {
  windowStart: string;
  windowEnd: string;
  headline: string;
  byProject: ExecutiveSummaryProject[];
  crashes: ExecutiveSummaryCrash[];
  topDecisions: ExecutiveSummaryDecision[];
  openHumanDecisions: ExecutiveSummaryOpenDecision[];
  schemaVersion: string;
}
