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
  completionSource: 'ledger' | 'lane-entry' | 'backfill' | string;
  /** Null for backfilled rows: the reconstructed completion dates the task but explains no stage durations. */
  stages: CycleTimeStageSeconds | null;
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
  /** Backward lane moves (any level drop, including runner lease recovery). */
  backwardTransitions: number;
  /** Present only with `detail=transitions` or on the per-task endpoint. */
  transitions?: TaskLaneTransition[] | null;
}

export type TransitionDirection = 'forward' | 'backward' | 'lateral';

export interface TaskLaneTransition {
  at: string;
  from: string;
  to: string;
  direction: TransitionDirection;
  /** Seconds spent in `from` before this move; null when the stay start is unknown. */
  dwellSeconds: number | null;
  actor: string;
  actorKind: 'runner' | 'review' | 'human' | 'orchestrator' | 'system' | 'external' | string;
  cause: string;
  causeDetail: string | null;
  attemptId: string | null;
  /** Backward moves only: seconds until the task got back to the level it fell from. */
  reworkSeconds: number | null;
}

export interface CycleTimeTransitionCell {
  from: string;
  to: string;
  count: number;
  direction: TransitionDirection;
}

export interface CycleTimeLaneDwell {
  lane: string;
  stays: number;
  p50Seconds: number | null;
  p90Seconds: number | null;
  maxSeconds: number | null;
  totalSeconds: number;
}

export interface CycleTimeBounceCause {
  cause: string;
  label: string;
  count: number;
  tasks: number;
  reworkKnown: number;
  reworkP50Seconds: number | null;
  reworkP90Seconds: number | null;
  reworkTotalSeconds: number;
  details: CycleTimeOutcomeCount[];
}

export interface CycleTimeLoopTask {
  taskId: string;
  taskKey: string;
  title: string;
  watchPath: string;
  backwardTransitions: number;
  leadTimeSeconds: number;
  causes: CycleTimeOutcomeCount[];
}

export interface CycleTimeTransitionSummary {
  totalTransitions: number;
  backwardTransitions: number;
  tasksWithBackwardTransitions: number;
  /** Lanes that occur as source or target, canonical order. */
  lanes: string[];
  cells: CycleTimeTransitionCell[];
  laneDwell: CycleTimeLaneDwell[];
  bounceCauses: CycleTimeBounceCause[];
  topLoops: CycleTimeLoopTask[];
}

export interface ProjectCycleTimeTaskResponse {
  project: string;
  capturedAt: string;
  task: TaskCycleTimeRow;
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
  /** Window tasks whose completion was reconstructed by the backfill sidecar; they enter the lead-time rollup only. */
  tasksBackfilled: number;
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
  transitions: CycleTimeTransitionSummary;
  tasks: TaskCycleTimeRow[];
}
