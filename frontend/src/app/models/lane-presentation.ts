import { ALL_TASK_STATES, TaskState, type TaskStateKey } from './task.model';

/** Visual and verbal identity for one canonical task lane. */
export interface LanePresentation {
  state: TaskStateKey;
  /** Full lane name used by board columns and result headings. */
  name: string;
  /** Compact form used by chips and lane selectors. */
  shortName: string;
  /** One-line description of what it means for a task to be in this lane. */
  sentence: string;
  /** Semantic CSS custom property shared by every lane-coloured surface. */
  toneToken: `--studio-lane-${string}`;
  glyph: string;
  docTopic: `lane-${string}`;
  /** Property used by the grouped-task API response for this lane. */
  groupKey: string;
}

/**
 * The only authored lane presentation catalogue.
 *
 * State keys remain protocol data in task.model.ts. Every user-facing lane
 * name, explanation, glyph, tone, help topic, and grouped-response key lives
 * here so board, task detail, Result, and project workflow cannot drift.
 */
export const LANE_PRESENTATIONS = {
  [TaskState.Backlog]: {
    state: TaskState.Backlog,
    name: 'Backlog',
    shortName: 'Backlog',
    sentence: 'Waiting for triage',
    toneToken: '--studio-lane-backlog',
    glyph: '🗒️',
    docTopic: 'lane-0-backlog',
    groupKey: 'backlog',
  },
  [TaskState.Preparation]: {
    state: TaskState.Preparation,
    name: 'In Preparation',
    shortName: 'Preparation',
    sentence: 'Being prepared before the task is ready to run',
    toneToken: '--studio-lane-preparation',
    glyph: '📋',
    docTopic: 'lane-1-preparation',
    groupKey: 'preparation',
  },
  [TaskState.OrchestratorPrep]: {
    state: TaskState.OrchestratorPrep,
    name: 'Orchestrator Prep',
    shortName: 'Orchestrator Prep',
    sentence: 'Being prepared by the orchestrator',
    toneToken: '--studio-lane-preparation',
    glyph: '⚙️',
    docTopic: 'lane-1a-orchestrator-prep',
    groupKey: 'orchestratorPrep',
  },
  [TaskState.Ready]: {
    state: TaskState.Ready,
    name: 'Ready',
    shortName: 'Ready',
    sentence: 'Waiting for runner pickup',
    toneToken: '--studio-lane-ready',
    glyph: '📦',
    docTopic: 'lane-2-ready',
    groupKey: 'ready',
  },
  [TaskState.Progress]: {
    state: TaskState.Progress,
    name: 'In Progress',
    shortName: 'In Progress',
    sentence: 'Running the task',
    toneToken: '--studio-lane-progress',
    glyph: '🔵',
    docTopic: 'lane-3-progress',
    groupKey: 'progress',
  },
  [TaskState.FailedPickup]: {
    state: TaskState.FailedPickup,
    name: 'Failed pickup',
    shortName: 'Failed pickup',
    sentence: 'Waiting after a failed runner pickup',
    toneToken: '--studio-lane-failed-pickup',
    glyph: '⚠️',
    docTopic: 'lane-3a-failed-pickup',
    groupKey: 'failedPickup',
  },
  [TaskState.CodeNotComplete]: {
    state: TaskState.CodeNotComplete,
    name: 'Code not complete',
    shortName: 'Code not complete',
    sentence: 'Waiting after the implementation retry budget was exhausted',
    toneToken: '--studio-lane-code-not-complete',
    glyph: '🚧',
    docTopic: 'lane-3b-code-not-complete',
    groupKey: 'codeNotComplete',
  },
  [TaskState.AutoReview]: {
    state: TaskState.AutoReview,
    name: 'Post Processing',
    shortName: 'Post Processing',
    sentence: 'Running automated review gates',
    toneToken: '--studio-lane-auto-review',
    glyph: '🤖',
    docTopic: 'lane-4-auto-review',
    groupKey: 'autoReview',
  },
  [TaskState.Escalated]: {
    state: TaskState.Escalated,
    name: 'Escalated',
    shortName: 'Escalated',
    sentence: 'Waiting for operator attention',
    toneToken: '--studio-lane-escalated',
    glyph: '⚠️',
    docTopic: 'lane-5e-escalated',
    groupKey: 'escalated',
  },
  [TaskState.HumanReview]: {
    state: TaskState.HumanReview,
    name: 'Human review',
    shortName: 'Human review',
    sentence: 'Waiting for a human decision',
    toneToken: '--studio-lane-human-review',
    glyph: '👁️',
    docTopic: 'lane-5-human-review',
    groupKey: 'humanReview',
  },
  [TaskState.Completed]: {
    state: TaskState.Completed,
    name: 'Delivered',
    shortName: 'Delivered',
    sentence: 'Delivered and accepted',
    toneToken: '--studio-lane-completed',
    glyph: '🟢',
    docTopic: 'lane-6-completed',
    groupKey: 'completed',
  },
  [TaskState.Archive]: {
    state: TaskState.Archive,
    name: 'Archive',
    shortName: 'Archive',
    sentence: 'Archived outside the active workflow',
    toneToken: '--studio-lane-archive',
    glyph: '🗄️',
    docTopic: 'lane-7-archive',
    groupKey: 'archive',
  },
} as const satisfies Record<TaskStateKey, LanePresentation>;

const COMPATIBILITY_STATES: Readonly<Record<string, TaskStateKey>> = {
  '2-ready-intake': TaskState.Preparation,
  '4-review': TaskState.AutoReview,
  '5-completed': TaskState.Completed,
  '6-archive': TaskState.Archive,
  '1b-needs-human-review': TaskState.HumanReview,
};

export const LANE_ORDER: readonly TaskStateKey[] = ALL_TASK_STATES;

export function lanePresentation(state: string | null | undefined): LanePresentation | null {
  if (!state) return null;
  const canonical = COMPATIBILITY_STATES[state] ?? state;
  return LANE_PRESENTATIONS[canonical as TaskStateKey] ?? null;
}

export function laneName(state: string | null | undefined): string {
  return lanePresentation(state)?.name ?? state ?? '';
}

export function laneShortName(state: string | null | undefined): string {
  return lanePresentation(state)?.shortName ?? state ?? '';
}

export function laneToneValue(state: string | null | undefined): string | null {
  const token = lanePresentation(state)?.toneToken;
  return token ? `var(${token})` : null;
}
