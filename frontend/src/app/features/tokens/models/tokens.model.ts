/**
 * Cycle 9 tokens feature models. Lifted out of `models/job.model.ts`
 * per ADR-0034. Re-exported from the legacy file so existing imports
 * keep working; new code should import from this feature folder.
 *
 * Covers per-job token summaries, per-project + workspace rollups,
 * timeline buckets, and ad-hoc Haiku CLI usage. Token-cost lives
 * here too because it's always paired with the rollups it explains.
 */

export interface JobTokenCall {
  ts: string;
  model: string | null;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
}

export interface JobTokenSummary {
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  totalTokens: number;
  lastModel: string | null;
  lastUpdate: string | null;
  entries: JobTokenCall[];
}

export interface TokenSummaryByModel {
  model: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
}

export interface TokenSummaryByProject {
  project: string;
  orchestratorLlmCalls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
}

export interface TokenSummary {
  project: string;
  orchestratorEntries: number;
  orchestratorLlmCalls: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheReadTokens: number;
  totalCacheCreationTokens: number;
  estimatedApiCostUsd: number;
  allModelsPriced: boolean;
  byModel: TokenSummaryByModel[];
  disclaimer: string;
}

/**
 * Workspace-wide rollup of orchestrator tokens + theoretical API cost.
 * Mirrors backend `TokenSummaryAggregate`. Used by the status-bar usage
 * modal to render a single "tokens consumed" number on hover, without
 * forcing the user to pick a project first.
 */
export interface TokenSummaryAggregate {
  projects: number;
  orchestratorEntries: number;
  orchestratorLlmCalls: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  totalCacheReadTokens: number;
  totalCacheCreationTokens: number;
  estimatedApiCostUsd: number;
  allModelsPriced: boolean;
  byModel: TokenSummaryByModel[];
  byProject: TokenSummaryByProject[];
  fetchedAt: string;
  disclaimer: string;
}

/**
 * Workspace-wide rollup of ad-hoc Claude Haiku CLI calls (title-generate,
 * status.md summary, prompt enhance, commit-message, supervisor soft
 * reasoning, etc.). These calls live outside the per-project orchestrator
 * log; the status-bar usage modal renders this aggregate in its own
 * section so the user can see the ambient Haiku spend the orchestrator
 * incurs on top of the main pipeline. Mirrors backend
 * `AdHocUsageAggregate`.
 */
export interface AdHocUsageAggregate {
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  allModelsPriced: boolean;
  bySource: AdHocUsageBySource[];
  byDay: AdHocUsageByDay[];
  byModel: AdHocUsageByModel[];
  logPath: string;
  logSizeBytes: number;
  logModifiedAt: string | null;
  disclaimer: string;
}

export interface AdHocUsageBySource {
  source: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
}

export interface AdHocUsageByDay {
  date: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
}

export interface AdHocUsageByModel {
  model: string;
  calls: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  estimatedApiCostUsd: number;
  modelPriced: boolean;
}

/**
 * Workspace token-usage timeline. Mirrors backend `TokenTimeline`.
 * Powers the workspace token view at `#/workspace/tokens`. Each cell is
 * a (project, time-bucket) datum that the chart stacks on the y axis.
 */
export interface TokenTimeline {
  windowStart: string;
  windowEnd: string;
  windowHours: number;
  bucketMinutes: number;
  bucketCount: number;
  cells: TokenTimelineCell[];
  projects: TokenTimelineProject[];
  fetchedAt: string;
  disclaimer: string;
}

export interface TokenTimelineCell {
  project: string;
  bucketStart: string;
  bucketEnd: string;
  calls: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  total: number;
  dollars: number | null;
  allModelsPriced: boolean;
}

export interface TokenTimelineProject {
  project: string;
  calls: number;
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  total: number;
  dollars: number | null;
  allModelsPriced: boolean;
  peakBucketStart: string | null;
  peakBucketTotal: number;
  lastActivity: string | null;
}

export interface WorkspaceExpensiveJobsResponse {
  jobs: WorkspaceExpensiveJob[];
}

export interface WorkspaceExpensiveJob {
  project: string;
  jobId: string;
  title: string;
  state: string | null;
  category: string;
  totalTokens: number;
  calls: number;
  lastActivity: string | null;
  lastModel: string | null;
}
