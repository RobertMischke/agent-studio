/** Compact project throughput used by the operator-first Project Overview. */
export interface ProjectThroughputSummary {
  project: string;
  capturedAt: string;
  completedLast24h: number;
  completedLast7d: number;
  recentCompletions: ProjectCompletionSummary[];
}

export interface ProjectCompletionSummary {
  taskId: string;
  taskKey: string;
  title: string;
  completedAt: string;
}

/** One commit included in, or waiting for, the stable deployment. */
export interface ProjectDeploymentCommit {
  sha: string;
  shortSha: string;
  subject: string;
  authorDateUtc: string;
}

/** Latest deploy-stable run plus the current integration-to-deployed delta. */
export interface ProjectDeploymentSummary {
  project: string;
  available: boolean;
  reason: string | null;
  source: string;
  lastDeployment: ProjectDeploymentRun | null;
  history: ProjectDeploymentRun[];
  pendingCount: number | null;
  pendingCommits: ProjectDeploymentCommit[];
}

export interface ProjectDeploymentRun {
    at: string;
    status: string;
    headBefore: string;
    headAfter: string;
    durationSeconds: number;
    jobsSinceLastRestart: number;
    reviewCountAfter: number;
    commits: ProjectDeploymentCommit[];
}

export type ProjectVisualEvidenceReviewStatus = 'unseen' | 'reviewed' | 'unavailable';

export interface ProjectVisualEvidenceItem {
  id: string;
  jobId: string;
  jobTitle: string;
  watchPath: string;
  fileName: string;
  relativePath: string;
  url: string | null;
  caption: string;
  testStatus: string | null;
  source: string;
  capturedAt: string;
  reviewStatus: ProjectVisualEvidenceReviewStatus;
}

export interface ProjectVisualEvidenceQueue {
  project: string;
  capturedAt: string;
  unseenCount: number;
  items: ProjectVisualEvidenceItem[];
}
