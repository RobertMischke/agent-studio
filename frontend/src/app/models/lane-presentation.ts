import { TaskState, type TaskStateKey } from './task.model';

/** User-facing identity for one canonical workflow lane. */
export interface LanePresentation {
  readonly displayName: string;
  readonly shortName: string;
  readonly sentence: string;
  readonly toneToken: `--studio-lane-${string}`;
  readonly glyph: string;
  readonly docTopic: `lane-${string}`;
}

/**
 * The single presentation source for task lanes. Components may decide where
 * to place a name, glyph, sentence, or tone, but never define those values.
 */
export const LANE_PRESENTATIONS: Readonly<Record<TaskStateKey, LanePresentation>> = {
  [TaskState.Backlog]: {
    displayName: 'Backlog',
    shortName: 'Backlog',
    sentence: 'Captured but not yet scheduled',
    toneToken: '--studio-lane-backlog',
    glyph: '🗒️',
    docTopic: 'lane-0-backlog',
  },
  [TaskState.Preparation]: {
    displayName: 'In Preparation',
    shortName: 'Preparation',
    sentence: 'Intake and preparation before the task is workable',
    toneToken: '--studio-lane-preparation',
    glyph: '📋',
    docTopic: 'lane-1-preparation',
  },
  [TaskState.OrchestratorPrep]: {
    displayName: 'Orchestrator Prep',
    shortName: 'Orchestrator Prep',
    sentence: 'Orchestrator preparation is running',
    toneToken: '--studio-lane-orchestrator-prep',
    glyph: '🛂',
    docTopic: 'lane-1a-orchestrator-prep',
  },
  [TaskState.Ready]: {
    displayName: 'Ready',
    shortName: 'Ready',
    sentence: 'Queued and ready for pickup',
    toneToken: '--studio-lane-ready',
    glyph: '📦',
    docTopic: 'lane-2-ready',
  },
  [TaskState.Progress]: {
    displayName: 'In Progress',
    shortName: 'In Progress',
    sentence: 'A run is executing the task',
    toneToken: '--studio-lane-progress',
    glyph: '🔵',
    docTopic: 'lane-3-progress',
  },
  [TaskState.FailedPickup]: {
    displayName: 'Failed pickup',
    shortName: 'Failed pickup',
    sentence: 'Pickup failed before a run could start',
    toneToken: '--studio-lane-failed-pickup',
    glyph: '⛔',
    docTopic: 'lane-3a-failed-pickup',
  },
  [TaskState.CodeNotComplete]: {
    displayName: 'Code not complete',
    shortName: 'Code not complete',
    sentence: 'The run ended before the code was complete',
    toneToken: '--studio-lane-code-not-complete',
    glyph: '🚧',
    docTopic: 'lane-3b-code-not-complete',
  },
  [TaskState.AutoReview]: {
    displayName: 'Post Processing',
    shortName: 'Post Processing',
    sentence: 'Automated review gates are running',
    toneToken: '--studio-lane-auto-review',
    glyph: '🤖',
    docTopic: 'lane-4-auto-review',
  },
  [TaskState.Escalated]: {
    displayName: 'Escalated',
    shortName: 'Escalated',
    sentence: 'Waiting for operator attention',
    toneToken: '--studio-lane-escalated',
    glyph: '⚠️',
    docTopic: 'lane-5e-escalated',
  },
  [TaskState.HumanReview]: {
    displayName: 'Human review',
    shortName: 'Human review',
    sentence: 'Waiting for a human decision',
    toneToken: '--studio-lane-human-review',
    glyph: '👁️',
    docTopic: 'lane-5-human-review',
  },
  [TaskState.Completed]: {
    displayName: 'Delivered',
    shortName: 'Delivered',
    sentence: 'Delivered and accepted',
    toneToken: '--studio-lane-completed',
    glyph: '🟢',
    docTopic: 'lane-6-completed',
  },
  [TaskState.Archive]: {
    displayName: 'Archive',
    shortName: 'Archive',
    sentence: 'Archived outside the active workflow',
    toneToken: '--studio-lane-archive',
    glyph: '🗄️',
    docTopic: 'lane-7-archive',
  },
};

const PRESENTATION_ALIASES: Readonly<Record<string, LanePresentation>> = {
  '2-ready-intake': {
    ...LANE_PRESENTATIONS[TaskState.OrchestratorPrep],
    displayName: 'Preparation',
    shortName: 'Preparation',
    docTopic: 'lane-2-ready',
  },
  '4-review': LANE_PRESENTATIONS[TaskState.AutoReview],
  '5-completed': LANE_PRESENTATIONS[TaskState.Completed],
  '6-archive': LANE_PRESENTATIONS[TaskState.Archive],
  '1b-needs-human-review': LANE_PRESENTATIONS[TaskState.HumanReview],
};

export function lanePresentation(state: string | null | undefined): LanePresentation | null {
  if (!state) return null;
  return PRESENTATION_ALIASES[state] ?? LANE_PRESENTATIONS[state as TaskStateKey] ?? null;
}

export function laneDisplayName(state: string | null | undefined): string {
  return lanePresentation(state)?.displayName ?? state ?? '';
}

export function laneShortName(state: string | null | undefined): string {
  return lanePresentation(state)?.shortName ?? state ?? '';
}

export function laneToneValue(state: string | null | undefined): string | null {
  const token = lanePresentation(state)?.toneToken;
  return token ? `var(${token})` : null;
}
