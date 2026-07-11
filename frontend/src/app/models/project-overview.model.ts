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
  lastDeployment: {
    at: string;
    status: string;
    headBefore: string;
    headAfter: string;
    durationSeconds: number;
    jobsSinceLastRestart: number;
    reviewCountAfter: number;
    commits: ProjectDeploymentCommit[];
  } | null;
  pendingCount: number | null;
  pendingCommits: ProjectDeploymentCommit[];
}
