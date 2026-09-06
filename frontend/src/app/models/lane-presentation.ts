import { ALL_TASK_STATES, TaskState, type TaskStateKey } from './task.model';

/** One canonical, theme-aware presentation for a workflow lane. */
export interface LanePresentation {
  readonly displayName: string;
  readonly shortName: string;
  readonly sentence: string;
  readonly toneToken: `--studio-lane-${string}`;
  readonly glyph: string;
  readonly docTopic: `lane-${string}`;
}

/**
 * The only source of user-facing lane names, glyphs, tones, and help topics.
 * Transport keys remain in {@link TaskState}; every visual projection reads
 * this map instead of translating those keys independently.
 */
export const LANE_PRESENTATIONS = {
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
    sentence: 'Being prepared before it is ready for pickup',
    toneToken: '--studio-lane-preparation',
    glyph: '📋',
    docTopic: 'lane-1-preparation',
  },
  [TaskState.OrchestratorPrep]: {
    displayName: 'Orchestrator Prep',
    shortName: 'Orchestrator Prep',
    sentence: 'Being prepared by the orchestrator',
    toneToken: '--studio-lane-preparation',
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
    sentence: 'Pickup failed and needs recovery',
    toneToken: '--studio-lane-failed',
    glyph: '↩️',
    docTopic: 'lane-3a-failed-pickup',
  },
  [TaskState.CodeNotComplete]: {
    displayName: 'Code not complete',
    shortName: 'Code incomplete',
    sentence: 'The run ended before the code was complete',
    toneToken: '--studio-lane-failed',
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
    sentence: 'Escalated for operator attention',
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
} as const satisfies Readonly<Record<TaskStateKey, LanePresentation>>;

const VIRTUAL_LANE_PRESENTATIONS: Readonly<Record<string, LanePresentation>> = {
  // A visual split of Ready, not a transport state. Keep its established
  // identity here so the board does not need a one-off title, glyph, or topic.
  '2-ready-intake': {
    displayName: 'Preparation',
    shortName: 'Preparation',
    sentence: 'Being prepared before it is ready for pickup',
    toneToken: '--studio-lane-preparation',
    glyph: '🛂',
    docTopic: 'lane-2-ready',
  },
};

const PRESENTATION_ALIASES: Readonly<Record<string, TaskStateKey>> = {
  '4-review': TaskState.AutoReview,
  '5-completed': TaskState.Completed,
  '6-archive': TaskState.Archive,
};

/** Resolve canonical and compatibility lane keys at one boundary. */
export function lanePresentation(state: string | null | undefined): LanePresentation | null {
  if (!state) return null;
  const virtual = VIRTUAL_LANE_PRESENTATIONS[state];
  if (virtual) return virtual;
  const canonical = PRESENTATION_ALIASES[state] ?? state;
  return LANE_PRESENTATIONS[canonical as TaskStateKey] ?? null;
}

export function laneDisplayName(state: string | null | undefined): string {
  return lanePresentation(state)?.displayName ?? state ?? '';
}

export function laneShortName(state: string | null | undefined): string {
  return lanePresentation(state)?.shortName ?? state ?? '';
}

/** A CSS value suitable for assigning to a local custom property. */
export function laneToneValue(state: string | null | undefined): string {
  const token = lanePresentation(state)?.toneToken;
  return token ? `var(${token})` : 'var(--studio-fg-muted)';
}

/** Exposed for structural tests without duplicating the canonical key list. */
export const PRESENTED_TASK_STATES: readonly TaskStateKey[] = ALL_TASK_STATES;
