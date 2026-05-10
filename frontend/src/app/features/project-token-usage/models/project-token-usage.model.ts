/**
 * Cycle 9 project-token-usage feature models. Lifted out of
 * `models/job.model.ts` per ADR-0034. Re-exported from the legacy file.
 *
 * Slice 8 of the quality-system mockup (docs/mockups/quality-system/).
 * Mirrors backend `ProjectTokenUsageSummary`, `ProjectTokenHeatmap`,
 * `ProjectExpensiveJobsResponse`, `ProjectJobTokenDetail`. The category
 * split (`job` / `supporting` / `orchestrator`) follows taxonomy.md.
 */

export type ProjectTokenCategory = 'job' | 'supporting' | 'orchestrator';

export interface ProjectTokenUsageSummary {
  project: string;
  hasData: boolean;
  lifetimeTotalTokens: number;
  lifetimeJobTokens: number;
  lifetimeSupportingTokens: number;
  lifetimeOrchestratorTokens: number;
  lifetimeCalls: number;
  last24hTotalTokens: number;
  last24hJobTokens: number;
  last24hSupportingTokens: number;
  last24hOrchestratorTokens: number;
  last24hCalls: number;
  firstActivity: string | null;
  lastActivity: string | null;
  fetchedAt: string;
  disclaimer: string;
}

export interface ProjectTokenHeatmapCell {
  day: string;
  total: number;
}

export interface ProjectTokenHeatmapJob {
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  total: number;
  calls: number;
  lastActivity: string | null;
  cells: ProjectTokenHeatmapCell[];
}

export interface ProjectTokenHeatmap {
  project: string;
  days: string[];
  jobs: ProjectTokenHeatmapJob[];
  hasData: boolean;
  fetchedAt: string;
}

export interface ProjectExpensiveJob {
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  totalTokens: number;
  calls: number;
  lastActivity: string | null;
  lastModel: string | null;
}

export interface ProjectExpensiveJobsResponse {
  project: string;
  jobs: ProjectExpensiveJob[];
}

export interface ProjectJobTokenRun {
  index: number;
  ts: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  total: number;
  deltaVsPrev: number | null;
  topic: string | null;
  summary: string | null;
}

export interface ProjectJobTokenDetail {
  project: string;
  jobId: string;
  title: string;
  state: string | null;
  category: ProjectTokenCategory;
  totalTokens: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  calls: number;
  firstActivity: string | null;
  lastActivity: string | null;
  lastModel: string | null;
  runs: ProjectJobTokenRun[];
  fetchedAt: string;
}
