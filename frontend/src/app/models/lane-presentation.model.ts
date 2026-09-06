import { ALL_TASK_STATES, TaskState, type TaskStateKey } from './task.model';

/** User-facing identity for one canonical workflow lane. */
export interface LanePresentation {
  readonly state: TaskStateKey;
  readonly name: string;
  readonly shortName: string;
  readonly sentence: string;
  readonly toneToken: `--studio-lane-${string}`;
  readonly glyph: string;
  readonly docTopic: string;
}

/**
 * The only source for workflow-lane names, copy, tone, glyphs, and help topics.
 * Protocol/grouping keys such as `humanReview` remain transport concerns and
 * must be translated through a TaskState before reaching presentation code.
 */
export const LANE_PRESENTATIONS: Readonly<Record<TaskStateKey, LanePresentation>> = {
  [TaskState.Backlog]: lane(TaskState.Backlog, 'Backlog', 'Backlog', 'Captured but not yet scheduled.', '--studio-lane-backlog', '🗒️', 'lane-0-backlog'),
  [TaskState.Preparation]: lane(TaskState.Preparation, 'In Preparation', 'Preparation', 'Waiting for preparation before the task is workable.', '--studio-lane-preparation', '📋', 'lane-1-preparation'),
  [TaskState.OrchestratorPrep]: lane(TaskState.OrchestratorPrep, 'Orchestrator Prep', 'Orchestrator Prep', 'Waiting for orchestrator preparation.', '--studio-lane-orchestrator-prep', '🛂', 'lane-1a-orchestrator-prep'),
  [TaskState.Ready]: lane(TaskState.Ready, 'Ready', 'Ready', 'Queued and ready for pickup.', '--studio-lane-ready', '📦', 'lane-2-ready'),
  [TaskState.Progress]: lane(TaskState.Progress, 'In Progress', 'In Progress', 'A run is executing the task.', '--studio-lane-progress', '🔵', 'lane-3-progress'),
  [TaskState.FailedPickup]: lane(TaskState.FailedPickup, 'Failed Pickup', 'Failed Pickup', 'Pickup failed and requires recovery.', '--studio-lane-failed-pickup', '⛔', 'lane-3-progress'),
  [TaskState.CodeNotComplete]: lane(TaskState.CodeNotComplete, 'Code not complete', 'Code not complete', 'The run stopped before the code was complete.', '--studio-lane-code-not-complete', '🚧', 'lane-3-progress'),
  [TaskState.AutoReview]: lane(TaskState.AutoReview, 'Post Processing', 'Post Processing', 'Automated review gates are running.', '--studio-lane-auto-review', '🤖', 'lane-4-auto-review'),
  [TaskState.Escalated]: lane(TaskState.Escalated, 'Escalated', 'Escalated', 'Waiting for operator attention.', '--studio-lane-escalated', '⚠️', 'lane-5e-escalated'),
  [TaskState.HumanReview]: lane(TaskState.HumanReview, 'Human review', 'Human review', 'Waiting for a human decision.', '--studio-lane-human-review', '👁️', 'lane-5-human-review'),
  [TaskState.Completed]: lane(TaskState.Completed, 'Delivered', 'Delivered', 'Delivered and accepted.', '--studio-lane-completed', '🟢', 'lane-6-completed'),
  [TaskState.Archive]: lane(TaskState.Archive, 'Archive', 'Archive', 'Archived outside the active workflow.', '--studio-lane-archive', '🗄️', 'lane-7-archive'),
};

export const PRESENTED_TASK_STATES: readonly TaskStateKey[] = ALL_TASK_STATES;

const PRESENTATION_ALIASES: Readonly<Record<string, TaskStateKey>> = {
  '2-ready-intake': TaskState.OrchestratorPrep,
  '4-review': TaskState.AutoReview,
  '5-completed': TaskState.Completed,
  '6-archive': TaskState.Archive,
  '1b-needs-human-review': TaskState.HumanReview,
};

/** Resolve a canonical or compatibility lane state to its presentation. */
export function lanePresentation(state: string | null | undefined): LanePresentation | null {
  if (!state) return null;
  const canonical = (PRESENTATION_ALIASES[state] ?? state) as TaskStateKey;
  return LANE_PRESENTATIONS[canonical] ?? null;
}

export function laneName(state: string | null | undefined): string {
  return lanePresentation(state)?.name ?? state ?? '';
}

export function laneShortName(state: string | null | undefined): string {
  return lanePresentation(state)?.shortName ?? state ?? '';
}

export function laneTone(state: string | null | undefined): string | null {
  const token = lanePresentation(state)?.toneToken;
  return token ? `var(${token})` : null;
}

function lane(
  state: TaskStateKey,
  name: string,
  shortName: string,
  sentence: string,
  toneToken: LanePresentation['toneToken'],
  glyph: string,
  docTopic: string,
): LanePresentation {
  return { state, name, shortName, sentence, toneToken, glyph, docTopic };
}
