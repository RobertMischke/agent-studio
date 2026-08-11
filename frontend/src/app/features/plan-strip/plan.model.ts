/**
 * Per-job task-plan view that drives the plan strip inside Activity and the
 * orchestrator task context. Mirrors the backend `TaskPlanView` DTO: the
 * agent's own TodoWrite / update_plan / todo_list items, with sub-actions
 * derived at read time by replaying `logs/plan-snapshots.jsonl` against
 * `logs/tool-calls.jsonl`. Read-only, no model call. See
 * docs/mockups/task-progress-tracking.
 */
export interface TaskPlanView {
  hasPlan: boolean;
  source: string | null;
  snapshotCount: number;
  activeItemId: string | null;
  /** Median sub-action count across already-done items; null below two samples. */
  softEstimateMedian: number | null;
  items: TaskPlanItemView[];
  /** Tool calls that fired before the first plan landed ("before plan" bucket). */
  unassignedSubActions: TaskPlanSubAction[];
}

export interface TaskPlanItemView {
  id: string;
  title: string;
  status: PlanItemStatus;
  subActionCount: number;
  subActions: TaskPlanSubAction[];
}

export interface TaskPlanSubAction {
  ts: string;
  tool: string;
  label: string | null;
}

export type PlanItemStatus = 'pending' | 'active' | 'done';
