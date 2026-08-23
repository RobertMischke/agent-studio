/**
 * Wire model of `GET /api/projects/{project}/cycle-time?window=7d|30d|all`.
 * Mirrors `backend/Features/Projects/CycleTime/*` records (camelCase on the wire).
 */

export type CycleTimeWindow = '7d' | '30d' | 'all';

export const CYCLE_TIME_WINDOWS: readonly CycleTimeWindow[] = ['7d', '30d', 'all'];

/** Additive lane stages in lane order; the sum equals the lead time of a task. */
export const CYCLE_TIME_STAGE_KEYS = [
  'preparation',
  'queueWait',
  'coding',
  'reviewWait',
  'testGate',
  'reviewOther',
  'integration',
  'humanReview',
  'unattributed',
] as const;

export type CycleTimeStageKey = (typeof CYCLE_TIME_STAGE_KEYS)[number];

export type CycleTimeStageSeconds = Record<CycleTimeStageKey, number>;

export interface TaskCycleTimeRow {
  taskId: string;
  taskKey: string;
  title: string;
  terminalState: string;
  watchPath: string;
  createdAt: string;
  firstClaimedAt: string | null;
  completedAt: string;
  completionSource: 'ledger' | 'lane-entry' | string;
  stages: CycleTimeStageSeconds;
  reviewRunSeconds: number;
  leadTimeSeconds: number;
  cycleTimeSeconds: number | null;
  codingRuns: number;
  reviewRounds: number;
  bounceRounds: number;
  integrationAttempts: number;
  integrationOutcome: string | null;
  integrationStage: string | null;
  dataGaps: string[];
}

export type CycleTimeAggregateKind = 'stage' | 'rollup' | 'count';

export interface CycleTimeAggregate {
  stage: string;
  label: string;
  kind: CycleTimeAggregateKind;
  unit: 'seconds' | 'count';
  highlighted: boolean;
  count: number;
  p50: number | null;
  p90: number | null;
  max: number | null;
  mean: number | null;
  total: number;
}

export interface CycleTimeOutcomeCount {
  outcome: string;
  count: number;
}

export interface ProjectCycleTimeCoverage {
  tasksInProject: number;
  /** Terminal tasks (completed or archived) at any time, regardless of completion evidence. */
  tasksTerminal: number;
  tasksInWindow: number;
  excludedNoCompletionTimestamp: number;
  excludedInFlight: number;
  excludedEpics: number;
  tasksWithoutLedger: number;
  tasksWithLaneEntryCompletion: number;
}

export interface ProjectCycleTimeResponse {
  project: string;
  projectId: string | null;
  shortCode: string | null;
  window: string;
  capturedAt: string;
  since: string | null;
  coverage: ProjectCycleTimeCoverage;
  aggregates: CycleTimeAggregate[];
  integrationOutcomes: CycleTimeOutcomeCount[];
  tasks: TaskCycleTimeRow[];
}
